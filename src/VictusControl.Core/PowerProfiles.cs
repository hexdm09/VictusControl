namespace VictusControl.Core;

/// <summary>
/// Thermal policy revision reported by the board (SystemData byte 3).
/// Selects which FanMode code table the firmware understands.
/// </summary>
public enum ThermalPolicy : byte
{
    Legacy = 0x00,
    Modern = 0x01,
}

/// <summary>
/// The three user-facing power profiles.
/// </summary>
public enum PowerProfile
{
    Eco,
    Balanced,
    Performance,
}

public static class PowerProfileExtensions
{
    /// <summary>
    /// Single rotation source: Eco -&gt; Balanced -&gt; Performance -&gt; Eco.
    /// </summary>
    public static PowerProfile Next(this PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => PowerProfile.Balanced,
        PowerProfile.Balanced => PowerProfile.Performance,
        _ => PowerProfile.Eco,
    };
}

/// <summary>
/// FanMode payload codes per thermal policy.
/// Modern (V1) covers Victus/OMEN 2022+ including board 8C9C.
/// </summary>
public static class FanModeCodes
{
    public static byte For(PowerProfile profile, ThermalPolicy policy) => policy switch
    {
        ThermalPolicy.Modern => profile switch
        {
            PowerProfile.Eco => 0x50,
            PowerProfile.Balanced => 0x30,
            PowerProfile.Performance => 0x31,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        },
        ThermalPolicy.Legacy => profile switch
        {
            PowerProfile.Eco => 0x03,
            PowerProfile.Balanced => 0x00,
            PowerProfile.Performance => 0x01,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        },
        _ => throw new NotSupportedException($"Unknown thermal policy 0x{(byte)policy:X2}."),
    };

    public static PowerProfile? Parse(byte code) => code switch
    {
        0x50 or 0x03 => PowerProfile.Eco,
        0x30 or 0x00 => PowerProfile.Balanced,
        0x31 or 0x01 => PowerProfile.Performance,
        _ => null,
    };
}

/// <summary>
/// GpuPowerData payload (cTGP / PPAB Dynamic Boost / DState).
/// </summary>
public sealed record GpuPowerData(byte CustomTgp, byte Ppab, byte DState = 0x01, byte PeakTemperature = 0x00)
{
    public static GpuPowerData For(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => new(0x00, 0x00),
        PowerProfile.Balanced => new(0x00, 0x01),
        PowerProfile.Performance => new(0x01, 0x01),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>
    /// Single detection source: maps a live (cTGP, PPAB) reading to a profile.
    /// </summary>
    public PowerProfile ToProfile() => (CustomTgp, Ppab) switch
    {
        (0x01, 0x01) => PowerProfile.Performance,
        (_, 0x01) => PowerProfile.Balanced,
        _ => PowerProfile.Eco,
    };

    public byte[] ToPayload() => [CustomTgp, Ppab, DState, PeakTemperature];
}

/// <summary>
/// CpuPowerData payload for BiosCommand 0x29. Reserved, no caller yet:
/// the AMD test board stays on firmware auto-limits (0xFF = don't touch).
/// </summary>
public sealed record CpuPowerData(byte Limit1, byte Limit2, byte Limit4 = 0xFF, byte LimitWithGpu = 0xFF)
{
    public byte[] ToPayload() => [Limit1, Limit2, Limit4, LimitWithGpu];
}
