namespace VictusControl.Core;

/// <summary>
/// High-level facade: applies a power profile through the minimal
/// WMI call sequence the firmware actually needs.
/// </summary>
public sealed class PowerController
{
    /// <summary>Constant flag byte heading every FanMode (0x1A) payload.</summary>
    private const byte FanModePrefix = 0xFF;

    /// <summary>Some firmware revisions ignore the first VRM transition; resend after this delay.</summary>
    private const int GpuWriteRetryDelayMs = 50;

    /// <summary>The firmware requires a 4-byte placeholder payload even for read commands.</summary>
    private static readonly byte[] GpuReadRequest = [0x00, 0x00, 0x00, 0x00];

    private readonly HpWmiClient _client;

    public PowerController(HpWmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Reads SystemData (128 bytes) and returns the thermal policy revision (byte 3).
    /// Throws on unknown revisions instead of guessing a code table (fail-closed).
    /// </summary>
    public ThermalPolicy GetThermalPolicy()
    {
        _client.EnsureSuccess(BiosCommand.SystemRead, null, 128, out byte[]? data);
        if (data is not { Length: >= 4 })
            throw new InvalidOperationException("SystemData response too short.");
        return data[3] switch
        {
            0x01 => ThermalPolicy.Modern,
            0x00 => ThermalPolicy.Legacy,
            var unknown => throw new NotSupportedException($"Unknown thermal policy 0x{unknown:X2}."),
        };
    }

    /// <summary>
    /// Switches the firmware performance mode (0x1A). This alone moves
    /// cTGP/PPAB on most Victus boards; GPU write below is belt-and-suspenders.
    /// </summary>
    public void SetFanMode(PowerProfile profile, ThermalPolicy policy)
    {
        byte mode = FanModeCodes.For(profile, policy);
        _client.EnsureSuccess(BiosCommand.FanMode, [FanModePrefix, mode, 0x00, 0x00], 0, out _);
    }

    /// <summary>
    /// Forces the GPU power state for the profile. Sent twice with a short gap:
    /// some firmware revisions ignore the first VRM transition.
    /// </summary>
    public void SetGpuPower(PowerProfile profile)
    {
        byte[] payload = GpuPowerData.For(profile).ToPayload();
        _client.EnsureSuccess(BiosCommand.GpuWrite, payload, 0, out _);
        Thread.Sleep(GpuWriteRetryDelayMs);
        _client.EnsureSuccess(BiosCommand.GpuWrite, payload, 0, out _);
    }

    public GpuPowerData GetGpuPower()
    {
        _client.EnsureSuccess(BiosCommand.GpuRead, GpuReadRequest, 4, out byte[]? data);
        if (data is not { Length: >= 4 })
            throw new InvalidOperationException("GpuPowerData response too short.");
        return new GpuPowerData(data[0], data[1], data[2], data[3]);
    }

    /// <summary>
    /// Full profile switch: fan mode first, then explicit GPU state,
    /// then verify-after-write so a half-applied mix (new fans + old GPU)
    /// surfaces as an error instead of silent state.
    /// </summary>
    public void ApplyProfile(PowerProfile profile)
    {
        ThermalPolicy policy = GetThermalPolicy();
        SetFanMode(profile, policy);
        SetGpuPower(profile);

        PowerProfile actual = GetGpuPower().ToProfile();
        if (actual != profile)
            throw new InvalidOperationException(
                $"Profile {profile} only partially applied (firmware reports {actual}).");
    }
}
