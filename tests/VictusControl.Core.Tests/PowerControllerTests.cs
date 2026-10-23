using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

// scripted IHpWmiClient: records call order, serves canned reads
internal sealed class FakeHpWmiClient : IHpWmiClient
{
    public readonly List<(uint Module, BiosCommand Command, byte[]? Payload, int ResponseSize)> Calls = [];
    public byte ThermalPolicyByte = 0x01;
    public GpuPowerData GpuState = GpuPowerData.For(PowerProfile.Performance);
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
        response = (module, command) switch
        {
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
        // unsupported boards answer firmware error 4 on every 0x52 call (read and write)
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
}

public sealed class PowerControllerTests
{
    [Fact]
    public void ApplyProfile_CallOrder_FanMode_Gpu_Gpu_Verify()
    {
        var fake = new FakeHpWmiClient { GpuState = GpuPowerData.For(PowerProfile.Eco) };
        new PowerController(fake).ApplyProfile(PowerProfile.Eco);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([
            BiosCommand.SystemRead,
            BiosCommand.FanMode,
            BiosCommand.GpuWrite,
            BiosCommand.GpuWrite,
            BiosCommand.GpuRead,
        ], order);
    }

    [Fact]
    public void ApplyProfile_FanModePayload_HasPrefixAndCode()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).ApplyProfile(PowerProfile.Performance);

        var fanMode = fake.Calls.First(c => c.Command == BiosCommand.FanMode);
        Assert.Equal([0xFF, 0x31, 0x00, 0x00], fanMode.Payload);
    }

    [Fact]
    public void ApplyProfile_VerifyMismatch_Throws()
    {
        // firmware stuck on Eco while Performance was requested
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
    public void GetThermalPolicy_Known(byte raw, ThermalPolicy expected)
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = raw };
        Assert.Equal(expected, new PowerController(fake).GetThermalPolicy());
    }

    [Fact]
    public void GetThermalPolicy_Unknown_Throws()
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x02 };
        Assert.Throws<NotSupportedException>(() => new PowerController(fake).GetThermalPolicy());
    }

    [Fact]
    public void SetMaxFan_True_CallsFanCountTriggerThenMaxFanWrite()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetMaxFan(true);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanCount, BiosCommand.MaxFanWrite], order);
        Assert.Equal([0x01, 0x00, 0x00, 0x00], fake.Calls[1].Payload);
    }

    [Fact]
    public void SetMaxFan_False_CallsFanCountTriggerThenMaxFanOffAndVictusReset()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetMaxFan(false);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanCount, BiosCommand.MaxFanWrite, BiosCommand.VictusFanSpeedSet], order);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], fake.Calls[1].Payload);
        Assert.Equal([0x00, 0x00], fake.Calls[2].Payload);
    }

    [Fact]
    public void SetFanPercent_CallsFanCountTriggerThenVictusFanSpeedSet()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetFanPercent(45, 45);

        BiosCommand[] order = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanCount, BiosCommand.VictusFanSpeedSet], order);
        Assert.Equal([45, 45, 0x00, 0x00], fake.Calls[1].Payload);
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
    public void GetFanPercent_ReturnsRawLevels()
    {
        var fake = new FakeHpWmiClient();
        var (p1, p2) = new PowerController(fake).GetFanPercent();
        Assert.Equal(58, p1);
        Assert.Equal(61, p2);
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
    public void GetTemperatures_ReturnsCorrectTemps()
    {
        var fake = new FakeHpWmiClient();
        var (cpu, gpu) = new PowerController(fake).GetTemperatures();
        Assert.Equal(38, cpu);
        Assert.Equal(50, gpu);
    }

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerController(null!));
    }
}
