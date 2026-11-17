from __future__ import annotations

import unittest

from victus_control.models import (
    FanCurve,
    FanCurvePoint,
    FanMode,
    PowerProfile,
    PrivilegeRequiredError,
    ValidationError,
    percent_to_pwm,
    pwm_to_percent,
)


class ModelsAndCurveTests(unittest.TestCase):
    def test_profile_cycle_order(self) -> None:
        self.assertEqual(PowerProfile.ECO.next_profile(), PowerProfile.BALANCED)
        self.assertEqual(PowerProfile.BALANCED.next_profile(), PowerProfile.PERFORMANCE)
        self.assertEqual(PowerProfile.PERFORMANCE.next_profile(), PowerProfile.ECO)

    def test_profile_kernel_mapping(self) -> None:
        self.assertEqual(PowerProfile.ECO.kernel_profile, "low-power")
        self.assertEqual(PowerProfile.BALANCED.kernel_profile, "balanced")
        self.assertEqual(PowerProfile.PERFORMANCE.kernel_profile, "performance")
        self.assertEqual(PowerProfile.from_kernel("low-power"), PowerProfile.ECO)
        self.assertEqual(PowerProfile.from_kernel("quiet"), PowerProfile.ECO)
        self.assertEqual(PowerProfile.from_kernel("balanced"), PowerProfile.BALANCED)
        self.assertEqual(PowerProfile.from_kernel("performance"), PowerProfile.PERFORMANCE)

    def test_fan_mode_hwmon_values(self) -> None:
        self.assertEqual(FanMode.MAX.pwm_enable_value, 0)
        self.assertEqual(FanMode.MANUAL.pwm_enable_value, 1)
        self.assertEqual(FanMode.CURVE.pwm_enable_value, 1)
        self.assertEqual(FanMode.AUTO.pwm_enable_value, 2)

    def test_percent_pwm_conversion(self) -> None:
        self.assertEqual(percent_to_pwm(0), 0)
        self.assertEqual(percent_to_pwm(100), 255)
        mid = percent_to_pwm(50)
        self.assertTrue(125 <= mid <= 130)
        self.assertEqual(pwm_to_percent(255), 100)
        self.assertEqual(pwm_to_percent(0), 0)
        with self.assertRaises(ValidationError):
            percent_to_pwm(101)
        with self.assertRaises(ValidationError):
            percent_to_pwm(-1)

    def test_fan_curve_interpolation_and_hysteresis(self) -> None:
        curve = FanCurve.default_balanced()
        low = curve.evaluate(35.0)
        self.assertEqual(low, 20)
        high = curve.evaluate(90.0)
        self.assertEqual(high, 100)

        p75 = curve.evaluate(75.0)
        # Drop by 1.5°C (< 3.0°C hysteresis) must keep previous speed
        held = curve.evaluate(73.5, anchor_temp_c=75.0, current_percent=p75)
        self.assertEqual(held, p75)

        # Drop by 5°C (>= 3.0°C hysteresis) steps down
        dropped = curve.evaluate(70.0, anchor_temp_c=75.0, current_percent=p75)
        self.assertLess(dropped, p75)

        # Rise by 1°C responds immediately without waiting for hysteresis
        risen = curve.evaluate(77.0, anchor_temp_c=75.0, current_percent=p75)
        self.assertGreater(risen, p75)

    def test_fan_curve_validation(self) -> None:
        with self.assertRaises(ValidationError):
            FanCurve([FanCurvePoint(50, 40)])
        err = PrivilegeRequiredError("/sys/class/hwmon/hwmon6/pwm1_enable")
        self.assertEqual(err.code, "PRIVILEGE_REQUIRED")


if __name__ == "__main__":
    unittest.main()
