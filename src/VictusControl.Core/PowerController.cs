namespace VictusControl.Core;

/// <summary>
/// High-level facade: applies a power profile through the minimal
/// WMI call sequence the firmware actually needs.
/// </summary>
public sealed class PowerController(HpWmiClient client)
{
    private readonly HpWmiClient _client = client;

    /// <summary>
    /// Reads SystemData (128 bytes) and returns the thermal policy revision (byte 3).
    /// </summary>
    public ThermalPolicy GetThermalPolicy()
    {
        _client.EnsureSuccess(BiosCommand.SystemRead, null, 128, out byte[]? data);
        if (data is not { Length: >= 4 })
            throw new InvalidOperationException("SystemData response too short.");
        return data[3] == 0x01 ? ThermalPolicy.Modern : ThermalPolicy.Legacy;
    }

    /// <summary>
    /// Switches the firmware performance mode (0x1A). This alone moves
    /// cTGP/PPAB on most Victus boards; GPU write below is belt-and-suspenders.
    /// </summary>
    public void SetFanMode(PowerProfile profile, ThermalPolicy policy)
    {
        byte mode = FanModeCodes.For(profile, policy);
        _client.EnsureSuccess(BiosCommand.FanMode, [0xFF, mode, 0x00, 0x00], 0, out _);
    }

    /// <summary>
    /// Forces the GPU power state for the profile. Sent twice with a short gap:
    /// some firmware revisions ignore the first VRM transition.
    /// </summary>
    public void SetGpuPower(PowerProfile profile)
    {
        byte[] payload = GpuPowerData.For(profile).ToPayload();
        _client.EnsureSuccess(BiosCommand.GpuWrite, payload, 0, out _);
        Thread.Sleep(50);
        _client.EnsureSuccess(BiosCommand.GpuWrite, payload, 0, out _);
    }

    public GpuPowerData GetGpuPower()
    {
        _client.EnsureSuccess(BiosCommand.GpuRead, [0x00, 0x00, 0x00, 0x00], 4, out byte[]? data);
        if (data is not { Length: >= 4 })
            throw new InvalidOperationException("GpuPowerData response too short.");
        return new GpuPowerData(data[0], data[1], data[2], data[3]);
    }

    /// <summary>
    /// Full profile switch: fan mode first, then explicit GPU state.
    /// </summary>
    public void ApplyProfile(PowerProfile profile)
    {
        ThermalPolicy policy = GetThermalPolicy();
        SetFanMode(profile, policy);
        SetGpuPower(profile);
    }
}
