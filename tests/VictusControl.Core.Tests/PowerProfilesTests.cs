using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

public sealed class PowerProfilesTests
{
    [Theory]
    [InlineData(PowerProfile.Eco, ThermalPolicy.VictusS, 0x00)]
    [InlineData(PowerProfile.Balanced, ThermalPolicy.VictusS, 0x00)]
    [InlineData(PowerProfile.Performance, ThermalPolicy.VictusS, 0x01)]
    [InlineData(PowerProfile.Eco, ThermalPolicy.Modern, 0x30)]
    [InlineData(PowerProfile.Balanced, ThermalPolicy.Modern, 0x30)]
    [InlineData(PowerProfile.Performance, ThermalPolicy.Modern, 0x31)]
    [InlineData(PowerProfile.Eco, ThermalPolicy.Legacy, 0x03)]
    [InlineData(PowerProfile.Balanced, ThermalPolicy.Legacy, 0x00)]
    [InlineData(PowerProfile.Performance, ThermalPolicy.Legacy, 0x01)]
    public void FanModeCodes_For_ReturnsExpected(PowerProfile profile, ThermalPolicy policy, byte expected)
    {
        Assert.Equal(expected, FanModeCodes.For(profile, policy));
    }

    [Theory]
    [InlineData(1, 17, 19)]
    [InlineData(50, 37, 39)]
    [InlineData(100, 58, 60)]
    public void VictusFanTable_PercentToKrpm_ScalesAndAppliesGpuDelta(int pct, byte expectedCpu, byte expectedGpu)
    {
        var table = new VictusFanTable(17, 58, 2);
        var (cpu, gpu) = table.PercentToKrpm(pct, pct);
        Assert.Equal(expectedCpu, cpu);
        Assert.Equal(expectedGpu, gpu);
    }

    [Fact]
    public void FanCurve_Evaluate_AppliesDownwardHysteresis()
    {
        FanCurve curve = FanCurve.BalancedDefault;
        int highPct = curve.Evaluate(75);
        // Drop by 1°C (< 3°C hysteresis): must hold highPct instead of hunting
        int heldPct = curve.Evaluate(74, lastAnchorTempC: 75, lastPercent: highPct);
        Assert.Equal(highPct, heldPct);

        // Drop by 5°C (>= 3°C hysteresis): steps down smoothly
        int lowerPct = curve.Evaluate(70, lastAnchorTempC: 75, lastPercent: highPct);
        Assert.True(lowerPct < highPct);
    }

    [Theory]
    [InlineData(PowerProfile.Eco, 0x00, 0x00)]
    [InlineData(PowerProfile.Balanced, 0x00, 0x01)]
    [InlineData(PowerProfile.Performance, 0x01, 0x01)]
    public void GpuPowerData_For_PresetsPreserveDefaultPeakTemp(PowerProfile profile, byte tgp, byte ppab)
    {
        GpuPowerData data = GpuPowerData.For(profile);
        Assert.Equal(tgp, data.CustomTgp);
        Assert.Equal(ppab, data.Ppab);
        Assert.Equal(0x01, data.DState);
        Assert.Equal(GpuPowerData.DefaultPeakTemp, data.PeakTemperature);
        Assert.Equal(profile, data.ToProfile());
    }

    [Fact]
    public void CpuPowerData_FirmwareAutoReset_MatchesHpWmiPayload()
    {
        Assert.Equal([0x00, 0x00, 0xFF, 0xFF], CpuPowerData.FirmwareAutoReset.ToPayload());
    }

    [Theory]
    [InlineData(PowerProfile.Eco, PowerProfile.Balanced)]
    [InlineData(PowerProfile.Balanced, PowerProfile.Performance)]
    [InlineData(PowerProfile.Performance, PowerProfile.Eco)]
    public void PowerProfile_Next_Rotates(PowerProfile current, PowerProfile expected)
    {
        Assert.Equal(expected, current.Next());
    }

    [Fact]
    public void BiosReturnCode_Describe_KnownCodes()
    {
        Assert.Equal("Success", BiosReturnCode.Describe(0));
        Assert.Contains("not supported", BiosReturnCode.Describe(3), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("buffer", BiosReturnCode.Describe(5), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a.exe\" /RU SYSTEM")]
    public void ValidateExePath_RejectsBlatant(string path)
    {
        Assert.Throws<ArgumentException>(() => AutostartManager.ValidateExePath(path));
    }
}
