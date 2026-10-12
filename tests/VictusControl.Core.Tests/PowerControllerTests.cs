using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

// scripted IHpWmiClient: records call order, serves canned reads
internal sealed class FakeHpWmiClient : IHpWmiClient
{
    public readonly List<(BiosCommand Command, byte[]? Payload, int ResponseSize)> Calls = [];
    public byte ThermalPolicyByte = 0x01;
    public GpuPowerData GpuState = GpuPowerData.For(PowerProfile.Performance);
    public bool FailGpuWrites;

    public int Execute(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        Calls.Add((command, payload, responseSize));
        response = command switch
        {
            BiosCommand.SystemRead => SystemData(),
            BiosCommand.GpuRead => GpuState.ToPayload(),
            _ => null,
        };
        if (FailGpuWrites && command == BiosCommand.GpuWrite)
            return 6;
        return BiosReturnCode.Ok;
    }

    public void EnsureSuccess(BiosCommand command, byte[]? payload, int responseSize, out byte[]? response)
    {
        int rc = Execute(command, payload, responseSize, out response);
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
    public void Constructor_NullClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PowerController(null!));
    }
}
