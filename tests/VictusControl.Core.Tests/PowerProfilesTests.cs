using VictusControl.Core;
using Xunit;

namespace VictusControl.Core.Tests;

/// <summary>
/// Hardware-free tests for the pure mapping logic.
/// Any change to the firmware code tables must break these first.
/// </summary>
public sealed class PowerProfilesTests
{
    [Theory]
    [InlineData(PowerProfile.Eco, ThermalPolicy.Modern, 0x50)]
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
    [InlineData(0x50, PowerProfile.Eco)]
    [InlineData(0x03, PowerProfile.Eco)]
    [InlineData(0x30, PowerProfile.Balanced)]
    [InlineData(0x00, PowerProfile.Balanced)]
    [InlineData(0x31, PowerProfile.Performance)]
    [InlineData(0x01, PowerProfile.Performance)]
    public void FanModeCodes_Parse_RoundTrips(byte code, PowerProfile expected)
    {
        Assert.Equal(expected, FanModeCodes.Parse(code));
    }

    [Theory]
    [InlineData(0x02)]
    [InlineData(0xFF)]
    public void FanModeCodes_Parse_Unknown_ReturnsNull(byte code)
    {
        Assert.Null(FanModeCodes.Parse(code));
    }

    [Fact]
    public void FanModeCodes_For_UnknownPolicy_Throws()
    {
        Assert.Throws<NotSupportedException>(() => FanModeCodes.For(PowerProfile.Eco, (ThermalPolicy)0xFF));
    }

    [Theory]
    [InlineData(PowerProfile.Eco, 0x00, 0x00)]
    [InlineData(PowerProfile.Balanced, 0x00, 0x01)]
    [InlineData(PowerProfile.Performance, 0x01, 0x01)]
    public void GpuPowerData_For_Presets(PowerProfile profile, byte tgp, byte ppab)
    {
        GpuPowerData data = GpuPowerData.For(profile);
        Assert.Equal(tgp, data.CustomTgp);
        Assert.Equal(ppab, data.Ppab);
        Assert.Equal(0x01, data.DState);
        Assert.Equal(0x00, data.PeakTemperature);
        Assert.Equal(profile, data.ToProfile());
    }

    [Fact]
    public void GpuPowerData_ToPayload_ByteOrder()
    {
        Assert.Equal([0x01, 0x01, 0x01, 0x00], GpuPowerData.For(PowerProfile.Performance).ToPayload());
    }

    [Theory]
    [InlineData(0x01, 0x01, PowerProfile.Performance)]
    [InlineData(0x00, 0x01, PowerProfile.Balanced)]
    [InlineData(0x00, 0x00, PowerProfile.Eco)]
    [InlineData(0x01, 0x00, PowerProfile.Eco)]
    public void GpuPowerData_ToProfile_Maps(byte tgp, byte ppab, PowerProfile expected)
    {
        Assert.Equal(expected, new GpuPowerData(tgp, ppab).ToProfile());
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
        Assert.Contains("4", BiosReturnCode.Describe(4));
        Assert.Contains("WMI", BiosReturnCode.Describe(-1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\a.exe\" /RU SYSTEM")]
    public void ValidateExePath_RejectsBlatant(string path)
    {
        Assert.Throws<ArgumentException>(() => AutostartManager.ValidateExePath(path));
    }

    [Fact]
    public void ValidateExePath_WindowsGates()
    {
        // Rooted/extension gates only make sense on Windows paths.
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Throws<ArgumentException>(() => AutostartManager.ValidateExePath("relative\\tool.exe"));
        Assert.Throws<ArgumentException>(() => AutostartManager.ValidateExePath("C:\\tool.txt"));
        // Reaching the File.Exists gate proves all prior gates passed.
        var ex = Assert.Throws<FileNotFoundException>(
            () => AutostartManager.ValidateExePath("C:\\Tools\\VictusControlTray.exe"));
        Assert.Equal("C:\\Tools\\VictusControlTray.exe", ex.FileName);
    }
}
