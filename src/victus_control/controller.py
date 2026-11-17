from __future__ import annotations

from pathlib import Path
import threading
from typing import Any

from victus_control.models import (
    DaemonSettings,
    FanCurve,
    FanMode,
    HardwareCapabilities,
    PowerProfile,
    TelemetrySnapshot,
)
from victus_control.sysfs_backend import LinuxSysfsBackend


class VictusController:
    """Thread-safe orchestrator for profiles, fan modes, temperature curves, and fail-safe shutdown."""

    def __init__(
        self,
        backend: LinuxSysfsBackend,
        settings_path: Path = Path("/var/lib/victus-control/settings.json"),
    ) -> None:
        self._backend = backend
        self._settings_path = settings_path
        self._lock = threading.RLock()

        self.settings = DaemonSettings.load(settings_path)
        self.capabilities: HardwareCapabilities = self._backend.probe_capabilities()

        initial_snap = self._backend.read_telemetry(allow_nvidia_smi=False)
        try:
            self._profile = PowerProfile.from_kernel(initial_snap.platform_profile)
        except Exception:
            self._profile = PowerProfile.BALANCED

        self._fan_mode = self._infer_initial_fan_mode(initial_snap.pwm_enable)
        self._fan_percent = initial_snap.pwm_percent or 0
        self._curve = FanCurve.from_list(self.settings.curve_points)
        self._curve_anchor_temp: float | None = None
        self._curve_applied_percent: int = 0
        self._last_on_battery: bool | None = initial_snap.on_battery

    @staticmethod
    def _infer_initial_fan_mode(pwm_enable: int | None) -> FanMode:
        if pwm_enable == 0:
            return FanMode.MAX
        if pwm_enable == 1:
            return FanMode.MANUAL
        return FanMode.AUTO

    def get_state(self, allow_nvidia_smi: bool = False) -> dict[str, Any]:
        with self._lock:
            allow_gpu_query = allow_nvidia_smi and self._profile is not PowerProfile.ECO
            snap: TelemetrySnapshot = self._backend.read_telemetry(allow_nvidia_smi=allow_gpu_query)
            return {
                "profile": self._profile.value,
                "fan_mode": self._fan_mode.value,
                "fan_percent": self._curve_applied_percent if self._fan_mode is FanMode.CURVE else self._fan_percent,
                "capabilities": self.capabilities.to_dict(),
                "telemetry": snap.to_dict(),
                "settings": self.settings.to_dict(),
            }

    def set_profile(self, profile: PowerProfile | str) -> dict[str, Any]:
        parsed = profile if isinstance(profile, PowerProfile) else PowerProfile.parse(profile)
        with self._lock:
            self._backend.apply_profile(parsed, eco_power_save=self.settings.eco_power_save)
            self._profile = parsed
            # Re-assert non-Auto fan mode if firmware reset it on profile change
            if self._fan_mode is FanMode.MAX:
                self._backend.set_fan_mode(FanMode.MAX)
            elif self._fan_mode is FanMode.MANUAL and self._fan_percent > 0:
                self._backend.set_fan_mode(FanMode.MANUAL, percent=self._fan_percent)
            elif self._fan_mode is FanMode.CURVE:
                self.step_curve_once()
            return self.get_state()

    def cycle_profile(self) -> dict[str, Any]:
        with self._lock:
            return self.set_profile(self._profile.next_profile())

    def set_fan(self, mode: FanMode | str, percent: int = 0) -> dict[str, Any]:
        parsed = mode if isinstance(mode, FanMode) else FanMode.parse(mode)
        with self._lock:
            if parsed is FanMode.CURVE:
                self._fan_mode = FanMode.CURVE
                self._curve_anchor_temp = None
                self._curve_applied_percent = 0
                self.step_curve_once()
            elif parsed is FanMode.MANUAL:
                self._backend.set_fan_mode(FanMode.MANUAL, percent=percent)
                self._fan_mode = FanMode.MANUAL
                self._fan_percent = percent
            elif parsed is FanMode.MAX:
                self._backend.set_fan_mode(FanMode.MAX)
                self._fan_mode = FanMode.MAX
                self._fan_percent = 100
            else:
                self._backend.set_fan_mode(FanMode.AUTO)
                self._fan_mode = FanMode.AUTO
                self._fan_percent = 0
                self._curve_anchor_temp = None
                self._curve_applied_percent = 0
            return self.get_state()

    def update_settings(self, updates: dict[str, Any]) -> dict[str, Any]:
        with self._lock:
            if "eco_power_save" in updates:
                self.settings.eco_power_save = bool(updates["eco_power_save"])
            if "eco_60hz_on_battery" in updates:
                self.settings.eco_60hz_on_battery = bool(updates["eco_60hz_on_battery"])
            if "auto_eco_on_battery" in updates:
                self.settings.auto_eco_on_battery = bool(updates["auto_eco_on_battery"])
            if "show_notifications" in updates:
                self.settings.show_notifications = bool(updates["show_notifications"])
            if "curve_points" in updates:
                self._curve = FanCurve.from_list(list(updates["curve_points"]))
                self.settings.curve_points = self._curve.to_list()
            self.settings.save(self._settings_path)

            if self._profile is PowerProfile.ECO and "eco_power_save" in updates:
                self._backend.apply_profile(PowerProfile.ECO, eco_power_save=self.settings.eco_power_save)

            return self.get_state()

    def set_prime_mode(self, mode: str) -> dict[str, Any]:
        with self._lock:
            selected = self._backend.set_prime_mode(mode)
            state = self.get_state()
            state["scheduled_prime_mode"] = selected
            return state

    def step_curve_once(self) -> None:
        """Evaluate temperature curve and update PWM if needed; also handles auto-Eco on AC unplug."""
        with self._lock:
            snap = self._backend.read_telemetry(allow_nvidia_smi=False)

            if self.settings.auto_eco_on_battery and self._last_on_battery is not None:
                if snap.on_battery and not self._last_on_battery and self._profile is not PowerProfile.ECO:
                    try:
                        self._backend.apply_profile(PowerProfile.ECO, eco_power_save=self.settings.eco_power_save)
                        self._profile = PowerProfile.ECO
                    except Exception:
                        pass
            self._last_on_battery = snap.on_battery

            if self._fan_mode is not FanMode.CURVE or not self.capabilities.has_pwm_control:
                return

            temps = [snap.cpu_temp_c]
            if snap.igpu_temp_c is not None:
                temps.append(snap.igpu_temp_c)
            if snap.dgpu_temp_c is not None:
                temps.append(snap.dgpu_temp_c)
            hot_c = max(temps)

            next_pct = self._curve.evaluate(
                hot_c,
                anchor_temp_c=self._curve_anchor_temp,
                current_percent=self._curve_applied_percent,
            )
            if next_pct != self._curve_applied_percent:
                self._backend.set_fan_mode(FanMode.MANUAL, percent=next_pct)
                self._curve_applied_percent = next_pct
                self._curve_anchor_temp = hot_c

    def shutdown(self) -> None:
        """Fail-safe: restore BIOS Auto fan curve whenever the daemon stops."""
        with self._lock:
            if self._fan_mode is not FanMode.AUTO and self.capabilities.has_pwm_control:
                try:
                    self._backend.set_fan_mode(FanMode.AUTO)
                    self._fan_mode = FanMode.AUTO
                except Exception:
                    pass
