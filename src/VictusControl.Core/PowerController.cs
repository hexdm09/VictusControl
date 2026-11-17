namespace VictusControl.Core;

public sealed class PowerController
{
    private const byte FanModePrefix = 0xFF;
    // vrm on some revisions ignores the first transition
    private const int GpuWriteRetryDelayMs = 50;
    // firmware expects a 4-byte placeholder even on reads
    private static readonly byte[] GpuReadRequest = [0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] MaxFanOnPayload = [0x01, 0x00, 0x00, 0x00];
    private static readonly byte[] MaxFanOffPayload = [0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] AutoFanSpeedPayload = [0x00, 0x00];

    private readonly IHpWmiClient _client;

    public PowerController(IHpWmiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public ThermalPolicy GetThermalPolicy()
    {
        if (_client.Execute(BiosCommand.VictusFanTableGet, GpuReadRequest, 128, out byte[]? tableData) == BiosReturnCode.Ok
            && VictusFanTable.TryParse(tableData) is not null)
        {
            return ThermalPolicy.VictusS;
        }

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
    // transport failures (WMI/CIM) still propagate.
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

    public VictusFanTable GetFanTableOrDefault()
    {
        try
        {
            if (_client.Execute(BiosCommand.VictusFanTableGet, GpuReadRequest, 128, out byte[]? data) == BiosReturnCode.Ok
                && VictusFanTable.TryParse(data) is { } parsed)
            {
                return parsed;
            }
        }
        catch
        {
            // fall back to standard Victus S bounds
        }
        return VictusFanTable.Default;
    }

    /// <summary>Graphics output routing (OmenMon GpuMode, subcommand 0x52).</summary>
    public GpuMode GetGpuMode()
    {
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
        if (policy == ThermalPolicy.VictusS)
        {
            // Victus S (hp-wmi platform_profile_victus_s_set_ec): arm 0x10 then send 2-byte [0xFF, mode]
            ArmFanControl();
            _client.EnsureSuccess(BiosCommand.FanMode, [FanModePrefix, mode], 0, out _);
        }
        else
        {
            _client.EnsureSuccess(BiosCommand.FanMode, [FanModePrefix, mode, 0x00, 0x00], 0, out _);
        }
    }

    public void SetGpuPower(PowerProfile profile)
    {
        // Preserve factory GPU slowdown threshold (byte 3 of 0x21, e.g. 87°C on 8C9C)
        byte peakTemp = GpuPowerData.DefaultPeakTemp;
        if (_client.Execute(BiosCommand.GpuRead, GpuReadRequest, 4, out byte[]? current) == BiosReturnCode.Ok
            && current is { Length: >= 4 }
            && current[3] > 0)
        {
            peakTemp = current[3];
        }

        byte[] payload = GpuPowerData.For(profile, peakTemp).ToPayload();
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

    public void ResetCpuPowerLimitsToAuto()
    {
        int rc = _client.Execute(BiosCommand.CpuWrite, CpuPowerData.FirmwareAutoReset.ToPayload(), 0, out _);
        if (rc is not (BiosReturnCode.Ok or BiosReturnCode.NotSupported))
            throw new InvalidOperationException($"CPU power limit reset failed: {BiosReturnCode.Describe(rc)}.");
    }

    public void SetMaxFan(bool enabled)
    {
        ArmFanControl();

        if (enabled)
        {
            _client.EnsureSuccess(BiosCommand.MaxFanWrite, MaxFanOnPayload, 0, out _);
        }
        else
        {
            // Exact Linux 7.0 hp-wmi PWM_MODE_AUTO reset sequence:
            // 1. Clear MaxFan (0x27 = 0)
            EnsureOkOrUnsupported(BiosCommand.MaxFanWrite, MaxFanOffPayload, 0);
            // 2. Re-arm 0x10 and write 2-byte [0x00, 0x00] to 0x2E to hand control back to EC curve
            ArmFanControl();
            EnsureOkOrUnsupported(BiosCommand.VictusFanSpeedSet, AutoFanSpeedPayload, 0);
        }
    }

    public (int Fan1Rpm, int Fan2Rpm) GetFanRpm()
    {
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedGet, null, 128, out byte[]? data);
        if (data is not { Length: >= 2 })
            throw new InvalidOperationException("FanLevel response too short.");
        return (data[0] * 100, data[1] * 100);
    }

    // 0 = firmware auto curve, 1..100 = manual duty scaled against 0x2F hardware fan table (RPM/100)
    public void SetFanPercent(int fan1Percent, int fan2Percent, VictusFanTable? table = null)
    {
        if (fan1Percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(fan1Percent), "Fan 1 percent must be 0 (auto) or 1..100.");
        if (fan2Percent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(fan2Percent), "Fan 2 percent must be 0 (auto) or 1..100.");

        if (fan1Percent == 0 && fan2Percent == 0)
        {
            SetMaxFan(false);
            return;
        }

        VictusFanTable effectiveTable = table ?? GetFanTableOrDefault();
        var (cpuKrpm, gpuKrpm) = effectiveTable.PercentToKrpm(fan1Percent, fan2Percent);

        // Exact Linux 7.0 hp_wmi_fan_speed_set contract:
        // 1. Arm via 0x10 (1 byte)
        // 2. Clear 0x27 MaxFan lock (4 bytes)
        // 3. Write 2-byte [cpuKrpm, gpuKrpm] to 0x2E
        ArmFanControl();
        EnsureOkOrUnsupported(BiosCommand.MaxFanWrite, MaxFanOffPayload, 0);
        byte[] payload = [cpuKrpm, gpuKrpm];
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedSet, payload, 0, out _);
    }

    public (int Fan1Krpm, int Fan2Krpm) GetFanKrpm()
    {
        _client.EnsureSuccess(BiosCommand.VictusFanSpeedGet, null, 128, out byte[]? data);
        if (data is not { Length: >= 2 })
            throw new InvalidOperationException("FanLevel response too short.");
        return (data[0], data[1]);
    }

    public (int CpuTemp, int GpuTemp) GetTemperatures(bool includeGpu = true)
    {
        bool cpuOk = _client.Execute(BiosCommand.TempRead, [0x00, 0x00, 0x00, 0x00], 4, out byte[]? cpuData) == BiosReturnCode.Ok
            && cpuData is { Length: >= 1 };
        int cpu = cpuOk ? cpuData![0] : 0;

        if (!includeGpu)
        {
            if (!cpuOk)
                throw new InvalidOperationException("CPU temperature read failed.");
            return (cpu, 0);
        }

        bool gpuOk = _client.Execute(BiosCommand.TempRead, [0x01, 0x00, 0x00, 0x00], 4, out byte[]? gpuData) == BiosReturnCode.Ok
            && gpuData is { Length: >= 1 };
        int gpu = gpuOk ? gpuData![0] : 0;

        if (!cpuOk && !gpuOk)
            throw new InvalidOperationException("Temperature read failed on both sensors.");
        return (cpu, gpu);
    }

    public void ApplyProfile(PowerProfile profile)
    {
        ThermalPolicy policy = GetThermalPolicyOrDefault();
        ApplyProfileCore(profile, policy, hasGpuPower: true);
    }

    public void ApplyProfile(PowerProfile profile, BiosCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(caps);
        ApplyProfileCore(profile, caps.EffectivePolicy, caps.GpuPower);
    }

    private void ApplyProfileCore(PowerProfile profile, ThermalPolicy policy, bool hasGpuPower)
    {
        SetFanMode(profile, policy);

        if (hasGpuPower)
        {
            SetGpuPower(profile);
            PowerProfile actual = GetGpuPower().ToProfile();
            if (actual != profile)
                throw new InvalidOperationException(
                    $"Profile {profile} only partially applied (firmware reports {actual}).");
        }

        if (profile == PowerProfile.Eco)
            ResetCpuPowerLimitsToAuto();
    }

    private void ArmFanControl()
    {
        EnsureOkOrUnsupported(BiosCommand.FanCount, [0x00], 4);
    }

    private void EnsureOkOrUnsupported(BiosCommand command, byte[] payload, int responseSize)
    {
        int rc = _client.Execute(command, payload, responseSize, out _);
        if (rc is not (BiosReturnCode.Ok or BiosReturnCode.NotSupported))
            throw new InvalidOperationException($"BIOS command {command} failed: {BiosReturnCode.Describe(rc)}.");
    }
}
