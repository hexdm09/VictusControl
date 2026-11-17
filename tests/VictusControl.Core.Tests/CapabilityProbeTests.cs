using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

public sealed class CapabilityProbeTests
{
    private static BiosCapabilities ProbeOf(FakeHpWmiClient fake) => CapabilityProbe.Probe(fake);

    [Fact]
    public void Probe_FullySupportedModernBoard_AllTrue()
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
        Assert.Null(caps.FanTable);
    }

    [Fact]
    public void Probe_VictusSBoardWith0x2F_DetectsVictusSPolicyAndFanTable()
    {
        var fake = new FakeHpWmiClient { HasVictusSFanTable = true };
        BiosCapabilities caps = ProbeOf(fake);

        Assert.Equal(ThermalPolicy.VictusS, caps.Policy);
        Assert.NotNull(caps.FanTable);
        Assert.Equal((byte)17, caps.FanTable!.MinKrpm);
        Assert.Equal((byte)58, caps.FanTable.MaxKrpm);
        Assert.Equal(2, caps.FanTable.GpuDelta);
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
    public void ApplyProfile_BoardWithoutGpuPower_UsesCachedPolicyWithoutSystemRead()
    {
        var fake = new FakeHpWmiClient();
        fake.Unsupported.Add(BiosCommand.GpuRead);
        fake.Unsupported.Add(BiosCommand.GpuWrite);
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Performance, caps);

        BiosCommand[] commands = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([BiosCommand.FanMode], commands);
        Assert.Equal([0xFF, 0x31, 0x00, 0x00], fake.Calls[0].Payload);
    }

    [Fact]
    public void ApplyProfile_BoardWithGpuPower_UsesCachedPolicyWithoutRedundantSystemRead()
    {
        var fake = new FakeHpWmiClient { GpuState = GpuPowerData.For(PowerProfile.Balanced, 0x57) };
        BiosCapabilities caps = CapabilityProbe.Probe(fake);
        fake.Calls.Clear();

        new PowerController(fake).ApplyProfile(PowerProfile.Balanced, caps);

        BiosCommand[] commands = [.. fake.Calls.Select(c => c.Command)];
        Assert.Equal([
            BiosCommand.FanMode,
            BiosCommand.GpuRead,
            BiosCommand.GpuWrite,
            BiosCommand.GpuWrite,
            BiosCommand.GpuRead,
        ], commands);
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
    public void GetFanKrpm_ShortResponse_Throws()
    {
        var fake = new FakeHpWmiClient { ShortFanData = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetFanKrpm());
    }

    [Fact]
    public void GetTemperatures_BothSensorsFailed_Throws()
    {
        var fake = new FakeHpWmiClient { FailTempRead = true };
        Assert.Throws<InvalidOperationException>(() => new PowerController(fake).GetTemperatures());
    }
}
