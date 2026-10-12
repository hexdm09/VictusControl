using VictusControl.Core;

namespace VictusControl.App;

// generated in memory: green = Eco, blue = Balanced, red = Performance
internal static class ProfileIcons
{
    public static Icon Eco { get; } = Make(Color.LimeGreen);
    public static Icon Balanced { get; } = Make(Color.DodgerBlue);
    public static Icon Performance { get; } = Make(Color.OrangeRed);
    public static Icon Idle { get; } = Make(Color.Gray);

    public static Icon IconFor(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => Eco,
        PowerProfile.Performance => Performance,
        _ => Balanced,
    };

    public static Color AccentFor(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => Color.LimeGreen,
        PowerProfile.Performance => Color.OrangeRed,
        _ => Color.DodgerBlue,
    };

    private static Icon Make(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
        }
        // FromHandle does not own the native handle: clone into managed memory, then free it
        nint native = bitmap.GetHicon();
        try
        {
            using var owned = Icon.FromHandle(native);
            return (Icon)owned.Clone();
        }
        finally
        {
            DestroyIcon(native);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);
}
