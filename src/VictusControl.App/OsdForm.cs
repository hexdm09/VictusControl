namespace VictusControl.App;

/// <summary>
/// Borderless top-center toast shown for ~1.2s on profile switch.
/// Never steals focus (NOACTIVATE).
/// </summary>
internal sealed class OsdForm : Form
{
    private readonly System.Windows.Forms.Timer _closeTimer;

    public OsdForm(string text, Color accent)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.FromArgb(28, 28, 30);
        Padding = new Padding(26, 14, 26, 14);

        var bar = new Panel
        {
            BackColor = accent,
            Width = 4,
            Dock = DockStyle.Left,
        };

        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 14, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(label);
        Controls.Add(bar);

        Load += (_, _) =>
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(area.X + ((area.Width - Width) / 2), area.Y + 90);
        };

        _closeTimer = new System.Windows.Forms.Timer { Interval = 1200 };
        _closeTimer.Tick += (_, _) => Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TOOLWINDOW = 0x00000080;
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Opacity = 0.95;
        _closeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _closeTimer.Dispose();
        base.Dispose(disposing);
    }

    public static void ShowToast(string text, Color accent)
    {
        using var form = new OsdForm(text, accent);
        form.ShowDialog();
    }
}
