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

    // unknown board revisions must not block the app: they get the Legacy table.
    // transport failures (WMI/CIM) still propagate — swallowing them would
    // silently apply the wrong fan-code table on a Modern board.
    public ThermalPolicy GetThermalPolicyOrDefault()
    {
        try
        {
            return GetThermalPolicy();
        }
        catch (NotSupportedException)
        {
            return ThermalPolicy.Legacy;
        }
    }

    /// <summary>Graphics output routing (OmenMon GpuMode, subcommand 0x52).</summary>
    public GpuMode GetGpuMode()
    {
        // OmenMon note: unsupported boards answer firmware error 4 here;
        // read through the tolerant path so callers can feature-gate on it
        int rc = _client.Execute(BiosModule.Legacy, BiosCommand.GpuMode, null, 4, out byte[]? data);
        if (rc != BiosReturnCode.Ok || data is not { Length: >= 1 })
            throw new InvalidOperationException($"GPU mode read failed: {BiosReturnCode.Describe(rc)}.");
        return data[0] switch
        {
            0x00 => GpuMode.Hybrid,
            0x01 => GpuMode.Discrete,
            0x02 => GpuMode.Optimus,
            var raw => (GpuMode)raw,
        };
    }

    /// <summary>Schedules the BIOS graphics mode; takes effect after a reboot.</summary>
    public void SetGpuMode(GpuMode mode)
    {
        byte[] payload = [(byte)mode, 0x00, 0x00, 0x00];
        _client.EnsureSuccess(BiosModule.GpuMode, BiosCommand.GpuMode, payload, 0, out _);
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

    public void SetMaxFan(bool enabled)
    {
        // Victus/OMEN EC requires FanCount query (0x10) to arm user-defined mode
        _client.Execute(BiosCommand.FanCount, [0x00], 4, out _);

        if (enabled)
        {
            byte[] payload = [0x01, 0x00, 0x00, 0x00];
            _client.EnsureSuccess(BiosCommand.MaxFanWrite, payload, 0, out _);
        }
        else
        {
            byte[] maxFanOff = [0x00, 0x00, 0x00, 0x00];
            _client.Execute(BiosCommand.MaxFanWrite, maxFanOff, 0, out _);

            // Victus S-series requires explicit reset to auto (0x00, 0x00)
            byte[] autoCurve = [0x00, 0x00];
            _client.Execute(BiosCommand.VictusFanSpeedSet, autoCurve, 0, out _);
        }
    }

    public (int Fan1Rpm, int Fan2Rpm) GetFanRpm()
    {
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedGet, null, 128, out byte[]? data);
        if (data is not { Length: >= 2 })
            throw new InvalidOperationException("FanLevel response too short.");
        return (data[0] * 100, data[1] * 100);
    }

    // 0 = firmware auto curve, 1..100 = manual duty per fan
    // (OmenMon SetFanLevel format: byte per fan, 0x2E)
    public void SetFanPercent(int fan1Percent, int fan2Percent)
    {
        if (fan1Percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(fan1Percent), "Fan 1 percent must be 0 (auto) or 1..100.");
        if (fan2Percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(fan2Percent), "Fan 2 percent must be 0 (auto) or 1..100.");

        // arm user-defined mode (same as SetMaxFan: Victus/OMEN EC needs the 0x10 probe first)
        _client.Execute(BiosCommand.FanCount, [0x00], 4, out _);
        byte[] payload = [(byte)fan1Percent, (byte)fan2Percent, 0x00, 0x00];
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedSet, payload, 0, out _);
    }

    public (int Fan1Percent, int Fan2Percent) GetFanPercent()
    {
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedGet, null, 128, out byte[]? data);
        if (data is not { Length: >= 2 })
            throw new InvalidOperationException("FanLevel response too short.");
        return (data[0], data[1]);
    }

    public (int CpuTemp, int GpuTemp) GetTemperatures()
    {
        int cpu, gpu;
        bool cpuOk = _client.Execute(BiosCommand.TempRead, [0x00, 0x00, 0x00, 0x00], 4, out byte[]? cpuData) == BiosReturnCode.Ok
            && cpuData is { Length: >= 1 };
        cpu = cpuOk ? cpuData![0] : 0;
        bool gpuOk = _client.Execute(BiosCommand.TempRead, [0x01, 0x00, 0x00, 0x00], 4, out byte[]? gpuData) == BiosReturnCode.Ok
            && gpuData is { Length: >= 1 };
        gpu = gpuOk ? gpuData![0] : 0;

        // one failed sensor is degraded-but-usable; both failed is an error
        if (!cpuOk && !gpuOk)
            throw new InvalidOperationException("Temperature read failed on both sensors.");
        return (cpu, gpu);
    }

    public void ApplyProfile(PowerProfile profile)
    {
        ThermalPolicy policy = GetThermalPolicyOrDefault();
        SetFanMode(profile, policy);
        SetGpuPower(profile);

        PowerProfile actual = GetGpuPower().ToProfile();
        if (actual != profile)
            throw new InvalidOperationException(
                $"Profile {profile} only partially applied (firmware reports {actual}).");
    }

    // boards without the GPU power subcommand (firmware answer "not supported")
    // still get the fan-mode half of the profile
    public void ApplyProfile(PowerProfile profile, BiosCapabilities caps)
    {
        if (!caps.GpuPower)
        {
            SetFanMode(profile, caps.EffectivePolicy);
            return;
        }
        ApplyProfile(profile);
    }
}
