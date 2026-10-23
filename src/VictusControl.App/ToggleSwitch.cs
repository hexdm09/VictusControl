namespace VictusControl.App;

// owner-drawn switch (WinForms has no native toggle)
internal sealed class ToggleSwitch : Control
{
    private bool _checked;
    private bool _hover;

    public event EventHandler? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserMouse, true);
        Cursor = Cursors.Hand;
        Size = new Size(44, 24);
        TabStop = false;
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

    protected override void OnClick(EventArgs e)
    {
        // toggle on the primary button only: right/middle clicks are not switches
        if (MouseButtons == MouseButtons.Left)
            Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Color track = _checked ? UiTheme.Accent : UiTheme.Border;
        if (_hover)
            track = ControlPaint.Light(track, 0.1f);
        UiTheme.PaintRounded(e, new Rectangle(0, 0, Width - 1, Height - 1),
            _checked ? Color.FromArgb(26, 58, 74) : UiTheme.Surface, track, Height / 2);

        int knob = Height - 8;
        int x = _checked ? Width - knob - 5 : 4;
        using var knobBrush = new SolidBrush(_checked ? UiTheme.Accent : Color.FromArgb(170, 178, 190));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.FillEllipse(knobBrush, x, 4, knob, knob);
    }
}
