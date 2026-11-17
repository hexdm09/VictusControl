from __future__ import annotations

from pathlib import Path
import shutil
import sys
import threading
from typing import Any

from victus_control import __version__
from victus_control.display import DisplayRefreshManager
from victus_control.ipc import DEFAULT_SOCKET_PATH, VictusIpcClient
from victus_control.models import FanMode, PowerProfile, VictusError
from victus_control.sysfs_backend import LinuxSysfsBackend

APP_ID = "io.github.hexdm09.VictusControl"


def _ensure_user_desktop_icon() -> None:
    """Ensure GNOME Wayland Dock / Alt+Tab can resolve <APP_ID>.desktop and <APP_ID>.svg."""
    try:
        svg_src = Path(__file__).parent / "icons/hicolor/scalable/apps" / f"{APP_ID}.svg"
        if not svg_src.is_file():
            return
        data_home = Path.home() / ".local/share"
        icon_dst = data_home / "icons/hicolor/scalable/apps" / f"{APP_ID}.svg"
        desktop_dst = data_home / "applications" / f"{APP_ID}.desktop"

        if not icon_dst.is_file():
            icon_dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(svg_src, icon_dst)

        if not desktop_dst.is_file():
            desktop_dst.parent.mkdir(parents=True, exist_ok=True)
            repo_src = Path(__file__).resolve().parents[2]
            exec_cmd = (
                "/usr/local/bin/victus-control-gui"
                if Path("/usr/local/bin/victus-control-gui").exists()
                else f"/usr/bin/env PYTHONPATH={repo_src} /usr/bin/python3 -m victus_control.ui.app"
            )
            desktop_dst.write_text(
                f"[Desktop Entry]\n"
                f"Type=Application\n"
                f"Name=VictusControl\n"
                f"Comment=Power profiles, fan curve, and Eco battery saver\n"
                f"Exec={exec_cmd}\n"
                f"Icon={APP_ID}\n"
                f"StartupWMClass={APP_ID}\n"
                f"Terminal=false\n"
                f"Categories=System;Settings;HardwareSettings;\n",
                encoding="utf-8",
            )
    except OSError:
        # Read-only sandbox or container: Gtk.IconTheme search path still renders in-window icon
        pass


def _load_gtk():
    import gi

    gi.require_version("Gtk", "4.0")
    gi.require_version("Adw", "1")
    from gi.repository import Adw, Gdk, GLib, Gtk

    return Adw, Gdk, GLib, Gtk


def main(argv: list[str] | None = None) -> int:
    _ensure_user_desktop_icon()
    Adw, Gdk, GLib, Gtk = _load_gtk()

    class VictusWindow(Adw.ApplicationWindow):
        def __init__(self, app: Any) -> None:
            super().__init__(application=app, title="VictusControl")
            self.set_default_size(476, -1)
            self.set_resizable(False)
            self.set_icon_name(APP_ID)
            self.add_css_class("victus-window")

            self._client = VictusIpcClient(DEFAULT_SOCKET_PATH)
            self._readonly_backend = LinuxSysfsBackend()
            self._suppress = False
            self._slider_debounce_id: int | None = None
            self._poll_in_flight = False

            self._load_css_and_icons()
            self._build_ui()
            self._refresh_async()
            self._timer_id = GLib.timeout_add(2000, self._on_poll_tick)

        def _load_css_and_icons(self) -> None:
            provider = Gtk.CssProvider()
            ui_dir = Path(__file__).parent
            provider.load_from_path(str(ui_dir / "theme.css"))
            display = Gdk.Display.get_default()
            if display is not None:
                Gtk.StyleContext.add_provider_for_display(
                    display, provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
                )
                icon_theme = Gtk.IconTheme.get_for_display(display)
                icon_theme.add_search_path(str(ui_dir / "icons"))
            Gtk.Window.set_default_icon_name(APP_ID)

        def _build_ui(self) -> None:
            outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)

            header_bar = Adw.HeaderBar()
            title_box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            svg_file = Path(__file__).parent / "icons/hicolor/scalable/apps" / f"{APP_ID}.svg"
            if svg_file.is_file():
                logo = Gtk.Image.new_from_file(str(svg_file))
                logo.set_pixel_size(20)
                title_box.append(logo)
            title_box.append(Gtk.Label(label="VICTUS CONTROL"))
            header_bar.set_title_widget(title_box)
            outer.append(header_bar)

            card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
            card.add_css_class("victus-card")
            card.set_margin_top(10)
            card.set_margin_bottom(14)
            card.set_margin_start(14)
            card.set_margin_end(14)
            outer.append(card)

            self._daemon_banner = Gtk.Label(
                label="Read-only mode: 'victusd' service is not running yet. Run ./install_linux.sh to enable hardware writes.",
                wrap=True,
                xalign=0.0,
            )
            self._daemon_banner.add_css_class("daemon-banner")
            self._daemon_banner.set_visible(False)
            card.append(self._daemon_banner)

            # Top telemetry row: temps + battery watts + dGPU sleep badge
            top_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            self._vitals_label = Gtk.Label(label="Reading sensors…", xalign=0.0, hexpand=True)
            self._vitals_label.add_css_class("tabular-readout")
            self._dgpu_badge = Gtk.Label(label="dGPU: —")
            self._dgpu_badge.add_css_class("badge-sleep")
            top_row.append(self._vitals_label)
            top_row.append(self._dgpu_badge)
            card.append(top_row)

            # Mode pills: ECO | BALANCED | PERF
            self._mode_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
            mode_cap = Gtk.Label(label="POWER MODE", xalign=0.0)
            mode_cap.add_css_class("section-caption")
            self._mode_box.append(mode_cap)

            pills_grid = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8, homogeneous=True)
            self._btn_eco = Gtk.Button(label="ECO")
            self._btn_bal = Gtk.Button(label="BALANCED")
            self._btn_perf = Gtk.Button(label="PERF")
            for btn, prof in (
                (self._btn_eco, PowerProfile.ECO),
                (self._btn_bal, PowerProfile.BALANCED),
                (self._btn_perf, PowerProfile.PERFORMANCE),
            ):
                btn.add_css_class("mode-pill")
                btn.connect("clicked", lambda _b, p=prof: self._on_profile_clicked(p))
                pills_grid.append(btn)
            self._mode_box.append(pills_grid)
            card.append(self._mode_box)

            # Fan controls: AUTO | CURVE | MAX + slider (adapts to kernel capability)
            self._fan_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
            self._fan_cap = Gtk.Label(label="FANS", xalign=0.0)
            self._fan_cap.add_css_class("section-caption")
            self._fan_box.append(self._fan_cap)

            self._fan_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
            self._btn_fan_auto = Gtk.Button(label="AUTO")
            self._btn_fan_curve = Gtk.Button(label="CURVE")
            self._btn_fan_max = Gtk.Button(label="MAX")
            for btn, fmode in (
                (self._btn_fan_auto, FanMode.AUTO),
                (self._btn_fan_curve, FanMode.CURVE),
                (self._btn_fan_max, FanMode.MAX),
            ):
                btn.add_css_class("fan-pill")
                btn.connect("clicked", lambda _b, m=fmode: self._on_fan_mode_clicked(m))
                self._fan_row.append(btn)

            self._fan_slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 10, 100, 1)
            self._fan_slider.set_draw_value(False)
            self._fan_slider.set_hexpand(True)
            self._fan_slider.set_value(45)
            self._fan_slider.connect("value-changed", self._on_slider_changed)
            self._fan_row.append(self._fan_slider)

            self._fan_pct_label = Gtk.Label(label="45%", width_chars=4, xalign=1.0)
            self._fan_pct_label.add_css_class("tabular-readout")
            self._fan_row.append(self._fan_pct_label)
            self._fan_box.append(self._fan_row)
            card.append(self._fan_box)

            # Footer: board + RPMs
            footer_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            self._footer_info = Gtk.Label(label="", xalign=0.0, hexpand=True)
            self._footer_info.add_css_class("footer-muted")
            self._fan_rpm_label = Gtk.Label(label="", xalign=1.0)
            self._fan_rpm_label.add_css_class("footer-muted")
            footer_row.append(self._footer_info)
            footer_row.append(self._fan_rpm_label)
            card.append(footer_row)

            # Expandable Eco & GPU drawer
            expander = Gtk.Expander(label="Eco Saver & Hardware Settings")
            extra_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
            extra_box.set_margin_top(10)

            self._sw_eco_save = self._make_switch_row(
                extra_box,
                "Eco CPU Saver (disable Turbo Boost + power EPP)",
                self._on_settings_toggled,
            )
            self._sw_eco_60hz = self._make_switch_row(
                extra_box,
                "Switch display to 60 Hz automatically in Eco on battery",
                self._on_settings_toggled,
            )
            self._sw_auto_bat = self._make_switch_row(
                extra_box,
                "Auto-switch to Eco when charger is unplugged",
                self._on_settings_toggled,
            )

            self._holders_label = Gtk.Label(label="", wrap=True, xalign=0.0)
            self._holders_label.add_css_class("footer-muted")
            self._holders_label.set_visible(False)
            extra_box.append(self._holders_label)

            expander.set_child(extra_box)
            card.append(expander)

            self.set_content(outer)

        def _make_switch_row(self, parent: Any, text: str, callback: Any) -> Any:
            row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            lbl = Gtk.Label(label=text, xalign=0.0, hexpand=True, wrap=True)
            sw = Gtk.Switch(valign=Gtk.Align.CENTER)
            sw.connect("notify::active", callback)
            row.append(lbl)
            row.append(sw)
            parent.append(row)
            return sw

        def _on_poll_tick(self) -> bool:
            if self.is_visible():
                self._refresh_async()
            return True

        def _refresh_async(self) -> None:
            if self._poll_in_flight:
                return
            self._poll_in_flight = True

            def worker() -> None:
                try:
                    if self._client.is_daemon_available():
                        state = self._client.get_state()
                        state["daemon_connected"] = True
                    else:
                        caps = self._readonly_backend.probe_capabilities()
                        snap = self._readonly_backend.read_telemetry(allow_nvidia_smi=False)
                        state = {
                            "profile": PowerProfile.from_kernel(snap.platform_profile).value,
                            "fan_mode": {0: "max", 1: "manual", 2: "auto"}.get(snap.pwm_enable or 2, "auto"),
                            "fan_percent": snap.pwm_percent or 0,
                            "capabilities": caps.to_dict(),
                            "telemetry": snap.to_dict(),
                            "settings": {
                                "eco_power_save": True,
                                "eco_60hz_on_battery": True,
                                "auto_eco_on_battery": True,
                            },
                            "daemon_connected": False,
                        }
                    GLib.idle_add(self._apply_state_to_ui, state)
                except Exception as exc:
                    GLib.idle_add(self._show_error, str(exc))
                finally:
                    self._poll_in_flight = False

            threading.Thread(target=worker, daemon=True).start()

        def _apply_state_to_ui(self, state: dict[str, Any]) -> bool:
            self._suppress = True
            try:
                daemon_ok = bool(state.get("daemon_connected"))
                self._daemon_banner.set_visible(not daemon_ok)

                caps = state["capabilities"]
                tel = state["telemetry"]

                # Adaptive UI capability gating for other laptop models / older kernels
                has_pwm = bool(caps.get("has_pwm_control"))
                has_fan_hwmon = bool(caps.get("has_hp_hwmon"))
                self._fan_box.set_visible(has_pwm or has_fan_hwmon)
                self._fan_row.set_sensitive(has_pwm and daemon_ok)
                self._fan_cap.set_label("FANS" if has_pwm else "FANS (RPM READ-ONLY ON THIS KERNEL)")
                self._dgpu_badge.set_visible(caps.get("dgpu_pci_address") is not None)
                self._sw_auto_bat.set_sensitive(bool(caps.get("has_battery")))

                prof = str(state.get("profile", "balanced"))
                for btn, key, cls in (
                    (self._btn_eco, "eco", "active-eco"),
                    (self._btn_bal, "balanced", "active-balanced"),
                    (self._btn_perf, "performance", "active-performance"),
                ):
                    btn.set_sensitive(daemon_ok)
                    if prof == key:
                        btn.add_css_class(cls)
                    else:
                        btn.remove_css_class(cls)

                fmode = str(state.get("fan_mode", "auto"))
                for btn, key in (
                    (self._btn_fan_auto, "auto"),
                    (self._btn_fan_curve, "curve"),
                    (self._btn_fan_max, "max"),
                ):
                    if fmode == key:
                        btn.add_css_class("active-fan")
                    else:
                        btn.remove_css_class("active-fan")

                fpct = int(state.get("fan_percent") or 0)
                if fpct > 0:
                    self._fan_slider.set_value(max(10, min(100, fpct)))
                    self._fan_pct_label.set_label(f"{fpct}%")

                parts = [f"CPU {tel['cpu_temp_c']:.0f}°C"]
                if tel.get("igpu_temp_c") is not None:
                    parts.append(f"iGPU {tel['igpu_temp_c']:.0f}°C")
                if tel.get("battery_watts") is not None:
                    parts.append(f"{tel['battery_watts']:+.1f} W")
                elif tel.get("battery_percent") is not None:
                    parts.append(f"BAT {tel['battery_percent']}%")
                self._vitals_label.set_label("  ·  ".join(parts))

                dgpu_state = tel.get("dgpu_power_state")
                if dgpu_state == "suspended":
                    self._dgpu_badge.set_label("dGPU Sleep (0W)")
                    self._dgpu_badge.remove_css_class("badge-awake")
                    self._dgpu_badge.add_css_class("badge-sleep")
                    self._holders_label.set_visible(False)
                elif dgpu_state == "active":
                    self._dgpu_badge.set_label("dGPU Active")
                    self._dgpu_badge.remove_css_class("badge-sleep")
                    self._dgpu_badge.add_css_class("badge-awake")
                    holders = tel.get("dgpu_holders") or []
                    if holders:
                        self._holders_label.set_label(f"Processes keeping dGPU awake: {', '.join(holders)}")
                        self._holders_label.set_visible(True)
                    else:
                        self._holders_label.set_visible(False)

                if tel.get("fan1_rpm") or tel.get("fan2_rpm"):
                    self._fan_rpm_label.set_label(f"{tel['fan1_rpm']} / {tel['fan2_rpm']} RPM")
                else:
                    self._fan_rpm_label.set_label("")
                self._footer_info.set_label(f"v{__version__} · Board {caps['board_name']} · {caps['scaling_driver']}")

                settings = state.get("settings") or {}
                self._sw_eco_save.set_active(bool(settings.get("eco_power_save", True)))
                self._sw_eco_60hz.set_active(bool(settings.get("eco_60hz_on_battery", True)))
                self._sw_auto_bat.set_active(bool(settings.get("auto_eco_on_battery", True)))
            finally:
                self._suppress = False
            return False

        def _show_error(self, message: str) -> bool:
            self._footer_info.set_label(message)
            return False

        def _on_profile_clicked(self, profile: PowerProfile) -> None:
            if self._suppress:
                return

            def worker() -> None:
                try:
                    state = self._client.set_profile(profile.value)
                    state["daemon_connected"] = True
                    settings = state.get("settings") or {}
                    on_bat = bool(state["telemetry"].get("on_battery"))
                    if profile is PowerProfile.ECO and settings.get("eco_60hz_on_battery", True) and on_bat:
                        GLib.idle_add(DisplayRefreshManager.set_prefer_60hz, True)
                    elif profile is not PowerProfile.ECO:
                        GLib.idle_add(DisplayRefreshManager.set_prefer_60hz, False)
                    GLib.idle_add(self._apply_state_to_ui, state)
                except VictusError as exc:
                    GLib.idle_add(self._show_error, str(exc))

            threading.Thread(target=worker, daemon=True).start()

        def _on_fan_mode_clicked(self, mode: FanMode) -> None:
            if self._suppress:
                return

            def worker() -> None:
                try:
                    state = self._client.set_fan(mode.value)
                    state["daemon_connected"] = True
                    GLib.idle_add(self._apply_state_to_ui, state)
                except VictusError as exc:
                    GLib.idle_add(self._show_error, str(exc))

            threading.Thread(target=worker, daemon=True).start()

        def _on_slider_changed(self, slider: Any) -> None:
            pct = int(round(slider.get_value()))
            self._fan_pct_label.set_label(f"{pct}%")
            if self._suppress:
                return
            if self._slider_debounce_id is not None:
                GLib.source_remove(self._slider_debounce_id)

            def apply_debounced() -> bool:
                self._slider_debounce_id = None
                threading.Thread(
                    target=self._apply_manual_fan_worker, args=(pct,), daemon=True
                ).start()
                return False

            self._slider_debounce_id = GLib.timeout_add(300, apply_debounced)

        def _apply_manual_fan_worker(self, pct: int) -> None:
            try:
                state = self._client.set_fan("manual", percent=pct)
                state["daemon_connected"] = True
                GLib.idle_add(self._apply_state_to_ui, state)
            except VictusError as exc:
                GLib.idle_add(self._show_error, str(exc))

        def _on_settings_toggled(self, _sw: Any, _pspec: Any) -> None:
            if self._suppress or not self._client.is_daemon_available():
                return
            updates = {
                "eco_power_save": self._sw_eco_save.get_active(),
                "eco_60hz_on_battery": self._sw_eco_60hz.get_active(),
                "auto_eco_on_battery": self._sw_auto_bat.get_active(),
            }

            def worker() -> None:
                try:
                    state = self._client.update_settings(**updates)
                    state["daemon_connected"] = True
                    GLib.idle_add(self._apply_state_to_ui, state)
                except VictusError as exc:
                    GLib.idle_add(self._show_error, str(exc))

            threading.Thread(target=worker, daemon=True).start()

    class VictusApp(Adw.Application):
        def __init__(self) -> None:
            super().__init__(application_id=APP_ID)
            self._win: VictusWindow | None = None

        def do_startup(self) -> None:
            Adw.Application.do_startup(self)
            Adw.StyleManager.get_default().set_color_scheme(Adw.ColorScheme.FORCE_DARK)

        def do_activate(self) -> None:
            if self._win is None:
                self._win = VictusWindow(self)
            self._win.present()

    app = VictusApp()
    return int(app.run(argv if argv is not None else sys.argv))


if __name__ == "__main__":
    sys.exit(main())
