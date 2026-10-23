using System.Drawing.Drawing2D;

namespace VictusControl.App;

// shared dark palette + drawing helpers (OMEN-inspired: near-black + cyan accents).
// fonts are pixel-exact and DPI-scaled: created per (size, style, dpi) and cached
// for the process lifetime, so paints never allocate and per-monitor moves just
// pick another cache entry.
internal static class UiTheme
{
    public static readonly Color Background = Color.FromArgb(16, 17, 22);
    public static readonly Color Surface = Color.FromArgb(24, 26, 33);
    public static readonly Color SurfaceHover = Color.FromArgb(31, 34, 43);
    public static readonly Color SurfaceActive = Color.FromArgb(36, 40, 51);
    public static readonly Color Border = Color.FromArgb(46, 50, 62);
    public static readonly Color BorderActive = Color.FromArgb(76, 201, 240);
    public static readonly Color TextPrimary = Color.FromArgb(235, 238, 245);
    public static readonly Color TextSecondary = Color.FromArgb(148, 156, 170);
    public static readonly Color Accent = Color.FromArgb(76, 201, 240);

    public static readonly Color Eco = Color.FromArgb(94, 200, 113);
    public static readonly Color Balanced = Color.FromArgb(76, 201, 240);
    public static readonly Color Performance = Color.FromArgb(255, 108, 92);

    // design-time sizes are in points at 96 dpi; pixel size = pt * 4/3 * dpi/96
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float Pt, FontStyle Style, int Dpi), Font> Fonts = new();

    public static Font Title(int dpi) => Font(15.5f, FontStyle.Bold, dpi);
    public static Font Subtitle(int dpi) => Font(9.75f, FontStyle.Regular, dpi);
    public static Font CardTitle(int dpi) => Font(12f, FontStyle.Bold, dpi);
    public static Font CardBody(int dpi) => Font(8.75f, FontStyle.Regular, dpi);
    public static Font BigValue(int dpi) => Font(16f, FontStyle.Bold, dpi);
    public static Font SmallLabel(int dpi) => Font(8.25f, FontStyle.Regular, dpi);

    public static Font Font(float pointSize, FontStyle style, int dpi) =>
        Fonts.GetOrAdd((pointSize, style, dpi), static key =>
            new Font("Segoe UI", (float)Math.Round(key.Pt * 4f / 3f * key.Dpi / 96f, 1), GraphicsUnit.Pixel));

    public static int Scaled(int designPixels, int dpi) =>
        (int)Math.Round(designPixels * (dpi / 96.0));

    public static Color AccentOf(Core.PowerProfile profile) => profile switch
    {
        Core.PowerProfile.Eco => Eco,
        Core.PowerProfile.Performance => Performance,
        _ => Balanced,
    };

    public static void PaintRounded(PaintEventArgs e, Rectangle bounds, Color fill, Color border, int radius = 10)
    {
        using var path = RoundedPath(bounds, radius);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(fill))
            e.Graphics.FillPath(brush, path);
        if (border != Color.Transparent)
        {
            using var pen = new Pen(border);
            e.Graphics.DrawPath(pen, path);
        }
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(Rectangle.Inflate(bounds, 0, 0));
            return path;
        }
        int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
