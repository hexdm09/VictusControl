using System.Runtime.InteropServices;

namespace VictusControl.App;

// Switches primary panel refresh rate between 60 Hz (Eco battery saving) and max Hz (144/165 Hz).
internal static class DisplayRefreshManager
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int DM_PELSWIDTH = 0x00080000;
    private const int DM_PELSHEIGHT = 0x00100000;
    private const int DM_BITSPERPEL = 0x00040000;
    private const int DM_DISPLAYFREQUENCY = 0x00400000;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    public static void ApplyForEcoState(bool prefer60Hz)
    {
        try
        {
            DEVMODE current = CreateDevMode();
            if (!EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref current))
                return;

            int width = current.dmPelsWidth;
            int height = current.dmPelsHeight;
            int bpp = current.dmBitsPerPel;
            int maxHz = current.dmDisplayFrequency;
            bool has60Hz = false;

            for (int i = 0; ; i++)
            {
                DEVMODE candidate = CreateDevMode();
                if (!EnumDisplaySettingsW(null, i, ref candidate))
                    break;
                if (candidate.dmPelsWidth == width && candidate.dmPelsHeight == height && candidate.dmBitsPerPel == bpp)
                {
                    if (candidate.dmDisplayFrequency == 60)
                        has60Hz = true;
                    if (candidate.dmDisplayFrequency > maxHz)
                        maxHz = candidate.dmDisplayFrequency;
                }
            }

            int targetHz = prefer60Hz && has60Hz ? 60 : maxHz;
            if (targetHz <= 0 || targetHz == current.dmDisplayFrequency)
                return;

            current.dmDisplayFrequency = targetHz;
            current.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_BITSPERPEL | DM_DISPLAYFREQUENCY;
            _ = ChangeDisplaySettingsExW(null, ref current, IntPtr.Zero, 0, IntPtr.Zero) == DISP_CHANGE_SUCCESSFUL;
        }
        catch
        {
            // RDP / headless / locked driver: ignore cleanly
        }
    }

    private static DEVMODE CreateDevMode() => new()
    {
        dmDeviceName = new string('\0', 32),
        dmFormName = new string('\0', 32),
        dmSize = (short)Marshal.SizeOf<DEVMODE>(),
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(
        string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}
