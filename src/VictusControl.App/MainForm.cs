using System.Runtime.InteropServices;
using VictusControl.Core;

namespace VictusControl.App;

// main application window: profiles, sensors, fan control, settings.
// hardware work is delegated to TrayContext (shared with the tray menu).
// layout is manual (AutoScaleMode.None): every constant scales from DeviceDpi,
// vertical text placement derives from measured font heights.
internal sealed class MainForm : Form
{
    private const int WM_DPICHANGED = 0x02E0;

    private readonly TrayContext _app;
    private readonly Action<string> _onError;

    private readonly ProfileCard _ecoCard;
    private readonly ProfileCard _balancedCard;
    private readonly ProfileCard _perfCard;
    private readonly SensorTile _cpuTile = new() { Caption = "CPU TEMPERATURE", Unit = "°C" };
    private readonly SensorTile _gpuTile = new() { Caption = "GPU TEMPERATURE", Unit = "°C" };
    private readonly SensorTile _fan1Tile = new() { Caption = "FAN 1", Unit = " RPM" };
    private readonly SensorTile _fan2Tile = new() { Caption = "FAN 2", Unit = " RPM" };
    private readonly Label _statusLabel = new();
    private readonly Label _profileChip = new();

    private readonly ToggleSwitch _osdSwitch = new();
    private readonly ToggleSwitch _keepAliveSwitch = new();
    private readonly ToggleSwitch _autostartSwitch = new();
    private readonly ToggleSwitch _startMinimizedSwitch = new();
    private readonly ToggleSwitch _closeToTraySwitch = new();

    private readonly Button _fanAutoButton = new();
    private readonly Button _fanMaxButton = new();
    private readonly Button _fanManualButton = new();
    private readonly TrackBar _fanSlider = new();
    private readonly Label _fanSliderValue = new();

    private readonly Button _hybridButton = new();
    private readonly Button _discreteButton = new();
    private readonly Label _gpuModeNote = new();
    private Control _graphicsSection = null!;
    private Control _graphicsPanel = null!;
    private Control _vitalsSection = null!;
    private Control _fanSection = null!;

    private readonly System.Windows.Forms.Timer _fanSliderDebounce;
    private bool _suppressSliderEvent;
    private bool _suppressSettingsEvents;

    // scrollable content root; children are positioned manually in LayoutContent
    private readonly Panel _root = new() { AutoScroll = true, BackColor = UiTheme.Background };
    private readonly List<(Control Control, int DesignHeight, int Gap)> _flowItems = [];
    private Panel _tilesRow = null!;
    private readonly SensorTile[] _tiles = null!;

    public MainForm(TrayContext app)
    {
        _app = app;
        _onError = msg => _statusLabel.Text = msg;
        Text = "VictusControl";
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(Scaled(620), Scaled(560));
        BackColor = UiTheme.Background;
        ForeColor = UiTheme.TextPrimary;
        Font = UiTheme.Subtitle(DeviceDpi);
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(Scaled(720), Scaled(820));
        Icon = ProfileIcons.AppIcon;

        RestoreWindowPosition();

        _ecoCard = new ProfileCard(PowerProfile.Eco, "ECO", "Quiet · cTGP off · Dynamic Boost off");
        _balancedCard = new ProfileCard(PowerProfile.Balanced, "BALANCED", "Standard · Dynamic Boost on");
        _perfCard = new ProfileCard(PowerProfile.Performance, "PERFORMANCE", "Max airflow · full cTGP + Dynamic Boost");
        _ecoCard.Click += (_, _) => _app.ApplyProfile(PowerProfile.Eco);
        _balancedCard.Click += (_, _) => _app.ApplyProfile(PowerProfile.Balanced);
        _perfCard.Click += (_, _) => _app.ApplyProfile(PowerProfile.Performance);
        _tiles = [_cpuTile, _gpuTile, _fan1Tile, _fan2Tile];

        BuildLayout();
        BindEvents();

        _fanSliderDebounce = new System.Windows.Forms.Timer { Interval = 350 };
        _fanSliderDebounce.Tick += (_, _) =>
        {
            _fanSliderDebounce.Stop();
            if (IsDisposed)
                return; // a late tick after close must not touch disposed controls
            _app.SetFanMode(FanMode.Manual, _fanSlider.Value);
        };

        _app.StateChanged += RefreshFromApp;
        _app.ErrorRaised += _onError;
        RefreshFromApp();
    }

    private int Scaled(int designPixels) => UiTheme.Scaled(designPixels, DeviceDpi);

    // clamp the saved position so most of the window lands inside a visible
    // working area even after the monitor layout changed since the last run
    private void RestoreWindowPosition()
    {
        if (_app.Settings.WindowLeft == int.MinValue)
        {
            StartPosition = FormStartPosition.CenterScreen;
            return;
        }

        Point saved = new(_app.Settings.WindowLeft, _app.Settings.WindowTop);
        Rectangle workArea = Screen.FromPoint(saved).WorkingArea;
        int visible = Scaled(140);
        int x = Math.Min(Math.Max(saved.X, workArea.Left - Size.Width + visible), workArea.Right - visible);
        int y = Math.Min(Math.Max(saved.Y, workArea.Top), workArea.Bottom - visible);
        Location = new Point(x, y);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _app.ResumeSensors();
        LayoutContent();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _fanSliderDebounce.Stop();
        _fanSliderDebounce.Dispose();
        _app.Settings.WindowLeft = Location.X;
        _app.Settings.WindowTop = Location.Y;
        _app.Settings.Save();
        _app.SuspendSensors();
        _app.StateChanged -= RefreshFromApp;
        _app.ErrorRaised -= _onError;
        base.OnFormClosed(e);
    }

    // close button hides to tray unless the user disabled that behavior
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && _app.Settings.CloseToTray && !_app.IsExiting)
        {
            base.OnFormClosing(e);
            if (e.Cancel)
                return;
            e.Cancel = true;
            Hide();
            _app.SuspendSensors();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            _app.ResumeSensors();
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        LayoutContent();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DPICHANGED)
        {
            // fonts and metrics derive from DeviceDpi on demand: reflow and repaint
            BeginInvoke(() =>
            {
                LayoutContent();
                PositionFanPanelChildren();
                PositionSettingsToggles(_settingsPanel);
                RefreshFromApp();
            });
        }
        base.WndProc(ref m);
    }

    private void BuildLayout()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = Scaled(58), BackColor = UiTheme.Background };
        var title = new Label
        {
            Text = "VICTUS CONTROL",
            Font = UiTheme.Title(DeviceDpi),
            ForeColor = UiTheme.TextPrimary,
            AutoSize = true,
            Location = new Point(Scaled(20), Scaled(14)),
        };
        _profileChip.TextAlign = ContentAlignment.MiddleRight;
        _profileChip.Font = UiTheme.CardTitle(DeviceDpi);
        header.Resize += (_, _) => _profileChip.Bounds = new Rectangle(header.Width - Scaled(190), Scaled(12), Scaled(170), Scaled(30));
        header.Controls.Add(title);
        header.Controls.Add(_profileChip);

        _statusLabel.Dock = DockStyle.Top;
        _statusLabel.Height = Scaled(30);
        _statusLabel.ForeColor = UiTheme.TextSecondary;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.BackColor = UiTheme.Background;
        _statusLabel.Padding = new Padding(Scaled(20), 0, 0, 0);
        _root.Dock = DockStyle.Fill;

        // Flow items are appended top to bottom; LayoutContent positions them.
        _tilesRow = new Panel { BackColor = UiTheme.Background };
        _tilesRow.Controls.AddRange([_cpuTile, _gpuTile, _fan1Tile, _fan2Tile]);
        AddFlow(MakeSection("PERFORMANCE MODE"), 36);
        AddFlow(_ecoCard, 84);
        AddFlow(_balancedCard, 84, 10);
        AddFlow(_perfCard, 84, 10);
        _vitalsSection = MakeSection("SYSTEM VITALS");
        AddFlow(_vitalsSection, 36);
        AddFlow(_tilesRow, 96, 10);
        _graphicsSection = MakeSection("GRAPHICS MODE");
        AddFlow(_graphicsSection, 36);
        AddFlow(BuildGraphicsPanel(), 78, 10);
        _fanSection = MakeSection("FAN CONTROL");
        AddFlow(_fanSection, 36);
        AddFlow(BuildFanPanel(), 128);
        AddFlow(MakeSection("SETTINGS"), 36);
        AddFlow(BuildSettingsPanel(), 234);

        foreach (var (control, _, _) in _flowItems)
            _root.Controls.Add(control);
        Controls.Add(_root);
        Controls.Add(_statusLabel);
        Controls.Add(header);
    }

    private void AddFlow(Control control, int designHeight, int gap = 0)
    {
        _flowItems.Add((control, designHeight, gap));
    }

    private Control MakeSection(string text)
    {
        return new Label
        {
            Text = text,
            Font = UiTheme.SmallLabel(DeviceDpi),
            ForeColor = UiTheme.TextSecondary,
            AutoSize = false,
            TextAlign = ContentAlignment.BottomLeft,
        };
    }

    private Control BuildGraphicsPanel()
    {
        _graphicsPanel = new RoundedPanel();
        var modeLabel = new Label
        {
            Text = "Mode",
            ForeColor = UiTheme.TextSecondary,
            BackColor = UiTheme.Surface,
            AutoSize = true,
            Location = new Point(Scaled(16), Scaled(10)),
        };
        _graphicsPanel.Controls.Add(modeLabel);

        StyleSegmentButton(_hybridButton, "Hybrid (auto)");
        StyleSegmentButton(_discreteButton, "Discrete (dGPU)");
        _hybridButton.Click += (_, _) => _app.RequestGpuMode(GpuMode.Hybrid);
        _discreteButton.Click += (_, _) => _app.RequestGpuMode(GpuMode.Discrete);
        _graphicsPanel.Controls.Add(_hybridButton);
        _graphicsPanel.Controls.Add(_discreteButton);

        _gpuModeNote.ForeColor = UiTheme.TextSecondary;
        _gpuModeNote.BackColor = UiTheme.Surface;
        _gpuModeNote.Font = UiTheme.SmallLabel(DeviceDpi);
        _gpuModeNote.AutoSize = true;
        _graphicsPanel.Controls.Add(_gpuModeNote);
        PositionFanPanelChildren(); // also lays out the graphics buttons
        return _graphicsPanel;
    }

    private Control BuildFanPanel()
    {
        var panel = new RoundedPanel();
        var modeLabel = new Label
        {
            Text = "Mode",
            ForeColor = UiTheme.TextSecondary,
            BackColor = UiTheme.Surface,
            AutoSize = true,
            Location = new Point(Scaled(16), Scaled(10)),
        };
        panel.Controls.Add(modeLabel);

        StyleSegmentButton(_fanAutoButton, "Auto");
        StyleSegmentButton(_fanMaxButton, "Max 100%");
        StyleSegmentButton(_fanManualButton, "Manual");
        _fanAutoButton.Click += (_, _) => _app.SetFanMode(FanMode.Auto, 0);
        _fanMaxButton.Click += (_, _) => _app.SetFanMode(FanMode.Max, 0);
        _fanManualButton.Click += (_, _) => _app.SetFanMode(FanMode.Manual, Math.Max(_fanSlider.Value, 20));
        panel.Controls.Add(_fanAutoButton);
        panel.Controls.Add(_fanMaxButton);
        panel.Controls.Add(_fanManualButton);

        _fanSlider.Minimum = 1;
        _fanSlider.Maximum = 100;
        _fanSlider.TickStyle = TickStyle.None;
        _fanSlider.ValueChanged += (_, _) =>
        {
            _fanSliderValue.Text = $"{_fanSlider.Value}%";
            if (_suppressSliderEvent)
                return;
            _fanSliderDebounce.Stop();
            _fanSliderDebounce.Start();
        };
        panel.Controls.Add(_fanSlider);

        _fanSliderValue.AutoSize = true;
        _fanSliderValue.ForeColor = UiTheme.TextPrimary;
        _fanSliderValue.BackColor = UiTheme.Surface;
        panel.Controls.Add(_fanSliderValue);
        panel.Resize += (_, _) => LayoutFanSlider(panel);
        return panel;
    }

    // shared absolute layout for the graphics buttons + fan slider value label
    private void PositionFanPanelChildren()
    {
        if (_graphicsPanel is null)
            return;
        Size button = new(Scaled(150), Scaled(28));
        _hybridButton.SetBounds(Scaled(16), Scaled(34), button.Width, button.Height);
        _discreteButton.SetBounds(Scaled(174), Scaled(34), button.Width, button.Height);
        _gpuModeNote.Location = new Point(Scaled(336), Scaled(41));
        _fanAutoButton.Size = _fanMaxButton.Size = _fanManualButton.Size = new Size(Scaled(76), Scaled(28));
        _fanAutoButton.Location = new Point(Scaled(16), Scaled(34));
        _fanMaxButton.Location = new Point(Scaled(96), Scaled(34));
        _fanManualButton.Location = new Point(Scaled(196), Scaled(34));
        if (_fanSlider.Parent is Panel parent)
            LayoutFanSlider(parent);
    }

    private void LayoutFanSlider(Panel panel)
    {
        _fanSlider.SetBounds(Scaled(16), Scaled(72), Math.Max(Scaled(120), panel.Width - Scaled(130) - _fanSliderValue.PreferredWidth), Scaled(42));
        _fanSliderValue.Location = new Point(panel.Width - _fanSliderValue.PreferredWidth - Scaled(16), Scaled(82));
    }

    private void StyleSegmentButton(Button button, string text)
    {
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = UiTheme.Border;
        button.FlatAppearance.MouseOverBackColor = UiTheme.SurfaceHover;
        button.BackColor = UiTheme.SurfaceActive;
        button.ForeColor = UiTheme.TextSecondary;
        button.Size = new Size(Scaled(76), Scaled(28));
        button.Font = UiTheme.CardBody(DeviceDpi);
        button.Cursor = Cursors.Hand;
    }

    private Panel _settingsPanel = null!;

    private Control BuildSettingsPanel()
    {
        _settingsPanel = new RoundedPanel();

        _settingsPanel.Controls.AddRange(MakeSettingRow("Show on-screen display", "Overlay on mode change (no focus steal)", _osdSwitch));
        _settingsPanel.Controls.AddRange(MakeSettingRow("Keep mode alive", "Re-apply the mode every 75 s against EC resets", _keepAliveSwitch));
        _settingsPanel.Controls.AddRange(MakeSettingRow("Start with Windows", "Elevated logon task, no UAC prompt", _autostartSwitch));
        _settingsPanel.Controls.AddRange(MakeSettingRow("Start minimized to tray", "No window after login, tray icon only", _startMinimizedSwitch));
        _settingsPanel.Controls.AddRange(MakeSettingRow("Close button minimizes to tray", "Full exit stays in the tray menu", _closeToTraySwitch));
        _settingsPanel.Resize += (_, _) => PositionSettingsToggles(_settingsPanel);
        PositionSettingsToggles(_settingsPanel);
        return _settingsPanel;
    }

    private Control[] MakeSettingRow(string title, string hint, ToggleSwitch toggle)
    {
        var label = new Label
        {
            Text = title,
            ForeColor = UiTheme.TextPrimary,
            BackColor = UiTheme.Surface,
            AutoSize = true,
            Font = UiTheme.Subtitle(DeviceDpi),
        };
        var note = new Label
        {
            Text = hint,
            ForeColor = UiTheme.TextSecondary,
            BackColor = UiTheme.Surface,
            AutoSize = true,
            Font = UiTheme.SmallLabel(DeviceDpi),
        };
        toggle.Tag = toggle; // marker; y is computed in PositionSettingsToggles
        _settingsToggles.Add((label, note, toggle));
        return [label, note, toggle];
    }

    private readonly List<(Label Label, Label Note, ToggleSwitch Toggle)> _settingsToggles = [];

    // row geometry is recomputed from current font metrics, so 125-150% dpi
    // never makes the hint text collide with the next row's title
    private void PositionSettingsToggles(Panel? panel)
    {
        if (panel is null)
            return;
        int dpi = DeviceDpi;
        int labelH = UiTheme.Subtitle(dpi).Height;
        int noteH = UiTheme.SmallLabel(dpi).Height;
        int rowGap = UiTheme.Scaled(8, dpi);
        int y = UiTheme.Scaled(12, dpi);
        foreach ((Label label, Label note, ToggleSwitch toggle) in _settingsToggles)
        {
            label.Location = new Point(UiTheme.Scaled(16, dpi), y);
            note.Location = new Point(UiTheme.Scaled(34, dpi), y + labelH + UiTheme.Scaled(2, dpi));
            toggle.Location = new Point(panel.Width - toggle.Width - UiTheme.Scaled(18, dpi), y + (labelH - toggle.Height) / 2);
            y += labelH + noteH + rowGap + UiTheme.Scaled(10, dpi);
        }
    }

    private void LayoutContent()
    {
        // a vertical scrollbar shrinks ClientSize after the first pass; two passes settle it
        for (int pass = 0; pass < 2; pass++)
        {
            int width = _root.ClientSize.Width - Scaled(40); // 20 px side padding
            int y = Scaled(4);

            foreach ((Control control, int designHeight, int gap) in _flowItems)
            {
                if (!control.Visible)
                    continue;
                control.Height = Scaled(designHeight);
                control.SetBounds(Scaled(20), y, width, control.Height);
                if (control == _tilesRow)
                {
                    // only visible tiles occupy slots: no holes for unsupported sensors
                    int tileWidth = Math.Max(Scaled(90), (width - Scaled(30)) / 4);
                    int index = 0;
                    foreach (SensorTile tile in _tiles)
                    {
                        if (!tile.Visible)
                            continue;
                        tile.SetBounds(index * (tileWidth + Scaled(10)), 0, tileWidth, _tilesRow.Height);
                        index++;
                    }
                }
                y += control.Height + Scaled(gap);
            }
        }
        _root.AutoScrollMargin = new Size(0, Scaled(12));
    }

    private void BindEvents()
    {
        _osdSwitch.CheckedChanged += (_, _) =>
        {
            if (_suppressSettingsEvents)
                return;
            _app.Settings.ShowOsd = _osdSwitch.Checked;
            _app.Settings.Save();
        };
        _keepAliveSwitch.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettingsEvents)
                _app.SetKeepAlive(_keepAliveSwitch.Checked);
        };
        _autostartSwitch.CheckedChanged += (_, _) =>
        {
            if (!_suppressSettingsEvents)
                _app.ToggleAutostart();
        };
        _startMinimizedSwitch.CheckedChanged += (_, _) =>
        {
            if (_suppressSettingsEvents)
                return;
            _app.Settings.StartMinimized = _startMinimizedSwitch.Checked;
            _app.Settings.Save();
        };
        _closeToTraySwitch.CheckedChanged += (_, _) =>
        {
            if (_suppressSettingsEvents)
                return;
            _app.Settings.CloseToTray = _closeToTraySwitch.Checked;
            _app.Settings.Save();
        };
    }

    private void RefreshFromApp()
    {
        // feature-adaptive visibility: unsupported sections disappear, layout reflows
        bool hw = _app.HardwarePresent;
        _vitalsSection.Visible = hw && (_app.Caps.Temperatures || _app.Caps.FanLevels);
        _tilesRow.Visible = _vitalsSection.Visible;
        _cpuTile.Visible = hw && _app.Caps.Temperatures;
        _gpuTile.Visible = hw && _app.Caps.Temperatures;
        _fan1Tile.Visible = hw && _app.Caps.FanLevels;
        _fan2Tile.Visible = hw && _app.Caps.FanLevels;
        _graphicsSection.Visible = hw && _app.Caps.GpuModeSwitch;
        _graphicsPanel.Visible = _graphicsSection.Visible;
        _fanSection.Visible = hw && (_app.Caps.FanLevels || _app.Caps.MaxFan);
        _fanMaxButton.Visible = hw && _app.Caps.MaxFan;
        _fanManualButton.Visible = hw && _app.Caps.FanLevels;
        LayoutContent();

        if (!hw)
        {
            _profileChip.Text = "NO HW";
            _profileChip.ForeColor = UiTheme.TextSecondary;
            _statusLabel.Text = $"Hardware not supported: {_app.HardwareError}";
            return;
        }

        _ecoCard.Active = _app.Current == PowerProfile.Eco;
        _balancedCard.Active = _app.Current == PowerProfile.Balanced;
        _perfCard.Active = _app.Current == PowerProfile.Performance;

        Color accent = UiTheme.AccentOf(_app.Current);
        _profileChip.ForeColor = accent;
        _profileChip.Text = _app.Current.ToString().ToUpperInvariant();
        Text = $"VictusControl — {_app.Current}";

        _suppressSettingsEvents = true;
        _osdSwitch.Checked = _app.Settings.ShowOsd;
        _keepAliveSwitch.Checked = _app.Settings.KeepAlive;
        _autostartSwitch.Checked = _app.AutostartEnabled;
        _startMinimizedSwitch.Checked = _app.Settings.StartMinimized;
        _closeToTraySwitch.Checked = _app.Settings.CloseToTray;
        _suppressSettingsEvents = false;

        SelectSegment(_fanAutoButton, _app.FanModeState == FanMode.Auto);
        SelectSegment(_fanMaxButton, _app.FanModeState == FanMode.Max);
        SelectSegment(_fanManualButton, _app.FanModeState == FanMode.Manual);
        if (_app.FanModeState == FanMode.Manual && _app.FanManualPercent is > 0)
        {
            _suppressSliderEvent = true;
            _fanSlider.Value = Math.Min(_app.FanManualPercent, 100);
            _suppressSliderEvent = false;
        }
        _fanSliderValue.Text = $"{_fanSlider.Value}%";
        _fanSlider.Enabled = _app.FanModeState == FanMode.Manual;

        GpuMode? currentGpu = _app.CurrentGpuMode;
        // Optimus lights up neither segment: it is a read-only firmware state
        SelectSegment(_hybridButton, currentGpu == GpuMode.Hybrid);
        SelectSegment(_discreteButton, currentGpu == GpuMode.Discrete);
        _gpuModeNote.Text = currentGpu switch
        {
            GpuMode.Hybrid => "current: hybrid · reboot to switch",
            { } mode => $"current: {mode} · reboot to switch",
            null => "reading…",
        };

        SensorSnapshot? s = _app.LastSensors;
        if (s is null)
        {
            _statusLabel.Text = "Reading firmware state…";
            return;
        }
        _statusLabel.Text = s.Success
            ? $"Live · thermal policy {s.Policy}"
            : "WMI busy — showing last known values";

        TempColor(s.CpuTemp, out Color cpuBar, out float cpuLevel);
        TempColor(s.GpuTemp, out Color gpuBar, out float gpuLevel);
        _cpuTile.Update(s.CpuTemp > 0 ? s.CpuTemp.ToString() : "--", s.CpuTemp > 0 ? cpuLevel : 0, cpuBar);
        _gpuTile.Update(s.GpuTemp > 0 ? s.GpuTemp.ToString() : "--", s.GpuTemp > 0 ? gpuLevel : 0, gpuBar);
        _fan1Tile.Update(
            s.Fan1Rpm > 0 ? (s.Fan1Rpm / 1000.0).ToString("0.0k", System.Globalization.CultureInfo.InvariantCulture) : "--",
            Math.Min(s.Fan1Rpm / 6000f, 1f), UiTheme.Balanced);
        _fan2Tile.Update(
            s.Fan2Rpm > 0 ? (s.Fan2Rpm / 1000.0).ToString("0.0k", System.Globalization.CultureInfo.InvariantCulture) : "--",
            Math.Min(s.Fan2Rpm / 6000f, 1f), UiTheme.Balanced);
    }

    private static void TempColor(int temp, out Color bar, out float level)
    {
        level = Math.Min(temp / 100f, 1f);
        bar = temp >= 85 ? UiTheme.Performance : temp >= 70 ? Color.FromArgb(245, 180, 80) : UiTheme.Eco;
    }

    private void SelectSegment(Button button, bool selected)
    {
        button.ForeColor = selected ? UiTheme.Accent : UiTheme.TextSecondary;
        button.FlatAppearance.BorderColor = selected ? UiTheme.Accent : UiTheme.Border;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        EnableDarkTitleBar();
    }

    // double-buffer the whole form: cards and tiles repaint without flicker
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x02000000; // WS_EX_COMPOSITED
            return cp;
        }
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            int on = 1;
            if (DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int)); // pre-20H1 attribute id
        }
        catch
        {
            // unsupported dwm: light title bar is acceptable
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // flat rounded card used for the fan/settings sections
    private sealed class RoundedPanel : Panel
    {
        public RoundedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(UiTheme.Background);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            UiTheme.PaintRounded(e, new Rectangle(0, 0, Width - 1, Height - 1), UiTheme.Surface, UiTheme.Border);
        }
    }
}
