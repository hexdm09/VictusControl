using VictusControl.Core;

namespace VictusControl.App;

// generated in memory: green = Eco, blue = Balanced, red = Performance
internal static class ProfileIcons
{
    public static Icon Eco { get; } = Make(Color.LimeGreen);
    public static Icon Balanced { get; } = Make(Color.DodgerBlue);
    public static Icon Performance { get; } = Make(Color.OrangeRed);
    public static Icon Idle { get; } = Make(Color.Gray);

    // real multi-size app icon (embedded resource, falls back to the exe icon)
    public static Icon AppIcon { get; } = LoadAppIcon();

    // WPF window icon from the same embedded .ico
    public static System.Windows.Media.ImageSource AppIconImageSource { get; } = LoadAppIconImageSource();

    private static System.Windows.Media.ImageSource LoadAppIconImageSource()
    {
        try
        {
            using var stream = typeof(ProfileIcons).Assembly
                .GetManifestResourceStream("VictusControl.App.app.ico");
            if (stream is not null)
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(
                    stream, System.Windows.Media.Imaging.BitmapCreateOptions.None,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                frame.Freeze();
                return frame;
            }
        }
        catch
        {
            // fall through: a window without an icon is acceptable
        }
        return null!; // Window.Icon accepts null (falls back to the default)
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            using var stream = typeof(ProfileIcons).Assembly
                .GetManifestResourceStream("VictusControl.App.app.ico");
            if (stream is not null)
                return new Icon(stream, new Size(32, 32));
        }
        catch
        {
            // fall through to the exe-embedded icon
        }
        try
        {
            return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Idle;
        }
        catch
        {
            return Idle;
        }
    }

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
