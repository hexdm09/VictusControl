namespace VictusControl.Core;

// thermal policy / board family reported by firmware
public enum ThermalPolicy : byte
{
    Legacy = 0x00,
    Modern = 0x01,
    VictusS = 0x02,
}

public enum PowerProfile
{
    Eco,
    Balanced,
    Performance,
}

// graphics routing (BIOS setting, applies after reboot; NOT Advanced Optimus)
public enum GpuMode : byte
{
    Hybrid = 0x00,
    Discrete = 0x01,
    Optimus = 0x02,
}

public static class GpuModeExtensions
{
    public static string Describe(this GpuMode mode) => mode switch
    {
        GpuMode.Hybrid => "Hybrid (iGPU + dGPU, automatic)",
        GpuMode.Discrete => "Discrete (dGPU only, max performance)",
        GpuMode.Optimus => "Optimus",
        _ => $"Unknown (0x{(byte)mode:X2})",
    };

    public static GpuMode? Parse(string name) => name.ToLowerInvariant() switch
    {
        "hybrid" or "auto" or "optimus-hybrid" => GpuMode.Hybrid,
        "discrete" or "dgpu" or "performance" => GpuMode.Discrete,
        _ => null,
    };
}

public static class PowerProfileExtensions
{
    // Eco -> Balanced -> Performance -> Eco
    public static PowerProfile Next(this PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => PowerProfile.Balanced,
        PowerProfile.Balanced => PowerProfile.Performance,
        _ => PowerProfile.Eco,
    };
}

// 0x1A fan/thermal mode codes per board family (verified against Linux 7.0 hp-wmi).
// Note: Victus S-series boards (e.g. 8C9C) use 0x00 (Eco/Balanced) and 0x01 (Performance)
// sent as a 2-byte [0xFF, code] packet after arming 0x10; OMEN Modern V1 uses 0x30/0x31.
public static class FanModeCodes
{
    private static readonly Dictionary<(ThermalPolicy Policy, PowerProfile Profile), byte> Codes = new()
    {
        // Victus S-series boards (0x2F fan table present, e.g. 8C9C)
        [(ThermalPolicy.VictusS, PowerProfile.Eco)] = 0x00,
        [(ThermalPolicy.VictusS, PowerProfile.Balanced)] = 0x00,
        [(ThermalPolicy.VictusS, PowerProfile.Performance)] = 0x01,
        // OMEN Modern V1 boards
        [(ThermalPolicy.Modern, PowerProfile.Eco)] = 0x30,
        [(ThermalPolicy.Modern, PowerProfile.Balanced)] = 0x30,
        [(ThermalPolicy.Modern, PowerProfile.Performance)] = 0x31,
        // Legacy V0 boards
        [(ThermalPolicy.Legacy, PowerProfile.Eco)] = 0x03,
        [(ThermalPolicy.Legacy, PowerProfile.Balanced)] = 0x00,
        [(ThermalPolicy.Legacy, PowerProfile.Performance)] = 0x01,
    };

    public static byte For(PowerProfile profile, ThermalPolicy policy) =>
        Codes.TryGetValue((policy, profile), out byte code)
            ? code
            : throw new NotSupportedException($"No fan mode code for {profile} on {policy} board.");

    public static PowerProfile? Parse(byte code) => code switch
    {
        0x50 or 0x03 => PowerProfile.Eco,
        0x30 or 0x00 => PowerProfile.Balanced,
        0x31 or 0x01 => PowerProfile.Performance,
        _ => null,
    };
}

// Hardware fan table parsed from subcommand 0x2F (HPWMI_VICTUS_S_GET_FAN_TABLE_QUERY).
// Firmware expects 0x2E speeds in units of 100 RPM (MinKrpm..MaxKrpm), NOT 1..100%.
public sealed record VictusFanTable(byte MinKrpm, byte MaxKrpm, int GpuDelta)
{
    public static VictusFanTable Default { get; } = new(17, 58, 0);

    public static VictusFanTable? TryParse(byte[]? raw)
    {
        if (raw is not { Length: >= 5 } || raw[0] == 0)
            return null;

        byte min = 0xFF;
        byte max = 0;
        int limit = Math.Min(raw.Length - 2, 126);

        for (int offset = 2; offset + 2 < limit + 2; offset += 3)
        {
            byte cpu = raw[offset];
            byte gpu = raw[offset + 1];
            byte level = raw[offset + 2];
            if ((cpu | gpu | level) == 0)
                break;
            if (cpu < min)
                min = cpu;
            if (cpu > max)
                max = cpu;
        }

        if (min == 0xFF || max == 0)
            return null;

        int gpuDelta = raw[3] - raw[2];
        return new VictusFanTable(min, max, gpuDelta);
    }

    public (byte CpuKrpm, byte GpuKrpm) PercentToKrpm(int fan1Percent, int fan2Percent)
    {
        byte cpu = ScaleSingle(fan1Percent);
        byte gpuBase = ScaleSingle(fan2Percent);
        int gpuAdjusted = fan1Percent == fan2Percent
            ? Math.Clamp(gpuBase + GpuDelta, 0, 255)
            : gpuBase;
        return (cpu, (byte)gpuAdjusted);
    }

    private byte ScaleSingle(int percent)
    {
        if (percent <= 0)
            return 0;
        if (percent >= 100)
            return MaxKrpm;
        double ratio = (percent - 1) / 99.0;
        int krpm = (int)Math.Round(MinKrpm + ratio * (MaxKrpm - MinKrpm));
        return (byte)Math.Clamp(krpm, MinKrpm, MaxKrpm);
    }
}

public sealed record FanCurvePoint(int TempC, int Percent);

// Temperature-to-percent fan curve with downward hysteresis to prevent hunting.
public sealed class FanCurve
{
    public IReadOnlyList<FanCurvePoint> Points { get; }
    public int HysteresisC { get; }

    public static FanCurve BalancedDefault { get; } = new([
        new(42, 20),
        new(55, 35),
        new(68, 55),
        new(78, 78),
        new(86, 100),
    ]);

    public FanCurve(IEnumerable<FanCurvePoint> points, int hysteresisC = 3)
    {
        ArgumentNullException.ThrowIfNull(points);
        var sorted = points.OrderBy(p => p.TempC).ToArray();
        if (sorted.Length < 2)
            throw new ArgumentException("Fan curve requires at least 2 points.", nameof(points));
        if (hysteresisC < 0)
            throw new ArgumentOutOfRangeException(nameof(hysteresisC));
        Points = sorted;
        HysteresisC = hysteresisC;
    }

    public int Evaluate(int currentTempC, int? lastAnchorTempC = null, int lastPercent = 0)
    {
        int rawTarget = Interpolate(currentTempC);
        if (lastAnchorTempC is int prevTemp && lastPercent > 0 && rawTarget < lastPercent)
        {
            if (prevTemp - currentTempC < HysteresisC)
                return lastPercent;
        }
        return rawTarget;
    }

    public int Interpolate(int tempC)
    {
        if (tempC <= Points[0].TempC)
            return Math.Clamp(Points[0].Percent, 1, 100);
        if (tempC >= Points[^1].TempC)
            return Math.Clamp(Points[^1].Percent, 1, 100);

        for (int i = 0; i < Points.Count - 1; i++)
        {
            FanCurvePoint a = Points[i];
            FanCurvePoint b = Points[i + 1];
            if (tempC >= a.TempC && tempC <= b.TempC)
            {
                if (b.TempC == a.TempC)
                    return Math.Clamp(b.Percent, 1, 100);
                double t = (double)(tempC - a.TempC) / (b.TempC - a.TempC);
                int pct = (int)Math.Round(a.Percent + t * (b.Percent - a.Percent));
                return Math.Clamp(pct, 1, 100);
            }
        }
        return Math.Clamp(Points[^1].Percent, 1, 100);
    }
}

// cTGP / PPAB dynamic boost payload (0x21 / 0x22).
// PeakTemperature preserves the factory GPU slowdown threshold read from byte 3 of 0x21 (default 87°C = 0x57).
public sealed record GpuPowerData(byte CustomTgp, byte Ppab, byte DState = 0x01, byte PeakTemperature = GpuPowerData.DefaultPeakTemp)
{
    public const byte DefaultPeakTemp = 0x57;

    public static GpuPowerData For(PowerProfile profile, byte peakTemperature = DefaultPeakTemp) => profile switch
    {
        PowerProfile.Eco => new(0x00, 0x00, 0x01, peakTemperature),
        PowerProfile.Balanced => new(0x00, 0x01, 0x01, peakTemperature),
        PowerProfile.Performance => new(0x01, 0x01, 0x01, peakTemperature),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    public PowerProfile ToProfile() => (CustomTgp, Ppab) switch
    {
        (0x01, 0x01) => PowerProfile.Performance,
        (_, 0x01) => PowerProfile.Balanced,
        _ => PowerProfile.Eco,
    };

    public byte[] ToPayload() => [CustomTgp, Ppab, DState, PeakTemperature];
}

// payload for BiosCommand 0x29 (HPWMI_SET_POWER_LIMITS_QUERY).
// [0x00, 0x00, 0xFF, 0xFF] resets stuck PL1/PL2 overrides back to firmware battery/eco limits.
public sealed record CpuPowerData(byte Limit1, byte Limit2, byte Limit4 = 0xFF, byte LimitWithGpu = 0xFF)
{
    public static CpuPowerData FirmwareAutoReset { get; } = new(0x00, 0x00, 0xFF, 0xFF);

    public byte[] ToPayload() => [Limit1, Limit2, Limit4, LimitWithGpu];
}
