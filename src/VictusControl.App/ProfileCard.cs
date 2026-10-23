using VictusControl.Core;

namespace VictusControl.App;

// large clickable card for one power profile
internal sealed class ProfileCard : Control
{
    private const int IconSize = 34;
    private const int IconLeft = 18;
    private const int IconRight = 14;

    private bool _active;
    private bool _hover;
    private Bitmap? _glyph;
    private int _glyphDpi;

    public PowerProfile Profile { get; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Detail { get; set; }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            Invalidate();
        }
    }

    public ProfileCard(PowerProfile profile, string caption, string detail)
    {
        Profile = profile;
        Caption = caption;
        Detail = detail;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserMouse, true);
        Cursor = Cursors.Hand;
        Height = 84;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color accent = UiTheme.AccentOf(Profile);
        Color fill = _active ? UiTheme.SurfaceActive : _hover ? UiTheme.SurfaceHover : UiTheme.Surface;
        Color border = _active ? accent : UiTheme.Border;
        UiTheme.PaintRounded(e, new Rectangle(0, 0, Width - 1, Height - 1), fill, border);

        // accent stripe on the active card
        if (_active)
        using (var stripe = new SolidBrush(accent))
            e.Graphics.FillRectangle(stripe, 0, 10, 3, Height - 20);

        int dpi = DeviceDpi;
        int iconSize = UiTheme.Scaled(IconSize, dpi);
        int textX = UiTheme.Scaled(IconLeft, dpi) + iconSize + UiTheme.Scaled(IconRight, dpi);

        if (_glyph is null || _glyphDpi != dpi)
        {
            _glyph?.Dispose();
            _glyph = RenderGlyph(accent, iconSize);
            _glyphDpi = dpi;
        }
        int centerY = Height / 2;
        e.Graphics.DrawImage(_glyph, UiTheme.Scaled(IconLeft, dpi), centerY - iconSize / 2);

        // vertical placement derived from measured text, not fixed offsets:
        // point-based fonts grow at 125-150% dpi and must not overlap
        Font title = UiTheme.CardTitle(dpi);
        Font body = UiTheme.CardBody(dpi);
        int titleH = TextRenderer.MeasureText(Caption, title).Height;
        int bodyH = TextRenderer.MeasureText(Detail, body).Height;
        int top = centerY - (titleH + bodyH + UiTheme.Scaled(3, dpi)) / 2;
        TextRenderer.DrawText(e.Graphics, Caption, title, new Point(textX, top), UiTheme.TextPrimary);
        TextRenderer.DrawText(e.Graphics, Detail, body, new Point(textX, top + titleH + UiTheme.Scaled(3, dpi)), UiTheme.TextSecondary);
    }

    private Bitmap RenderGlyph(Color accent, int size)
    {
        var bmp = new Bitmap(size, size);
        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var back = new SolidBrush(Color.FromArgb(38, 42, 53));
        g.FillEllipse(back, 0, 0, size - 1, size - 1);
        using var pen = new Pen(accent, (float)Math.Max(1.4, size * 0.055));
        g.DrawEllipse(pen, 0, 0, size - 1, size - 1);

        float mid = size / 2f;
        switch (Profile)
        {
            case PowerProfile.Eco: // leaf
            {
                pen.Width = (float)Math.Max(1.5, size * 0.06);
                g.DrawLine(pen, mid, mid + size * 0.25f, mid, mid - size * 0.19f);
                g.DrawArc(pen, mid, mid - size * 0.25f, size * 0.28f, size * 0.28f, 200, 130);
                break;
            }
            case PowerProfile.Balanced: // equalizer
            {
                pen.Width = (float)Math.Max(1.8, size * 0.07);
                g.DrawLine(pen, mid - size * 0.18f, mid + size * 0.18f, mid - size * 0.18f, mid - size * 0.06f);
                g.DrawLine(pen, mid, mid + size * 0.21f, mid, mid - size * 0.21f);
                g.DrawLine(pen, mid + size * 0.18f, mid + size * 0.12f, mid + size * 0.18f, mid - size * 0.12f);
                break;
            }
            default: // bolt
            {
                var pts = new PointF[]
                {
                    new(mid + size * 0.06f, mid - size * 0.27f), new(mid - size * 0.18f, mid + size * 0.06f),
                    new(mid - size * 0.03f, mid + size * 0.06f), new(mid - size * 0.09f, mid + size * 0.27f),
                    new(mid + size * 0.18f, mid - size * 0.06f), new(mid + size * 0.03f, mid - size * 0.06f),
                };
                using var fill = new SolidBrush(accent);
                g.FillPolygon(fill, pts);
                break;
            }
        }
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _glyph?.Dispose();
        base.Dispose(disposing);
    }
}
