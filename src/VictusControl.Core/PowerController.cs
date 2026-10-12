namespace VictusControl.Core;

public sealed class PowerController
{
    private const byte FanModePrefix = 0xFF;
    // vrm on some revisions ignores the first transition
    private const int GpuWriteRetryDelayMs = 50;
    // firmware expects a 4-byte placeholder even on reads
    private static readonly byte[] GpuReadRequest = [0x00, 0x00, 0x00, 0x00];

    private readonly IHpWmiClient _client;

    public PowerController(IHpWmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

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

    public void SetFanMode(PowerProfile profile, ThermalPolicy policy)
    {
        byte mode = FanModeCodes.For(profile, policy);
        _client.EnsureSuccess(BiosCommand.FanMode, [FanModePrefix, mode, 0x00, 0x00], 0, out _);
    }

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
