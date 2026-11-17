from __future__ import annotations

from dataclasses import asdict, dataclass, field
from enum import Enum
import json
from pathlib import Path
from typing import Any


class VictusError(Exception):
    """Base structured error for VictusControl."""

    def __init__(self, message: str, code: str = "VICTUS_ERROR") -> None:
        super().__init__(message)
        self.code = code


class ValidationError(VictusError):
    def __init__(self, message: str) -> None:
        super().__init__(message, code="VALIDATION_ERROR")


class HardwareNotFoundError(VictusError):
    def __init__(self, message: str) -> None:
        super().__init__(message, code="HARDWARE_NOT_FOUND")


class PrivilegeRequiredError(VictusError):
    def __init__(self, path: str | Path) -> None:
        super().__init__(
            f"Root privileges required to write '{path}'. Start 'victusd' systemd service or run with sudo.",
            code="PRIVILEGE_REQUIRED",
        )
        self.path = str(path)


class PowerProfile(str, Enum):
    ECO = "eco"
    BALANCED = "balanced"
    PERFORMANCE = "performance"

    @property
    def kernel_profile(self) -> str:
        if self is PowerProfile.ECO:
            return "low-power"
        if self is PowerProfile.BALANCED:
            return "balanced"
        return "performance"

    @property
    def label(self) -> str:
        if self is PowerProfile.ECO:
            return "ECO"
        if self is PowerProfile.BALANCED:
            return "BALANCED"
        return "PERFORMANCE"

    def next_profile(self) -> PowerProfile:
        if self is PowerProfile.ECO:
            return PowerProfile.BALANCED
        if self is PowerProfile.BALANCED:
            return PowerProfile.PERFORMANCE
        return PowerProfile.ECO

    @classmethod
    def parse(cls, value: str) -> PowerProfile:
        norm = value.strip().lower()
        mapping = {
            "eco": cls.ECO,
            "low-power": cls.ECO,
            "quiet": cls.ECO,
            "cool": cls.ECO,
            "balanced": cls.BALANCED,
            "default": cls.BALANCED,
            "bal": cls.BALANCED,
            "performance": cls.PERFORMANCE,
            "perf": cls.PERFORMANCE,
        }
        if norm not in mapping:
            raise ValidationError(f"Unknown profile '{value}'. Expected eco, balanced, or perf.")
        return mapping[norm]

    @classmethod
    def from_kernel(cls, value: str) -> PowerProfile:
        return cls.parse(value)


class FanMode(str, Enum):
    AUTO = "auto"
    CURVE = "curve"
    MANUAL = "manual"
    MAX = "max"

    @property
    def pwm_enable_value(self) -> int:
        if self is FanMode.MAX:
            return 0
        if self in (FanMode.MANUAL, FanMode.CURVE):
            return 1
        return 2

    @classmethod
    def parse(cls, value: str) -> FanMode:
        norm = value.strip().lower()
        mapping = {
            "auto": cls.AUTO,
            "off": cls.AUTO,
            "bios": cls.AUTO,
            "curve": cls.CURVE,
            "smart": cls.CURVE,
            "manual": cls.MANUAL,
            "max": cls.MAX,
            "on": cls.MAX,
            "full": cls.MAX,
        }
        if norm not in mapping:
            raise ValidationError(f"Unknown fan mode '{value}'. Expected auto, curve, max, or manual.")
        return mapping[norm]


def percent_to_pwm(percent: int) -> int:
    if not isinstance(percent, int) or percent < 0 or percent > 100:
        raise ValidationError(f"Fan percent must be 0..100, got {percent}.")
    if percent == 0:
        return 0
    if percent == 100:
        return 255
    return max(1, min(255, round(percent * 255 / 100)))


def pwm_to_percent(pwm: int) -> int:
    clamped = max(0, min(255, int(pwm)))
    return round(clamped * 100 / 255)


@dataclass(frozen=True, slots=True)
class FanCurvePoint:
    temp_c: float
    percent: int

    def __post_init__(self) -> None:
        if self.percent < 1 or self.percent > 100:
            raise ValidationError(f"Curve percent must be 1..100, got {self.percent}.")


class FanCurve:
    """Linearly interpolated temperature curve with downward hysteresis."""

    def __init__(self, points: list[FanCurvePoint], hysteresis_c: float = 3.0) -> None:
        if len(points) < 2:
            raise ValidationError("Fan curve requires at least 2 points.")
        if hysteresis_c < 0:
            raise ValidationError("Hysteresis must be non-negative.")
        self.points: tuple[FanCurvePoint, ...] = tuple(sorted(points, key=lambda p: p.temp_c))
        self.hysteresis_c = float(hysteresis_c)

    @classmethod
    def default_balanced(cls) -> FanCurve:
        return cls(
            [
                FanCurvePoint(42.0, 20),
                FanCurvePoint(55.0, 35),
                FanCurvePoint(68.0, 55),
                FanCurvePoint(78.0, 78),
                FanCurvePoint(86.0, 100),
            ],
            hysteresis_c=3.0,
        )

    def interpolate(self, temp_c: float) -> int:
        pts = self.points
        if temp_c <= pts[0].temp_c:
            return pts[0].percent
        if temp_c >= pts[-1].temp_c:
            return pts[-1].percent

        for idx in range(len(pts) - 1):
            low, high = pts[idx], pts[idx + 1]
            if low.temp_c <= temp_c <= high.temp_c:
                if high.temp_c == low.temp_c:
                    return high.percent
                ratio = (temp_c - low.temp_c) / (high.temp_c - low.temp_c)
                pct = round(low.percent + ratio * (high.percent - low.percent))
                return max(1, min(100, pct))
        return pts[-1].percent

    def evaluate(
        self,
        temp_c: float,
        anchor_temp_c: float | None = None,
        current_percent: int = 0,
    ) -> int:
        raw_pct = self.interpolate(temp_c)
        if anchor_temp_c is not None and current_percent > 0 and raw_pct < current_percent:
            if (anchor_temp_c - temp_c) < self.hysteresis_c:
                return current_percent
        return raw_pct

    def to_list(self) -> list[dict[str, float | int]]:
        return [{"temp_c": p.temp_c, "percent": p.percent} for p in self.points]

    @classmethod
    def from_list(cls, raw: list[dict[str, Any]], hysteresis_c: float = 3.0) -> FanCurve:
        return cls([FanCurvePoint(float(item["temp_c"]), int(item["percent"])) for item in raw], hysteresis_c)


@dataclass(frozen=True, slots=True)
class HardwareCapabilities:
    vendor: str
    product_name: str
    board_name: str
    bios_version: str
    has_platform_profile: bool
    platform_profile_choices: tuple[str, ...]
    has_hp_hwmon: bool
    has_pwm_control: bool
    has_cpu_boost: bool
    has_epp: bool
    scaling_driver: str
    has_battery: bool
    dgpu_pci_address: str | None
    has_prime_select: bool

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["platform_profile_choices"] = list(self.platform_profile_choices)
        return data


@dataclass(frozen=True, slots=True)
class TelemetrySnapshot:
    platform_profile: str
    cpu_temp_c: float
    igpu_temp_c: float | None
    dgpu_temp_c: float | None
    nvme_temp_c: float | None
    fan1_rpm: int
    fan2_rpm: int
    pwm_enable: int | None
    pwm_percent: int | None
    cpu_boost_enabled: bool | None
    cpu_governor: str | None
    cpu_epp: str | None
    on_battery: bool
    battery_status: str | None
    battery_percent: int | None
    battery_watts: float | None
    dgpu_power_state: str | None
    dgpu_holders: tuple[str, ...] = ()
    prime_mode: str | None = None

    def to_dict(self) -> dict[str, Any]:
        data = asdict(self)
        data["dgpu_holders"] = list(self.dgpu_holders)
        return data


@dataclass(slots=True)
class DaemonSettings:
    eco_power_save: bool = True
    eco_60hz_on_battery: bool = True
    auto_eco_on_battery: bool = True
    show_notifications: bool = True
    curve_points: list[dict[str, float | int]] = field(
        default_factory=lambda: FanCurve.default_balanced().to_list()
    )

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)

    @classmethod
    def load(cls, path: Path) -> DaemonSettings:
        try:
            if path.is_file():
                raw = json.loads(path.read_text(encoding="utf-8"))
                return cls(
                    eco_power_save=bool(raw.get("eco_power_save", True)),
                    eco_60hz_on_battery=bool(raw.get("eco_60hz_on_battery", True)),
                    auto_eco_on_battery=bool(raw.get("auto_eco_on_battery", True)),
                    show_notifications=bool(raw.get("show_notifications", True)),
                    curve_points=list(raw.get("curve_points") or FanCurve.default_balanced().to_list()),
                )
        except (OSError, ValueError, TypeError):
            pass
        return cls()

    def save(self, path: Path) -> None:
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(self.to_dict(), indent=2), encoding="utf-8")
        except OSError:
            pass
