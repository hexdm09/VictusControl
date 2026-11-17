from __future__ import annotations

from pathlib import Path
import unittest

from victus_control.sysfs_backend import LinuxSysfsBackend


class LiveReadonlySmokeTests(unittest.TestCase):
    def test_live_sysfs_read_does_not_throw_or_mutate(self) -> None:
        if not Path("/sys/class/hwmon").exists():
            self.skipTest("No /sys/class/hwmon on this host")
        backend = LinuxSysfsBackend(command_runner=lambda _: (1, ""))
        caps = backend.probe_capabilities()
        snap = backend.read_telemetry(allow_nvidia_smi=False)
        self.assertIsInstance(caps.board_name, str)
        self.assertGreaterEqual(snap.cpu_temp_c, 0.0)


if __name__ == "__main__":
    unittest.main()
