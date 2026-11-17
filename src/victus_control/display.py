from __future__ import annotations

import shutil
import subprocess


class DisplayRefreshManager:
    """Switches the internal eDP laptop panel between 60 Hz (Eco) and max Hz on GNOME Wayland / X11."""

    @staticmethod
    def set_prefer_60hz(prefer_60hz: bool) -> bool:
        if DisplayRefreshManager._apply_mutter_dbus(prefer_60hz):
            return True
        return DisplayRefreshManager._apply_xrandr_fallback(prefer_60hz)

    @staticmethod
    def _apply_mutter_dbus(prefer_60hz: bool) -> bool:
        try:
            import gi

            gi.require_version("Gio", "2.0")
            gi.require_version("GLib", "2.0")
            from gi.repository import GLib, Gio

            bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
            res = bus.call_sync(
                "org.gnome.Mutter.DisplayConfig",
                "/org/gnome/Mutter/DisplayConfig",
                "org.gnome.Mutter.DisplayConfig",
                "GetCurrentState",
                None,
                None,
                Gio.DBusCallFlags.NONE,
                2000,
                None,
            )
            serial, monitors, logical_monitors, _props = res.unpack()

            connector_modes: dict[str, str] = {}
            changed = False

            for monitor_info in monitors:
                spec, modes, _mprops = monitor_info
                connector = spec[0]
                current_mode = None
                for m in modes:
                    mode_id, width, height, rate, _scale, _scales, mflags = m
                    if mflags.get("is-current"):
                        current_mode = (mode_id, width, height, float(rate))
                        break
                if current_mode is None:
                    continue

                cur_id, cur_w, cur_h, cur_rate = current_mode
                is_internal = connector.startswith("eDP") or connector.startswith("LVDS")
                if not is_internal:
                    connector_modes[connector] = cur_id
                    continue

                same_res = [
                    (m[0], float(m[3]))
                    for m in modes
                    if m[1] == cur_w and m[2] == cur_h
                ]
                if not same_res:
                    connector_modes[connector] = cur_id
                    continue

                if prefer_60hz:
                    # Pick closest mode around 60 Hz (59..61 Hz)
                    near_60 = [m for m in same_res if 58.0 <= m[1] <= 62.0]
                    chosen_id, chosen_rate = min(near_60, key=lambda x: abs(x[1] - 60.0)) if near_60 else (cur_id, cur_rate)
                else:
                    chosen_id, chosen_rate = max(same_res, key=lambda x: x[1])

                connector_modes[connector] = chosen_id
                if chosen_id != cur_id and abs(chosen_rate - cur_rate) > 1.0:
                    changed = True

            if not changed:
                return True

            new_logical = []
            for lm in logical_monitors:
                lx, ly, lscale, ltransform, lprimary, lspecs, _lprops = lm
                mon_cfgs = []
                for lspec in lspecs:
                    conn = lspec[0]
                    if conn in connector_modes:
                        mon_cfgs.append((conn, connector_modes[conn], {}))
                if mon_cfgs:
                    new_logical.append((int(lx), int(ly), float(lscale), int(ltransform), bool(lprimary), mon_cfgs))

            # method 1 = temporary/immediate apply (no modal confirmation dialog)
            params = GLib.Variant(
                "(uua(iiduba(ssa{sv}))a{sv})",
                (int(serial), 1, new_logical, {}),
            )
            bus.call_sync(
                "org.gnome.Mutter.DisplayConfig",
                "/org/gnome/Mutter/DisplayConfig",
                "org.gnome.Mutter.DisplayConfig",
                "ApplyMonitorsConfig",
                params,
                None,
                Gio.DBusCallFlags.NONE,
                3000,
                None,
            )
            return True
        except Exception:
            return False

    @staticmethod
    def _apply_xrandr_fallback(prefer_60hz: bool) -> bool:
        if shutil.which("xrandr") is None:
            return False
        try:
            proc = subprocess.run(["xrandr", "--query"], check=False, capture_output=True, text=True, timeout=2.0)
            if proc.returncode != 0:
                return False
            output_name = None
            rates: list[float] = []
            for line in proc.stdout.splitlines():
                if " connected" in line and ("eDP" in line or "LVDS" in line):
                    output_name = line.split()[0]
                elif output_name and "*" in line:
                    tokens = line.replace("*", " ").replace("+", " ").split()
                    for tok in tokens[1:]:
                        try:
                            rates.append(float(tok))
                        except ValueError:
                            pass
                    break
            if not output_name or not rates:
                return False
            target = 60.0 if prefer_60hz and any(58 <= r <= 62 for r in rates) else max(rates)
            res = subprocess.run(
                ["xrandr", "--output", output_name, "--rate", f"{target:.2f}"],
                check=False,
                capture_output=True,
                timeout=2.0,
            )
            return res.returncode == 0
        except (OSError, subprocess.SubprocessError):
            return False
