using VictusControl.Core;

namespace VictusControl.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(75);
    private const int CycleDebounceMs = 500;

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _ecoItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _perfItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _keepAliveItem;
    private readonly HotkeyManager _hotkeys;
    private readonly System.Windows.Forms.Timer _keepAliveTimer;
    private readonly SynchronizationContext _sync;

    private PowerProfile _current = PowerProfile.Balanced;
    private bool _keepAlive = true;
    private int _busy;
    private int _lastCycleTick;

    public TrayContext()
    {
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _ecoItem = new ToolStripMenuItem("ECO", null, (_, _) => Apply(PowerProfile.Eco));
        _balancedItem = new ToolStripMenuItem("Balanced", null, (_, _) => Apply(PowerProfile.Balanced));
        _perfItem = new ToolStripMenuItem("Performance", null, (_, _) => Apply(PowerProfile.Performance));
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = QueryAutostartSafe(),
        };
        _keepAliveItem = new ToolStripMenuItem("Keep mode alive", null, (_, _) => ToggleKeepAlive())
        {
            Checked = _keepAlive,
            ToolTipText = $"Re-apply the mode every {KeepAliveInterval.TotalSeconds:0}s so the EC firmware cannot silently reset it.",
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _ecoItem, _balancedItem, _perfItem,
            new ToolStripSeparator(),
            _autostartItem, _keepAliveItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) =>
        {
            UpdateUi();
            RefreshFromFirmwareAsync();
        };

        _icon = new NotifyIcon
        {
            Text = "VictusControl",
            Icon = ProfileIcons.Idle,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Cycle();

        _hotkeys = new HotkeyManager();
        _hotkeys.CycleRequested += Cycle;
        _hotkeys.Start();

        // 75s tick: prevent ec from silently resetting custom fan profile
        _keepAliveTimer = new System.Windows.Forms.Timer { Interval = (int)KeepAliveInterval.TotalMilliseconds };
        _keepAliveTimer.Tick += (_, _) =>
        {
            if (_keepAlive)
                Apply(_current, silent: true);
        };
        _keepAliveTimer.Start();

        UpdateUi();
        RefreshFromFirmwareAsync();
    }

    private void UpdateUi()
    {
        _ecoItem.Checked = _current == PowerProfile.Eco;
        _balancedItem.Checked = _current == PowerProfile.Balanced;
        _perfItem.Checked = _current == PowerProfile.Performance;
        _icon.Icon = ProfileIcons.IconFor(_current);
        _icon.Text = $"VictusControl: {_current}";
    }

    private void RefreshFromFirmwareAsync()
    {
        Task.Run(() =>
        {
            try
            {
                using var client = new HpWmiClient();
                PowerProfile actual = new PowerController(client).GetGpuPower().ToProfile();
                _sync.Post(_ =>
                {
                    _current = actual;
                    UpdateUi();
                }, null);
            }
            catch
            {
                // wmi temporarily busy; keep cached state
            }
        });
    }

    private void Apply(PowerProfile profile, bool silent = false)
    {
        // Serializes keep-alive ticks against manual/hotkey switches.
        if (Interlocked.Exchange(ref _busy, 1) != 0)
            return;
        Task.Run(() =>
        {
            Exception? error = null;
            try
            {
                using var client = new HpWmiClient();
                new PowerController(client).ApplyProfile(profile);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            _sync.Post(_ =>
            {
                try
                {
                    if (error is null)
                    {
                        _current = profile;
                        UpdateUi();
                        if (!silent)
                            OsdForm.ShowToast(Label(profile), profile);
                    }
                    else
                    {
                        MessageBox.Show(error.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            }, null);
        });
    }

    private void Cycle()
    {
        int now = Environment.TickCount;
        if (now - _lastCycleTick < CycleDebounceMs)
            return;
        _lastCycleTick = now;
        Apply(_current.Next());
    }

    private void ToggleAutostart()
    {
        try
        {
            if (AutostartManager.IsEnabled())
                AutostartManager.Disable();
            else
                AutostartManager.Enable(Application.ExecutablePath);
            _autostartItem.Checked = QueryAutostartSafe();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool QueryAutostartSafe()
    {
        try
        {
            return AutostartManager.IsEnabled();
        }
        catch
        {
            // A hung schtasks must never kill the tray at startup.
            return false;
        }
    }

    private void ToggleKeepAlive() => (_keepAlive, _keepAliveItem.Checked) = (!_keepAlive, !_keepAlive);

    private static string Label(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => "ECO mode",
        PowerProfile.Performance => "Performance mode",
        _ => "Balanced mode",
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keepAliveTimer.Dispose();
            _hotkeys.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
