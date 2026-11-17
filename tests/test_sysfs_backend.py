from __future__ import annotations

from pathlib import Path
import tempfile
import unittest

from victus_control.models import FanMode, PowerProfile
from victus_control.sysfs_backend import LinuxSysfsBackend


class FakeSysfsTree:
    def __init__(self, root: Path) -> None:
        self.root = root
        self._populate()

    def _write(self, rel: str, content: str) -> Path:
        p = self.root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text(content, encoding="utf-8")
        return p

    def _populate(self) -> None:
        self._write("devices/virtual/dmi/id/sys_vendor", "HP\n")
        self._write("devices/virtual/dmi/id/product_name", "Victus by HP Gaming Laptop 16-s1xxx\n")
        self._write("devices/virtual/dmi/id/board_name", "8C9C\n")
        self._write("devices/virtual/dmi/id/bios_version", "F.17\n")

        self._write("firmware/acpi/platform_profile_choices", "low-power balanced performance\n")
        self._write("firmware/acpi/platform_profile", "performance\n")

        # hwmon6: hp-wmi fan controller
        self._write("class/hwmon/hwmon6/name", "hp\n")
        self._write("class/hwmon/hwmon6/fan1_input", "1700\n")
        self._write("class/hwmon/hwmon6/fan2_input", "1800\n")
        self._write("class/hwmon/hwmon6/pwm1", "74\n")
        self._write("class/hwmon/hwmon6/pwm1_enable", "2\n")

        # hwmon5: AMD Ryzen k10temp
        self._write("class/hwmon/hwmon5/name", "k10temp\n")
        self._write("class/hwmon/hwmon5/temp1_label", "Tctl\n")
        self._write("class/hwmon/hwmon5/temp1_input", "44625\n")

        # hwmon9: AMD iGPU
        self._write("class/hwmon/hwmon9/name", "amdgpu\n")
        self._write("class/hwmon/hwmon9/temp1_label", "edge\n")
        self._write("class/hwmon/hwmon9/temp1_input", "42000\n")

        # hwmon3: NVMe
        self._write("class/hwmon/hwmon3/name", "nvme\n")
        self._write("class/hwmon/hwmon3/temp1_input", "33000\n")

        # CPU amd-pstate-epp + boost
        for cpu in range(2):
            self._write(f"devices/system/cpu/cpu{cpu}/cpufreq/scaling_driver", "amd-pstate-epp\n")
            self._write(f"devices/system/cpu/cpu{cpu}/cpufreq/scaling_governor", "performance\n")
            self._write(
                f"devices/system/cpu/cpu{cpu}/cpufreq/energy_performance_preference",
                "performance\n",
            )
            self._write(
                f"devices/system/cpu/cpu{cpu}/cpufreq/energy_performance_available_preferences",
                "default performance balance_performance balance_power power\n",
            )
        self._write("devices/system/cpu/cpufreq/boost", "1\n")

        # Power supply: unplugged, discharging at 9.4 W
        self._write("class/power_supply/ACAD/type", "Mains\n")
        self._write("class/power_supply/ACAD/online", "0\n")
        self._write("class/power_supply/BAT1/type", "Battery\n")
        self._write("class/power_supply/BAT1/status", "Discharging\n")
        self._write("class/power_supply/BAT1/capacity", "84\n")
        self._write("class/power_supply/BAT1/power_now", "9400000\n")

        # GPUs: NVIDIA dGPU suspended in D3cold + AMD iGPU active
        self._write("bus/pci/devices/0000:01:00.0/vendor", "0x10de\n")
        self._write("bus/pci/devices/0000:01:00.0/device", "0x2820\n")
        self._write("bus/pci/devices/0000:01:00.0/class", "0x030000\n")
        self._write("bus/pci/devices/0000:01:00.0/power/runtime_status", "suspended\n")
        self._write("bus/pci/devices/0000:01:00.0/power/control", "auto\n")

        self._write("bus/pci/devices/0000:05:00.0/vendor", "0x1002\n")
        self._write("bus/pci/devices/0000:05:00.0/device", "0x1900\n")
        self._write("bus/pci/devices/0000:05:00.0/class", "0x030000\n")
        self._write("bus/pci/devices/0000:05:00.0/power/runtime_status", "active\n")


class LinuxSysfsBackendTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.sys_root = Path(self._tmp.name) / "sys"
        self.tree = FakeSysfsTree(self.sys_root)
        self.commands_called: list[list[str]] = []

        def fake_runner(cmd: list[str]) -> tuple[int, str]:
            self.commands_called.append(cmd)
            return (0, "49\n")

        self.backend = LinuxSysfsBackend(sys_root=self.sys_root, command_runner=fake_runner)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_probe_capabilities_detects_8c9c_hardware(self) -> None:
        caps = self.backend.probe_capabilities()
        self.assertEqual(caps.board_name, "8C9C")
        self.assertTrue(caps.has_platform_profile)
        self.assertTrue(caps.has_hp_hwmon)
        self.assertTrue(caps.has_pwm_control)
        self.assertTrue(caps.has_cpu_boost)
        self.assertTrue(caps.has_epp)
        self.assertTrue(caps.has_battery)
        self.assertEqual(caps.dgpu_pci_address, "0000:01:00.0")

    def test_read_telemetry_never_calls_nvidia_smi_when_dgpu_suspended(self) -> None:
        snap = self.backend.read_telemetry()
        self.assertAlmostEqual(snap.cpu_temp_c, 44.6, places=1)
        self.assertAlmostEqual(snap.igpu_temp_c or 0.0, 42.0, places=1)
        self.assertEqual(snap.fan1_rpm, 1700)
        self.assertEqual(snap.fan2_rpm, 1800)
        self.assertEqual(snap.dgpu_power_state, "suspended")
        self.assertIsNone(snap.dgpu_temp_c)
        self.assertTrue(snap.on_battery)
        self.assertEqual(snap.battery_percent, 84)
        self.assertAlmostEqual(snap.battery_watts or 0.0, -9.4, places=1)
        # Crucial battery invariant: must NEVER invoke nvidia-smi on a suspended dGPU!
        self.assertEqual(self.commands_called, [])

    def test_apply_eco_profile_sets_low_power_epp_and_disables_cpu_boost(self) -> None:
        self.backend.apply_profile(PowerProfile.ECO, eco_power_save=True)

        self.assertEqual(
            (self.sys_root / "firmware/acpi/platform_profile").read_text().strip(),
            "low-power",
        )
        self.assertEqual(
            (self.sys_root / "devices/system/cpu/cpufreq/boost").read_text().strip(),
            "0",
        )
        for cpu in range(2):
            gov = (self.sys_root / f"devices/system/cpu/cpu{cpu}/cpufreq/scaling_governor").read_text().strip()
            epp = (
                self.sys_root / f"devices/system/cpu/cpu{cpu}/cpufreq/energy_performance_preference"
            ).read_text().strip()
            self.assertEqual(gov, "powersave")
            self.assertEqual(epp, "power")

    def test_apply_performance_profile_restores_boost_and_perf_epp(self) -> None:
        self.backend.apply_profile(PowerProfile.ECO, eco_power_save=True)
        self.backend.apply_profile(PowerProfile.PERFORMANCE, eco_power_save=True)

        self.assertEqual(
            (self.sys_root / "firmware/acpi/platform_profile").read_text().strip(),
            "performance",
        )
        self.assertEqual(
            (self.sys_root / "devices/system/cpu/cpufreq/boost").read_text().strip(),
            "1",
        )
        epp = (self.sys_root / "devices/system/cpu/cpu0/cpufreq/energy_performance_preference").read_text().strip()
        self.assertEqual(epp, "performance")

    def test_fan_modes_write_correct_hwmon_values(self) -> None:
        self.backend.set_fan_mode(FanMode.MAX)
        self.assertEqual((self.sys_root / "class/hwmon/hwmon6/pwm1_enable").read_text().strip(), "0")

        self.backend.set_fan_mode(FanMode.MANUAL, percent=50)
        self.assertEqual((self.sys_root / "class/hwmon/hwmon6/pwm1_enable").read_text().strip(), "1")
        pwm = int((self.sys_root / "class/hwmon/hwmon6/pwm1").read_text().strip())
        self.assertTrue(125 <= pwm <= 130)

        self.backend.set_fan_mode(FanMode.AUTO)
        self.assertEqual((self.sys_root / "class/hwmon/hwmon6/pwm1_enable").read_text().strip(), "2")


if __name__ == "__main__":
    unittest.main()
