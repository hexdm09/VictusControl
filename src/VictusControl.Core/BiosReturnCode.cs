namespace VictusControl.Core;

/// <summary>
/// BIOS return codes (rwReturnCode).
/// </summary>
public static class BiosReturnCode
{
    public const int Ok = 0;
    public const int FirmwareError1 = 1;
    public const int NotSupported = 3;
    public const int FirmwareError4 = 4;
    public const int BadBufferSize = 5;
    public const int FirmwareError6 = 6;
    public const int FirmwareError46 = 46;
    public const int WmiError = -1;

    public static string Describe(int code) => code switch
    {
        Ok => "Success",
        NotSupported => "Command not supported on this platform/BIOS",
        BadBufferSize => "Insufficient buffer size",
        FirmwareError1 or FirmwareError4 or FirmwareError6 or FirmwareError46 =>
            $"HP firmware internal error (code {code})",
        WmiError => "WMI/OS error (no OutData returned)",
        _ => $"Unknown error (code {code})",
    };
}
