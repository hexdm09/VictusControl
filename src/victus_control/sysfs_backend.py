from __future__ import annotations

from collections.abc import Callable
import os
from pathlib import Path
import shutil
import subprocess

from victus_control.models import (
    FanMode,
    HardwareCapabilities,
    PowerProfile,
    PrivilegeRequiredError,
    TelemetrySnapshot,
    ValidationError,
    percent_to_pwm,
    pwm_to_percent,
)

CommandRunner = Callable[[list[str]], tuple[int, str]]

# Supported laptop fan hwmon drivers across HP Victus/OMEN and other Linux laptops
_FAN_HWMON_DRIVERS = ("hp", "omen", "asus", "asus_nb_wmi", "thinkpad", "legion", "dell_smm", "applesmc")


def _default_runner(cmd: list[str]) -> tuple[int, str]:
    try:
        proc = subprocess.run(
            cmd,
            check=False,
            capture_output=True,
            text=True,
            timeout=3.0,
        )
        return (proc.returncode, proc.stdout)
    except (OSError, subprocess.SubprocessError):
        return (1, "")


class LinuxSysfsBackend:
    """Adaptive Linux sysfs hardware adapter for HP Victus / OMEN and cross-vendor laptops.

    Supports:
    - AMD Ryzen (``amd-pstate-epp`` / ``cpufreq/boost`` / ``k10temp`` / ``amdgpu``)
    - Intel Core (``intel_pstate`` / ``intel_pstate/no_turbo`` / ``coretemp``)
    - Legacy ``acpi-cpufreq`` governors (``schedutil``, ``powersave``, ``ondemand``, ``performance``)
    - HP Victus S / OMEN ``hp-wmi`` hwmon (full PWM on Linux 6.19+/7.0+, graceful read-only RPM on older kernels)
    - NVIDIA & discrete AMD/Intel GPUs (PCIe Runtime D3 ``runtime_status`` without waking sleeping dGPUs)
    """

    def __init__(
        self,
        sys_root: Path = Path("/sys"),
        proc_root: Path = Path("/proc"),
        command_runner: CommandRunner | None = None,
    ) -> None:
        self.sys_root = sys_root
        self.proc_root = proc_root
        self._run = command_runner or _default_runner

    # ---- discovery helpers -------------------------------------------------

    def _read_str(self, path: Path) -> str | None:
        try:
            return path.read_text(encoding="utf-8").strip()
        except OSError:
            return None

    def _read_int(self, path: Path) -> int | None:
        raw = self._read_str(path)
        if raw is None or raw == "":
            return None
        try:
            return int(raw)
        except ValueError:
            return None

    def _write_str(self, path: Path, value: str) -> None:
        try:
            path.write_text(f"{value}\n", encoding="utf-8")
        except PermissionError as exc:
            raise PrivilegeRequiredError(path) from exc
        except OSError as exc:
            if exc.errno in (1, 13, 30):  # EPERM, EACCES, EROFS
                raise PrivilegeRequiredError(path) from exc
            raise

    def _find_hwmon_by_name(self, target_names: tuple[str, ...]) -> Path | None:
        hwmon_root = self.sys_root / "class/hwmon"
        if not hwmon_root.is_dir():
            return None
        by_name: dict[str, Path] = {}
        for entry in sorted(hwmon_root.iterdir()):
            nm = self._read_str(entry / "name")
            if nm and nm not in by_name:
                by_name[nm] = entry
        for wanted in target_names:
            if wanted in by_name:
                return by_name[wanted]
        return None

    def _find_dgpu_pci(self) -> tuple[Path | None, str | None]:
        """Locate discrete GPU PCI device (NVIDIA 0x10de, or non-boot AMD/Intel dGPU)."""
        pci_root = self.sys_root / "bus/pci/devices"
        if not pci_root.is_dir():
            return (None, None)

        gpus: list[tuple[Path, str, str | None]] = []
        for dev in sorted(pci_root.iterdir()):
            cls = (self._read_str(dev / "class") or "").lower()
            if not cls.startswith("0x03"):
                continue
            vendor = (self._read_str(dev / "vendor") or "").lower()
            boot_vga = self._read_str(dev / "boot_vga")
            gpus.append((dev, vendor, boot_vga))

        for dev, vendor, _boot in gpus:
            if vendor == "0x10de":
                return (dev, vendor)
        if len(gpus) >= 2:
            for dev, vendor, boot_vga in gpus:
                if boot_vga == "0":
                    return (dev, vendor)
        return (None, None)

    def _find_battery_dir(self) -> Path | None:
        psy_root = self.sys_root / "class/power_supply"
        if not psy_root.is_dir():
            return None
        for entry in sorted(psy_root.iterdir()):
            if self._read_str(entry / "type") == "Battery":
                return entry
        return None

    def _is_on_battery(self, bat_dir: Path | None) -> bool:
        psy_root = self.sys_root / "class/power_supply"
        if psy_root.is_dir():
            for entry in sorted(psy_root.iterdir()):
                if self._read_str(entry / "type") == "Mains":
                    online = self._read_int(entry / "online")
                    if online is not None:
                        return online == 0
        if bat_dir is not None:
            status = (self._read_str(bat_dir / "status") or "").lower()
            return status == "discharging"
        return False

    def _read_cpu_boost(self) -> bool | None:
        generic_boost = self.sys_root / "devices/system/cpu/cpufreq/boost"
        if generic_boost.exists():
            val = self._read_int(generic_boost)
            return bool(val) if val is not None else None

        # Intel pstate uses inverted polarity: no_turbo == 0 means Turbo Boost is ON
        intel_no_turbo = self.sys_root / "devices/system/cpu/intel_pstate/no_turbo"
        if intel_no_turbo.exists():
            val = self._read_int(intel_no_turbo)
            return (val == 0) if val is not None else None
        return None

    def _has_cpu_boost_control(self) -> bool:
        return (
            (self.sys_root / "devices/system/cpu/cpufreq/boost").exists()
            or (self.sys_root / "devices/system/cpu/intel_pstate/no_turbo").exists()
        )

    # ---- capability probing ------------------------------------------------

    def probe_capabilities(self) -> HardwareCapabilities:
        dmi = self.sys_root / "devices/virtual/dmi/id"
        vendor = self._read_str(dmi / "sys_vendor") or "Unknown"
        product = self._read_str(dmi / "product_name") or "Unknown"
        board = self._read_str(dmi / "board_name") or "Unknown"
        bios = self._read_str(dmi / "bios_version") or "Unknown"

        pp_file = self.sys_root / "firmware/acpi/platform_profile"
        pp_choices_raw = self._read_str(self.sys_root / "firmware/acpi/platform_profile_choices") or ""
        choices = tuple(c for c in pp_choices_raw.split() if c)

        fan_hwmon = self._find_hwmon_by_name(_FAN_HWMON_DRIVERS)
        has_pwm = fan_hwmon is not None and (fan_hwmon / "pwm1_enable").exists()

        cpu0 = self.sys_root / "devices/system/cpu/cpu0/cpufreq"
        scaling_driver = self._read_str(cpu0 / "scaling_driver") or "unknown"
        has_epp = (cpu0 / "energy_performance_preference").exists()
        has_boost = self._has_cpu_boost_control()

        bat_dir = self._find_battery_dir()
        dgpu_dir, _ = self._find_dgpu_pci()

        return HardwareCapabilities(
            vendor=vendor,
            product_name=product,
            board_name=board,
            bios_version=bios,
            has_platform_profile=pp_file.exists(),
            platform_profile_choices=choices,
            has_hp_hwmon=fan_hwmon is not None,
            has_pwm_control=has_pwm,
            has_cpu_boost=has_boost,
            has_epp=has_epp,
            scaling_driver=scaling_driver,
            has_battery=bat_dir is not None,
            dgpu_pci_address=dgpu_dir.name if dgpu_dir is not None else None,
            has_prime_select=shutil.which("prime-select") is not None,
        )

    # ---- telemetry reading -------------------------------------------------

    def read_telemetry(self, allow_nvidia_smi: bool = False) -> TelemetrySnapshot:
        pp = self._read_str(self.sys_root / "firmware/acpi/platform_profile") or "balanced"

        cpu_temp = self._read_hwmon_temp_c(("k10temp", "coretemp", "zenpower", "cpu_thermal", "acpitz")) or 0.0
        igpu_temp = self._read_hwmon_temp_c(("amdgpu", "i915", "xe"))
        nvme_temp = self._read_hwmon_temp_c(("nvme",))

        fan_hwmon = self._find_hwmon_by_name(_FAN_HWMON_DRIVERS)
        fan1 = self._read_int(fan_hwmon / "fan1_input") if fan_hwmon else None
        fan2 = self._read_int(fan_hwmon / "fan2_input") if fan_hwmon else None
        pwm_en = self._read_int(fan_hwmon / "pwm1_enable") if fan_hwmon else None
        pwm_raw = self._read_int(fan_hwmon / "pwm1") if fan_hwmon else None
        pwm_pct = pwm_to_percent(pwm_raw) if pwm_raw is not None else None

        cpu0 = self.sys_root / "devices/system/cpu/cpu0/cpufreq"
        gov = self._read_str(cpu0 / "scaling_governor")
        epp = self._read_str(cpu0 / "energy_performance_preference")
        boost = self._read_cpu_boost()

        bat_dir = self._find_battery_dir()
        on_bat = self._is_on_battery(bat_dir)
        bat_status = self._read_str(bat_dir / "status") if bat_dir else None
        bat_pct = self._read_int(bat_dir / "capacity") if bat_dir else None
        bat_watts = self._read_battery_watts(bat_dir, bat_status) if bat_dir else None

        dgpu_dir, dgpu_vendor = self._find_dgpu_pci()
        dgpu_state = self._read_str(dgpu_dir / "power/runtime_status") if dgpu_dir else None
        dgpu_temp: float | None = None
        dgpu_holders: tuple[str, ...] = ()

        if dgpu_state == "active":
            if dgpu_vendor == "0x10de":
                dgpu_holders = self._detect_dgpu_holders()
                if allow_nvidia_smi:
                    rc, out = self._run(
                        ["nvidia-smi", "--query-gpu=temperature.gpu", "--format=csv,noheader,nounits"]
                    )
                    if rc == 0 and out.strip():
                        try:
                            dgpu_temp = float(out.strip().splitlines()[0])
                        except ValueError:
                            dgpu_temp = None

        prime_mode = self._read_str(Path("/etc/prime-discrete"))

        return TelemetrySnapshot(
            platform_profile=pp,
            cpu_temp_c=round(cpu_temp, 1),
            igpu_temp_c=round(igpu_temp, 1) if igpu_temp is not None else None,
            dgpu_temp_c=dgpu_temp,
            nvme_temp_c=round(nvme_temp, 1) if nvme_temp is not None else None,
            fan1_rpm=fan1 or 0,
            fan2_rpm=fan2 or 0,
            pwm_enable=pwm_en,
            pwm_percent=pwm_pct,
            cpu_boost_enabled=boost,
            cpu_governor=gov,
            cpu_epp=epp,
            on_battery=on_bat,
            battery_status=bat_status,
            battery_percent=bat_pct,
            battery_watts=bat_watts,
            dgpu_power_state=dgpu_state,
            dgpu_holders=dgpu_holders,
            prime_mode=prime_mode,
        )

    def _read_hwmon_temp_c(self, names: tuple[str, ...]) -> float | None:
        hw = self._find_hwmon_by_name(names)
        if hw is None:
            return None
        raw = self._read_int(hw / "temp1_input")
        if raw is None or raw <= -100_000:
            return None
        return raw / 1000.0

    def _read_battery_watts(self, bat_dir: Path, status: str | None) -> float | None:
        power_uw = self._read_int(bat_dir / "power_now")
        if power_uw is None:
            cur_ua = self._read_int(bat_dir / "current_now")
            volt_uv = self._read_int(bat_dir / "voltage_now")
            if cur_ua is not None and volt_uv is not None:
                power_uw = (cur_ua * volt_uv) // 1_000_000
        if power_uw is None:
            return None
        watts = round(power_uw / 1_000_000.0, 1)
        st = (status or "").lower()
        if st == "discharging" and watts > 0:
            return -watts
        return watts

    def _detect_dgpu_holders(self) -> tuple[str, ...]:
        """Find user processes holding /dev/nvidia* without waking PCIe."""
        if not self.proc_root.is_dir():
            return ()
        holders: set[str] = set()
        try:
            with os.scandir(self.proc_root) as proc_it:
                for entry in proc_it:
                    if not entry.name.isdigit():
                        continue
                    fd_dir = Path(entry.path) / "fd"
                    try:
                        with os.scandir(fd_dir) as fd_it:
                            for fd in fd_it:
                                target = os.readlink(fd.path)
                                if target.startswith("/dev/nvidia"):
                                    comm = self._read_str(Path(entry.path) / "comm") or entry.name
                                    holders.add(f"{comm}({entry.name})")
                                    break
                    except OSError:
                        continue
        except OSError:
            return ()
        return tuple(sorted(holders)[:6])

    # ---- mutations (require root / victusd) --------------------------------

    def apply_profile(self, profile: PowerProfile, eco_power_save: bool = True) -> None:
        pp_file = self.sys_root / "firmware/acpi/platform_profile"
        if pp_file.exists():
            choices = (self._read_str(self.sys_root / "firmware/acpi/platform_profile_choices") or "").split()
            target = self._resolve_kernel_profile(profile, choices)
            if target is not None:
                self._write_str(pp_file, target)

        self._apply_cpu_policy(profile, eco_power_save=eco_power_save)

    @staticmethod
    def _resolve_kernel_profile(profile: PowerProfile, choices: list[str]) -> str | None:
        if not choices:
            return profile.kernel_profile
        candidates_map = {
            PowerProfile.ECO: ("low-power", "quiet", "cool", "balanced"),
            PowerProfile.BALANCED: ("balanced", "balanced-performance"),
            PowerProfile.PERFORMANCE: ("performance", "balanced-performance", "balanced"),
        }
        for candidate in candidates_map[profile]:
            if candidate in choices:
                return candidate
        return None

    def _apply_cpu_policy(self, profile: PowerProfile, eco_power_save: bool) -> None:
        cpu_root = self.sys_root / "devices/system/cpu"
        if not cpu_root.is_dir():
            return

        if profile is PowerProfile.ECO and eco_power_save:
            gov_prefs = ("powersave", "schedutil", "ondemand")
            target_epp = "power"
            enable_boost = False
        elif profile is PowerProfile.ECO:
            gov_prefs = ("powersave", "schedutil", "ondemand")
            target_epp = "balance_power"
            enable_boost = True
        elif profile is PowerProfile.BALANCED:
            gov_prefs = ("powersave", "schedutil", "ondemand")
            target_epp = "balance_performance"
            enable_boost = True
        else:
            gov_prefs = ("powersave", "performance", "schedutil")
            target_epp = "performance"
            enable_boost = True

        for entry in sorted(cpu_root.iterdir()):
            if not (entry.name.startswith("cpu") and entry.name[3:].isdigit()):
                continue
            cpufreq = entry / "cpufreq"
            gov_file = cpufreq / "scaling_governor"
            epp_file = cpufreq / "energy_performance_preference"
            if gov_file.exists():
                avail_govs = (self._read_str(cpufreq / "scaling_available_governors") or "").split()
                # Without EPP (e.g. acpi-cpufreq), PERFORMANCE uses 'performance' governor directly
                prefs = ("performance", "schedutil", "powersave") if (profile is PowerProfile.PERFORMANCE and not epp_file.exists()) else gov_prefs
                chosen_gov = next((g for g in prefs if not avail_govs or g in avail_govs), None)
                if chosen_gov is not None:
                    self._write_str(gov_file, chosen_gov)
            if epp_file.exists():
                avail = (self._read_str(cpufreq / "energy_performance_available_preferences") or "").split()
                if not avail or target_epp in avail:
                    self._write_str(epp_file, target_epp)

        boost_file = cpu_root / "cpufreq/boost"
        intel_no_turbo = cpu_root / "intel_pstate/no_turbo"
        if boost_file.exists():
            self._write_str(boost_file, "1" if enable_boost else "0")
        elif intel_no_turbo.exists():
            self._write_str(intel_no_turbo, "0" if enable_boost else "1")

    def set_fan_mode(self, mode: FanMode, percent: int = 0) -> None:
        fan_hwmon = self._find_hwmon_by_name(_FAN_HWMON_DRIVERS)
        if fan_hwmon is None or not (fan_hwmon / "pwm1_enable").exists():
            raise ValidationError("PWM fan control is not supported by this laptop's kernel driver (upgrade to Linux 6.19+ for HP Victus/OMEN).")

        enable_file = fan_hwmon / "pwm1_enable"
        pwm_file = fan_hwmon / "pwm1"

        if mode is FanMode.AUTO:
            self._write_str(enable_file, "2")
            return

        if mode is FanMode.MAX:
            self._write_str(enable_file, "0")
            return

        if percent < 1 or percent > 100:
            raise ValidationError(f"Manual/Curve fan speed requires 1..100%, got {percent}.")

        pwm_val = percent_to_pwm(percent)
        if self._read_int(enable_file) != 1:
            self._write_str(enable_file, "1")
        self._write_str(pwm_file, str(pwm_val))

    def set_prime_mode(self, mode: str) -> str:
        norm = mode.strip().lower()
        mapping = {
            "hybrid": "on-demand",
            "on-demand": "on-demand",
            "igpu": "intel",
            "integrated": "intel",
            "intel": "intel",
            "discrete": "nvidia",
            "dgpu": "nvidia",
            "nvidia": "nvidia",
        }
        if norm not in mapping:
            raise ValidationError("Expected GPU mode: hybrid (on-demand), igpu (integrated), or discrete (nvidia).")
        if shutil.which("prime-select") is None:
            raise ValidationError("prime-select utility is not installed on this system.")
        target = mapping[norm]
        rc, out = self._run(["prime-select", target])
        if rc != 0:
            raise ValidationError(f"prime-select {target} failed: {out.strip()}")
        return target
