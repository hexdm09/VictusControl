from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from tests.test_sysfs_backend import FakeSysfsTree
from victus_control.controller import VictusController
from victus_control.ipc import VictusIpcClient, VictusIpcServer
from victus_control.models import FanMode, PowerProfile
from victus_control.sysfs_backend import LinuxSysfsBackend


class ControllerAndIpcTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.sys_root = self.root / "sys"
        self.tree = FakeSysfsTree(self.sys_root)
        self.backend = LinuxSysfsBackend(sys_root=self.sys_root, command_runner=lambda _: (1, ""))
        self.settings_path = self.root / "settings.json"
        self.socket_path = self.root / "victusd.sock"

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_curve_step_and_failsafe_restore_to_auto_on_shutdown(self) -> None:
        ctrl = VictusController(self.backend, settings_path=self.settings_path)
        ctrl.set_fan(FanMode.CURVE)

        # Simulate high CPU temperature (80°C)
        (self.sys_root / "class/hwmon/hwmon5/temp1_input").write_text("80000\n", encoding="utf-8")
        ctrl.step_curve_once()

        self.assertEqual((self.sys_root / "class/hwmon/hwmon6/pwm1_enable").read_text().strip(), "1")
        pwm = int((self.sys_root / "class/hwmon/hwmon6/pwm1").read_text().strip())
        self.assertGreater(pwm, 180)

        # Fail-safe invariant: shutting down daemon restores pwm1_enable=2 (BIOS Auto)
        ctrl.shutdown()
        self.assertEqual((self.sys_root / "class/hwmon/hwmon6/pwm1_enable").read_text().strip(), "2")

    def test_unix_socket_ipc_roundtrip(self) -> None:
        ctrl = VictusController(self.backend, settings_path=self.settings_path)
        server = VictusIpcServer(ctrl, socket_path=self.socket_path)
        server.start()
        try:
            client = VictusIpcClient(socket_path=self.socket_path)
            state = client.get_state()
            self.assertEqual(state["capabilities"]["board_name"], "8C9C")

            updated = client.set_profile("eco")
            self.assertEqual(updated["profile"], "eco")
            self.assertEqual(
                (self.sys_root / "firmware/acpi/platform_profile").read_text().strip(),
                "low-power",
            )

            cycled = client.cycle_profile()
            self.assertEqual(cycled["profile"], "balanced")

            fan_res = client.set_fan("manual", percent=65)
            self.assertEqual(fan_res["fan_mode"], "manual")
            self.assertEqual(fan_res["fan_percent"], 65)
        finally:
            server.stop()
            ctrl.shutdown()


if __name__ == "__main__":
    unittest.main()
