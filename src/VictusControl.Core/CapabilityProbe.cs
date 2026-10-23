namespace VictusControl.Core;

// result of a one-shot capability probe: what this particular board supports.
// every entry is derived from a READ-ONLY WMI call, so probing is safe.
public sealed record BiosCapabilities(
    ThermalPolicy? Policy,
    bool GpuPower,
    bool FanLevels,
    bool Temperatures,
    bool MaxFan,
    bool FanCount,
    bool GpuModeSwitch)
{
    public static BiosCapabilities None { get; } = new(null, false, false, false, false, false, false);

    public ThermalPolicy EffectivePolicy => Policy ?? ThermalPolicy.Legacy;
}

// probes readable subcommands once and maps firmware "not supported"
// answers (and WMI faults) to feature flags instead of exceptions.
// every probe mirrors the response size of the real call: some boards
// reject e.g. the fan-level read when asked through hpqBIOSInt4.
public static class CapabilityProbe
{
    public static BiosCapabilities Probe(IHpWmiClient client)
    {
        return new BiosCapabilities(
            Policy: ProbePolicy(client),
            GpuPower: ProbeRead(client, BiosCommand.GpuRead, [0x00, 0x00, 0x00, 0x00], 4),
            FanLevels: ProbeRead(client, BiosCommand.VictusFanSpeedGet, null, 128),
            Temperatures: ProbeRead(client, BiosCommand.TempRead, [0x00, 0x00, 0x00, 0x00], 4),
            MaxFan: ProbeRead(client, BiosCommand.MaxFanRead, null, 4),
            FanCount: ProbeRead(client, BiosCommand.FanCount, [0x00], 4),
            GpuModeSwitch: ProbeRead(client, BiosCommand.GpuMode, null, 4, BiosModule.Legacy));
    }

    private static ThermalPolicy? ProbePolicy(IHpWmiClient client)
    {
        try
        {
            return client.Execute(BiosCommand.SystemRead, null, 128, out byte[]? data) == BiosReturnCode.Ok
                && data is { Length: > 3 }
                ? data[3] switch
                {
                    0x01 => ThermalPolicy.Modern,
                    0x00 => ThermalPolicy.Legacy,
                    _ => null, // unknown board revision: callers fall back to Legacy
                }
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool ProbeRead(IHpWmiClient client, BiosCommand command, byte[]? payload, int responseSize, uint module = BiosModule.Gaming)
    {
        try
        {
            return client.Execute(module, command, payload, responseSize, out byte[]? data) == BiosReturnCode.Ok
                && data is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }
}
