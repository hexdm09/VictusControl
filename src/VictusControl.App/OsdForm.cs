using VictusControl.Core;

namespace VictusControl.App;

// top-center non-activating overlay toast shown on profile switch
internal sealed class OsdForm : Form
{
    private const int DisplayMs = 1200;

    private static OsdForm? _active;

    private readonly System.Windows.Forms.Timer _closeTimer;

    private OsdForm(string text, Color accent)
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
            Font = UiTheme.Font(14, FontStyle.Bold, DeviceDpi),
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        Controls.Add(label);
        Controls.Add(bar);

        Load += (_, _) =>
        {
            // show on the screen the user is looking at, not blindly on the primary
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(area.X + ((area.Width - Width) / 2), area.Y + UiTheme.Scaled(90, DeviceDpi));
        };

        _closeTimer = new System.Windows.Forms.Timer { Interval = DisplayMs };
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

    public static void ShowToast(string text, PowerProfile profile)
    {
        OsdForm form = new(text, ProfileIcons.AccentFor(profile));
        // guard by reference: the previous toast closes asynchronously and must
        // not clear the pointer to this newer one when its FormClosed fires
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_active, form))
                _active = null;
        };
        _active?.Close();
        _active = form;
        form.Show();
    }
}
