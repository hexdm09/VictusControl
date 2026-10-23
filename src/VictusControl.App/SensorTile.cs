namespace VictusControl.App;

// live value tile: big number + caption + colored bar
internal sealed class SensorTile : Control
{
    private string _value = "--";
    private float _level; // 0..1 for the intensity bar
    private Color _bar = UiTheme.Balanced;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Unit { get; set; } = "";

    public void Update(string value, float level, Color bar)
    {
        _value = value;
        _level = Math.Clamp(level, 0f, 1f);
        _bar = bar;
        Invalidate();
    }

    public SensorTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        UiTheme.PaintRounded(e, new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.Surface, UiTheme.Border);

        int dpi = DeviceDpi;
        int pad = UiTheme.Scaled(14, dpi);
        Font label = UiTheme.SmallLabel(dpi);
        Font valueFont = UiTheme.BigValue(dpi);
        int captionH = TextRenderer.MeasureText(Caption, label).Height;

        TextRenderer.DrawText(e.Graphics, Caption, label, new Point(pad, pad), UiTheme.TextSecondary);
        TextRenderer.DrawText(e.Graphics, $"{_value}{Unit}", valueFont, new Point(pad, pad + captionH + UiTheme.Scaled(2, dpi)), UiTheme.TextPrimary);

        // intensity bar
        int barHeight = UiTheme.Scaled(4, dpi);
        var track = new Rectangle(pad, Height - barHeight - UiTheme.Scaled(10, dpi), Width - 2 * pad, barHeight);
        using (var trackBrush = new SolidBrush(UiTheme.Border))
            e.Graphics.FillRectangle(trackBrush, track);
        if (_level > 0)
        using (var barBrush = new SolidBrush(_bar))
            e.Graphics.FillRectangle(barBrush, new Rectangle(track.X, track.Y, (int)(track.Width * _level), track.Height));
    }
}
