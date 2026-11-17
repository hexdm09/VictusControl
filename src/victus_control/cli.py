from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
from typing import Any

from victus_control.display import DisplayRefreshManager
from victus_control.ipc import DEFAULT_SOCKET_PATH, VictusIpcClient
from victus_control.models import FanMode, PowerProfile, ValidationError, VictusError
from victus_control.sysfs_backend import LinuxSysfsBackend


def _read_state_direct(backend: LinuxSysfsBackend) -> dict[str, Any]:
    caps = backend.probe_capabilities()
    snap = backend.read_telemetry(allow_nvidia_smi=False)
    try:
        prof = PowerProfile.from_kernel(snap.platform_profile).value
    except Exception:
        prof = snap.platform_profile
    fan_mode = {0: "max", 1: "manual", 2: "auto"}.get(snap.pwm_enable or 2, "auto")
    return {
        "profile": prof,
        "fan_mode": fan_mode,
        "fan_percent": snap.pwm_percent or 0,
        "capabilities": caps.to_dict(),
        "telemetry": snap.to_dict(),
        "daemon_connected": False,
    }


def _format_status(state: dict[str, Any]) -> str:
    caps = state["capabilities"]
    tel = state["telemetry"]
    prof = str(state["profile"]).upper()
    fan_mode = str(state["fan_mode"]).upper()
    fan_pct = state.get("fan_percent", 0)

    dgpu_str = tel.get("dgpu_power_state") or "n/a"
    if dgpu_str == "suspended":
        dgpu_badge = "suspended (D3cold ~0.0 W)"
    elif dgpu_str == "active":
        holders = ", ".join(tel.get("dgpu_holders") or [])
        dgpu_badge = f"ACTIVE ({holders})" if holders else "ACTIVE"
    else:
        dgpu_badge = dgpu_str

    bat_parts = []
    if tel.get("battery_status"):
        bat_parts.append(str(tel["battery_status"]))
    if tel.get("battery_percent") is not None:
        bat_parts.append(f"{tel['battery_percent']}%")
    if tel.get("battery_watts") is not None:
        bat_parts.append(f"{tel['battery_watts']:+.1f} W")
    bat_str = " · ".join(bat_parts) if bat_parts else "AC / Desktop"

    lines = [
        f"Board          : {caps['vendor']} {caps['product_name']} (Board {caps['board_name']}, BIOS {caps['bios_version']})",
        f"Active Profile : {prof} (kernel={tel['platform_profile']}, governor={tel.get('cpu_governor')}, epp={tel.get('cpu_epp')}, boost={tel.get('cpu_boost_enabled')})",
        f"Fans           : {fan_mode}" + (f" ({fan_pct}%)" if fan_pct else "") + f" — Fan1 {tel['fan1_rpm']} RPM | Fan2 {tel['fan2_rpm']} RPM (pwm1_enable={tel.get('pwm_enable')})",
        f"Temperatures   : CPU {tel['cpu_temp_c']:.1f}°C"
        + (f" | iGPU {tel['igpu_temp_c']:.1f}°C" if tel.get("igpu_temp_c") is not None else "")
        + (f" | NVMe {tel['nvme_temp_c']:.1f}°C" if tel.get("nvme_temp_c") is not None else ""),
        f"NVIDIA dGPU    : {dgpu_badge}" + (f" [prime={tel['prime_mode']}]" if tel.get("prime_mode") else ""),
        f"Power / Battery: {bat_str}",
    ]
    return "\n".join(lines)


def main(argv: list[str] | None = None, socket_path: Path = DEFAULT_SOCKET_PATH) -> int:
    parser = argparse.ArgumentParser(
        prog="victusctl",
        description="VictusControl Linux CLI — manage power profiles, fans, display Hz, and dGPU sleep",
    )
    parser.add_argument("--status", action="store_true", help="Show live profile, fans, temperatures, dGPU & battery")
    parser.add_argument("--info", action="store_true", help="Show hardware capability report")
    parser.add_argument("--get", action="store_true", help="Show active power profile")
    parser.add_argument("--set", metavar="PROFILE", help="Set power profile: eco | balanced | perf")
    parser.add_argument("--cycle", action="store_true", help="Cycle profile: eco -> balanced -> perf -> eco")
    parser.add_argument("--fan", metavar="MODE_OR_PCT", help="Set fan mode: auto | curve | max | <1-100>")
    parser.add_argument("--hz", choices=["60", "max"], help="Switch internal laptop display to 60 Hz or max Hz")
    parser.add_argument("--gpumode", choices=["hybrid", "igpu", "discrete"], help="Switch PRIME GPU mode (reboot/re-login)")
    parser.add_argument("--json", action="store_true", help="Output raw JSON")

    args = parser.parse_args(argv)
    client = VictusIpcClient(socket_path=socket_path)
    backend = LinuxSysfsBackend()

    try:
        if args.hz:
            ok = DisplayRefreshManager.set_prefer_60hz(args.hz == "60")
            if not ok:
                print("Error: Could not change display refresh rate via Mutter/xrandr.", file=sys.stderr)
                return 1
            print(f"Display refresh rate set to: {'60 Hz' if args.hz == '60' else 'maximum Hz'}")
            return 0

        if args.set:
            prof = PowerProfile.parse(args.set)
            if client.is_daemon_available():
                state = client.set_profile(prof.value)
            else:
                backend.apply_profile(prof, eco_power_save=True)
                state = _read_state_direct(backend)
            if prof is PowerProfile.ECO and state["telemetry"].get("on_battery"):
                DisplayRefreshManager.set_prefer_60hz(True)
            elif prof is not PowerProfile.ECO:
                DisplayRefreshManager.set_prefer_60hz(False)
            print(json.dumps(state, indent=2) if args.json else f"Applied profile: {prof.label}")
            return 0

        if args.cycle:
            if client.is_daemon_available():
                state = client.cycle_profile()
            else:
                cur_state = _read_state_direct(backend)
                nxt = PowerProfile.parse(cur_state["profile"]).next_profile()
                backend.apply_profile(nxt, eco_power_save=True)
                state = _read_state_direct(backend)
            print(json.dumps(state, indent=2) if args.json else f"Cycled profile -> {str(state['profile']).upper()}")
            return 0

        if args.fan:
            raw_fan = args.fan.strip()
            if raw_fan.isdigit() or (raw_fan.startswith("-") and raw_fan[1:].isdigit()):
                pct = int(raw_fan)
                if pct < 1 or pct > 100:
                    raise ValidationError("Manual fan percent must be 1..100 (or use auto|curve|max).")
                mode, percent = FanMode.MANUAL, pct
            else:
                mode = FanMode.parse(raw_fan)
                percent = 0
                if mode is FanMode.CURVE and not client.is_daemon_available():
                    raise ValidationError("Smart temperature curve requires 'victusd' daemon running.")

            if client.is_daemon_available():
                state = client.set_fan(mode.value, percent=percent)
            else:
                backend.set_fan_mode(mode, percent=percent)
                state = _read_state_direct(backend)
            label = f"{mode.value.upper()} ({percent}%)" if mode is FanMode.MANUAL else mode.value.upper()
            print(json.dumps(state, indent=2) if args.json else f"Fans set to: {label}")
            return 0

        if args.gpumode:
            if client.is_daemon_available():
                state = client.set_prime_mode(args.gpumode)
                selected = state.get("scheduled_prime_mode", args.gpumode)
            else:
                selected = backend.set_prime_mode(args.gpumode)
            print(f"GPU PRIME mode scheduled: {selected} (log out or reboot to apply).")
            return 0

        # Read-only commands (--status, --info, --get, or default)
        if client.is_daemon_available():
            state = client.get_state()
            state["daemon_connected"] = True
        else:
            state = _read_state_direct(backend)

        if args.json:
            print(json.dumps(state, indent=2))
        elif args.get:
            print(f"Profile: {str(state['profile']).upper()}")
        elif args.info:
            print(json.dumps(state["capabilities"], indent=2))
        else:
            print(_format_status(state))
        return 0

    except ValidationError as exc:
        print(f"Invalid arguments: {exc}", file=sys.stderr)
        return 2
    except VictusError as exc:
        print(f"Error [{exc.code}]: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
