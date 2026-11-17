using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

internal sealed class FakeHpWmiClient : IHpWmiClient
{
    public readonly List<(uint Module, BiosCommand Command, byte[]? Payload, int ResponseSize)> Calls = [];
    public byte ThermalPolicyByte = 0x01;
    public bool HasVictusSFanTable;
    public GpuPowerData GpuState = GpuPowerData.For(PowerProfile.Performance, 0x57);
    public bool FailGpuWrites;
    public bool FailTempRead;
    public bool ShortFanData;
    public bool ThrowOnSystemRead;
    public HashSet<BiosCommand> Unsupported = [];
    public GpuMode GpuModeState = GpuMode.Hybrid;
    public bool FailGpuModeModule;

    public int Execute(uint module, BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        if (ThrowOnSystemRead && command == BiosCommand.SystemRead)
            throw new InvalidOperationException("simulated WMI transport failure");
        Calls.Add((module, command, payload, responseSize));
        if (Unsupported.Contains(command))
        {
            response = null;
            return BiosReturnCode.NotSupported;
        }
        if (command == BiosCommand.VictusFanTableGet && !HasVictusSFanTable)
        {
            response = null;
            return BiosReturnCode.NotSupported;
        }
        response = (module, command) switch
        {
            (_, BiosCommand.VictusFanTableGet) => BuildSampleFanTable(),
            (_, BiosCommand.SystemRead) => SystemData(),
            (_, BiosCommand.GpuRead) => GpuState.ToPayload(),
            (_, BiosCommand.VictusFanSpeedGet) => ShortFanData ? [58] : [58, 61],
            (_, BiosCommand.TempRead) => FailTempRead
                ? null
                : payload is { Length: > 0 } && payload[0] == 0x01 ? [50, 0, 0, 0] : [38, 0, 0, 0],
            (_, BiosCommand.FanCount) => [2, 0, 0, 0],
            (_, BiosCommand.MaxFanRead) => [0, 0, 0, 0],
            (BiosModule.Legacy, BiosCommand.GpuMode) => [(byte)GpuModeState, 0, 0, 0],
            _ => null,
        };
        if (FailTempRead && command == BiosCommand.TempRead)
            return BiosReturnCode.FirmwareError6;
        if (FailGpuWrites && command == BiosCommand.GpuWrite)
            return 6;
        if (FailGpuModeModule && command == BiosCommand.GpuMode)
            return 4;
        return BiosReturnCode.Ok;
    }

    public void EnsureSuccess(uint module, BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        int rc = Execute(module, command, payload, responseSize, out response);
        if (rc != BiosReturnCode.Ok)
            throw new InvalidOperationException($"BIOS command {command} failed: {BiosReturnCode.Describe(rc)}.");
    }

    public void Dispose() { }

    private byte[] SystemData()
    {
        var data = new byte[128];
        data[3] = ThermalPolicyByte;
        return data;
    }

    private static byte[] BuildSampleFanTable()
    {
        var table = new byte[128];
        table[0] = 0x01; // header valid
        table[1] = 0x02; // 2 fans
        // entry 0: CPU 17 (1700 RPM), GPU 19 (1900 RPM -> delta +2)
        table[2] = 17; table[3] = 19; table[4] = 1;
        // entry 1: CPU 36, GPU 38
        table[5] = 36; table[6] = 38; table[7] = 2;
        // entry 2: CPU 58 (5800 RPM max), GPU 60
        table[8] = 58; table[9] = 60; table[10] = 3;
        return table;
    }
}

public sealed class PowerControllerTests
{
    [Fact]
    public void ApplyProfile_VictusSBoard_Eco_Uses2ByteFanModePreservesPeakTempAndResetsCpuLimits()
    {
        var fake = new FakeHpWmiClient
        {
            HasVictusSFanTable = true,
            GpuState = GpuPowerData.For(PowerProfile.Eco, 0x57),
        };
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Eco, caps);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([
            BiosCommand.FanCount,
            BiosCommand.FanMode,
            BiosCommand.GpuRead,
            BiosCommand.GpuWrite,
            BiosCommand.GpuWrite,
            BiosCommand.GpuRead,
            BiosCommand.CpuWrite,
        ], order);

        Assert.Equal([0xFF, 0x00], fake.Calls[1].Payload);
        Assert.Equal([0x00, 0x00, 0x01, 0x57], fake.Calls[3].Payload);
        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], fake.Calls[6].Payload);
    }

    [Fact]
    public void ApplyProfile_ModernNonVictusS_Performance_Sends4ByteFanMode()
    {
        var fake = new FakeHpWmiClient();
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Performance, caps);

        var fanMode = fake.Calls.First(c => c.Command == BiosCommand.FanMode);
        Assert.Equal([0xFF, 0x31, 0x00, 0x00], fanMode.Payload);
    }

    [Fact]
    public void ApplyProfile_VerifyMismatch_Throws()
    {
        var fake = new FakeHpWmiClient { GpuState = GpuPowerData.For(PowerProfile.Eco) };
        var ex = Assert.Throws<InvalidOperationException>(
            () => new PowerController(fake).ApplyProfile(PowerProfile.Performance));
        Assert.Contains("partially applied", ex.Message);
    }

    [Fact]
    public void ApplyProfile_FailedGpuWrite_Throws()
    {
        var fake = new FakeHpWmiClient { FailGpuWrites = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).ApplyProfile(PowerProfile.Balanced));
    }

    [Theory]
    [InlineData(0x01, ThermalPolicy.Modern)]
    [InlineData(0x00, ThermalPolicy.Legacy)]
    public void GetThermalPolicy_NonVictusS_Known(byte raw, ThermalPolicy expected)
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = raw };
        Assert.Equal(expected, new PowerController(fake).GetThermalPolicy());
    }

    [Fact]
    public void GetThermalPolicy_VictusSWith0x2F_ReturnsVictusS()
    {
        var fake = new FakeHpWmiClient { HasVictusSFanTable = true };
        Assert.Equal(ThermalPolicy.VictusS, new PowerController(fake).GetThermalPolicy());
    }

    [Fact]
    public void GetThermalPolicy_Unknown_Throws()
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x07 };
        Assert.Throws<NotSupportedException>(() => new PowerController(fake).GetThermalPolicy());
    }

    [Fact]
    public void SetMaxFan_True_CallsFanCountThenMaxFanWrite()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetMaxFan(true);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanCount, BiosCommand.MaxFanWrite], order);
        Assert.Equal([0x01, 0x00, 0x00, 0x00], fake.Calls[1].Payload);
    }

    [Fact]
    public void SetMaxFan_False_FollowsExactKernelAutoResetSequence()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetMaxFan(false);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([
            BiosCommand.FanCount,
            BiosCommand.MaxFanWrite,
            BiosCommand.FanCount,
            BiosCommand.VictusFanSpeedSet,
        ], order);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], fake.Calls[1].Payload);
        Assert.Equal([0x00, 0x00], fake.Calls[3].Payload);
    }

    [Fact]
    public void SetFanPercent_ScalesToKrpmClearsMaxFanAndSends2BytePayloadWithGpuDelta()
    {
        var fake = new FakeHpWmiClient { HasVictusSFanTable = true };
        var controller = new PowerController(fake);
        VictusFanTable table = controller.GetFanTableOrDefault();
        fake.Calls.Clear();

        controller.SetFanPercent(100, 100, table);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanCount, BiosCommand.MaxFanWrite, BiosCommand.VictusFanSpeedSet], order);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], fake.Calls[1].Payload);
        // 100% -> CPU 58 (5800 RPM), GPU 58 + delta(2) = 60 (6000 RPM), 2-byte payload
        Assert.Equal([58, 60], fake.Calls[2].Payload);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(101, 50)]
    [InlineData(50, -1)]
    [InlineData(50, 101)]
    public void SetFanPercent_OutOfRange_Throws(int fan1, int fan2)
    {
        var fake = new FakeHpWmiClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PowerController(fake).SetFanPercent(fan1, fan2));
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void GetFanRpm_ReturnsCorrectRpm()
    {
        var fake = new FakeHpWmiClient();
        var (f1, f2) = new PowerController(fake).GetFanRpm();
        Assert.Equal(5800, f1);
        Assert.Equal(6100, f2);
    }

    [Fact]
    public void GetTemperatures_SkipGpu_DoesNotTouchGpuSensor()
    {
        var fake = new FakeHpWmiClient();
        var (cpu, gpu) = new PowerController(fake).GetTemperatures(includeGpu: false);
        Assert.Equal(38, cpu);
        Assert.Equal(0, gpu);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerController(null!));
    }
}
