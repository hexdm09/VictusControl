using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

public sealed class CapabilityProbeTests
{
    private static BiosCapabilities ProbeOf(FakeHpWmiClient fake) => CapabilityProbe.Probe(fake);

    [Fact]
    public void Probe_FullySupportedBoard_AllTrue()
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x01 };
        BiosCapabilities caps = ProbeOf(fake);

        Assert.Equal(ThermalPolicy.Modern, caps.Policy);
        Assert.True(caps.GpuPower);
        Assert.True(caps.FanLevels);
        Assert.True(caps.Temperatures);
        Assert.True(caps.MaxFan);
        Assert.True(caps.FanCount);
        Assert.True(caps.GpuModeSwitch);
    }

    [Fact]
    public void Probe_UnknownThermalPolicy_FallsBackToLegacy()
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x07 };
        BiosCapabilities caps = ProbeOf(fake);

        Assert.Null(caps.Policy);
        Assert.Equal(ThermalPolicy.Legacy, caps.EffectivePolicy);
    }

    [Theory]
    [InlineData(BiosCommand.GpuRead, nameof(BiosCapabilities.GpuPower))]
    [InlineData(BiosCommand.VictusFanSpeedGet, nameof(BiosCapabilities.FanLevels))]
    [InlineData(BiosCommand.TempRead, nameof(BiosCapabilities.Temperatures))]
    [InlineData(BiosCommand.MaxFanRead, nameof(BiosCapabilities.MaxFan))]
    [InlineData(BiosCommand.FanCount, nameof(BiosCapabilities.FanCount))]
    public void Probe_SingleUnsupportedCommand_FlagFalse(BiosCommand command, string property)
    {
        var fake = new FakeHpWmiClient();
        fake.Unsupported.Add(command);

        BiosCapabilities caps = ProbeOf(fake);
        bool value = (bool)typeof(BiosCapabilities).GetProperty(property)!.GetValue(caps)!;
        Assert.False(value);
    }

    [Fact]
    public void Probe_GpuModeModuleError_SwitchFlagFalse()
    {
        var fake = new FakeHpWmiClient { FailGpuModeModule = true };
        BiosCapabilities caps = ProbeOf(fake);
        Assert.False(caps.GpuModeSwitch);
    }
}

public sealed class GpuModeTests
{
    [Fact]
    public void GetGpuMode_HybridBoard_Parsed()
    {
        var fake = new FakeHpWmiClient { GpuModeState = GpuMode.Hybrid };
        Assert.Equal(GpuMode.Hybrid, new PowerController(fake).GetGpuMode());
    }

    [Fact]
    public void GetGpuMode_DiscreteBoard_Parsed()
    {
        var fake = new FakeHpWmiClient { GpuModeState = GpuMode.Discrete };
        Assert.Equal(GpuMode.Discrete, new PowerController(fake).GetGpuMode());
    }

    [Fact]
    public void SetGpuMode_UsesGpuModeModuleAndPayload()
    {
        var fake = new FakeHpWmiClient();
        new PowerController(fake).SetGpuMode(GpuMode.Discrete);

        (uint module, BiosCommand command, byte[]? payload, _) = fake.Calls.Single();
        Assert.Equal(BiosModule.GpuMode, module);
        Assert.Equal(BiosCommand.GpuMode, command);
        Assert.Equal([0x01, 0x00, 0x00, 0x00], payload);
    }

    [Fact]
    public void SetGpuMode_FirmwareError_Throws()
    {
        var fake = new FakeHpWmiClient { FailGpuModeModule = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).SetGpuMode(GpuMode.Discrete));
    }

    [Fact]
    public void GpuModeExtensions_ParseNames()
    {
        Assert.Equal(GpuMode.Hybrid, GpuModeExtensions.Parse("hybrid"));
        Assert.Equal(GpuMode.Hybrid, GpuModeExtensions.Parse("AUTO"));
        Assert.Equal(GpuMode.Discrete, GpuModeExtensions.Parse("discrete"));
        Assert.Null(GpuModeExtensions.Parse("internal"));
    }
}

public sealed class CapabilityAwareApplyTests
{
    [Fact]
    public void ApplyProfile_BoardWithoutGpuPower_FanModeOnly()
    {
        var fake = new FakeHpWmiClient();
        fake.Unsupported.Add(BiosCommand.GpuRead);
        fake.Unsupported.Add(BiosCommand.GpuWrite);
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Performance, caps);

        // policy comes from the cached capabilities: no SystemRead, no GPU traffic
        BiosCommand[] commands = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanMode], commands);
        Assert.Equal([0xFF, 0x31, 0x00, 0x00], fake.Calls[0].Payload);
    }

    [Fact]
    public void ApplyProfile_BoardWithGpuPower_FullSequence()
    {
        var fake = new FakeHpWmiClient { GpuState = GpuPowerData.For(PowerProfile.Balanced) };
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Balanced, caps);

        BiosCommand[] commands = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.SystemRead, BiosCommand.FanMode, BiosCommand.GpuWrite, BiosCommand.GpuWrite, BiosCommand.GpuRead], commands);
    }

    [Fact]
    public void GetThermalPolicyOrDefault_UnknownBoard_Legacy()
    {
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x55 };
        Assert.Equal(ThermalPolicy.Legacy, new PowerController(fake).GetThermalPolicyOrDefault());
    }

    [Fact]
    public void GetThermalPolicyOrDefault_TransportFailure_Propagates()
    {
        // a WMI hiccup must NOT silently select the Legacy fan-code table
        // on a Modern board — only an unknown policy byte may fall back
        var fake = new FakeHpWmiClient { ThermalPolicyByte = 0x01, ThrowOnSystemRead = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetThermalPolicyOrDefault());
    }

    [Fact]
    public void GetFanRpm_ShortResponse_Throws()
    {
        var fake = new FakeHpWmiClient { ShortFanData = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetFanRpm());
    }

    [Fact]
    public void GetFanPercent_ShortResponse_Throws()
    {
        var fake = new FakeHpWmiClient { ShortFanData = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetFanPercent());
    }

    [Fact]
    public void GetTemperatures_BothSensorsFailed_Throws()
    {
        var fake = new FakeHpWmiClient { FailTempRead = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetTemperatures());
    }
}
