namespace VictusControl.Core;

// thermal policy reported by board (SystemData byte 3)
public enum ThermalPolicy : byte
{
    Legacy = 0x00,
    Modern = 0x01,
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

// 0x1A fan mode codes per thermal policy.
// extensible table: owners of rare board revisions can append entries
// without touching the switching logic.
public static class FanModeCodes
{
    private static readonly Dictionary<(ThermalPolicy Policy, PowerProfile Profile), byte> Codes = new()
    {
        // Modern V1 boards (2023+, e.g. 8C9C)
        [(ThermalPolicy.Modern, PowerProfile.Eco)] = 0x50,
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

// cTGP / PPAB dynamic boost payload
public sealed record GpuPowerData(byte CustomTgp, byte Ppab, byte DState = 0x01, byte PeakTemperature = 0x00)
{
    public static GpuPowerData For(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => new(0x00, 0x00),
        PowerProfile.Balanced => new(0x00, 0x01),
        PowerProfile.Performance => new(0x01, 0x01),
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

// payload for BiosCommand 0x29 (AMD boards use firmware auto limits by default)
public sealed record CpuPowerData(byte Limit1, byte Limit2, byte Limit4 = 0xFF, byte LimitWithGpu = 0xFF)
{
    public byte[] ToPayload() => [Limit1, Limit2, Limit4, LimitWithGpu];
}
