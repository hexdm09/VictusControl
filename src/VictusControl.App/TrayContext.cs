using VictusControl.Core;

namespace VictusControl.App;

internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(75);

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _ecoItem;
    private readonly ToolStripMenuItem _balancedItem;
    private readonly ToolStripMenuItem _perfItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _keepAliveItem;
    private readonly HotkeyManager _hotkeys;
    private readonly System.Threading.Timer _keepAliveTimer;

    private PowerProfile _current = PowerProfile.Balanced;
    private bool _keepAlive = true;

    public TrayContext()
    {
        _ecoItem = new ToolStripMenuItem("ECO", null, (_, _) => Apply(PowerProfile.Eco));
        _balancedItem = new ToolStripMenuItem("Balanced", null, (_, _) => Apply(PowerProfile.Balanced));
        _perfItem = new ToolStripMenuItem("Performance", null, (_, _) => Apply(PowerProfile.Performance));
        _autostartItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart())
        {
            Checked = AutostartManager.IsEnabled(),
        };
        _keepAliveItem = new ToolStripMenuItem("Keep mode alive", null, (_, _) => ToggleKeepAlive())
        {
            Checked = _keepAlive,
            ToolTipText = "Re-apply the mode every 75s so the EC firmware cannot silently reset it.",
        };

        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _ecoItem, _balancedItem, _perfItem,
            new ToolStripSeparator(),
            _autostartItem, _keepAliveItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()),
        ]);
        menu.Opening += (_, _) => RefreshState();

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

        _keepAliveTimer = new System.Threading.Timer(_ => KeepAliveTick(), null, KeepAliveInterval, KeepAliveInterval);

        RefreshState();
    }

    private void RefreshState()
    {
        try
        {
            using var client = new HpWmiClient();
            var controller = new PowerController(client);
            GpuPowerData gpu = controller.GetGpuPower();
            _current = (gpu.CustomTgp, gpu.Ppab) switch
            {
                (0x01, 0x01) => PowerProfile.Performance,
                (_, 0x01) => PowerProfile.Balanced,
                _ => PowerProfile.Eco,
            };
        }
        catch
        {
            // Keep the last known state if WMI is momentarily unavailable.
        }

        _ecoItem.Checked = _current == PowerProfile.Eco;
        _balancedItem.Checked = _current == PowerProfile.Balanced;
        _perfItem.Checked = _current == PowerProfile.Performance;
        _icon.Icon = _current switch
        {
            PowerProfile.Eco => ProfileIcons.Eco,
            PowerProfile.Performance => ProfileIcons.Performance,
            _ => ProfileIcons.Balanced,
        };
        _icon.Text = $"VictusControl — {_current}";
    }

    private void Apply(PowerProfile profile, bool silent = false)
    {
        try
        {
            using var client = new HpWmiClient();
            new PowerController(client).ApplyProfile(profile);
            _current = profile;
            RefreshState();
            if (!silent)
                OsdForm.ShowToast(Label(profile), Accent(profile));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Cycle()
    {
        PowerProfile next = _current switch
        {
            PowerProfile.Eco => PowerProfile.Balanced,
            PowerProfile.Balanced => PowerProfile.Performance,
            _ => PowerProfile.Eco,
        };
        Apply(next);
    }

    private void KeepAliveTick()
    {
        if (_keepAlive)
            Apply(_current, silent: true);
    }

    private void ToggleAutostart()
    {
        try
        {
            if (AutostartManager.IsEnabled())
                AutostartManager.Disable();
            else
                AutostartManager.Enable(Application.ExecutablePath);
            _autostartItem.Checked = AutostartManager.IsEnabled();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VictusControl", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleKeepAlive() => (_keepAlive, _keepAliveItem.Checked) = (!_keepAlive, !_keepAlive);

    private static string Label(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => "ECO mode",
        PowerProfile.Performance => "Performance mode",
        _ => "Balanced mode",
    };

    private static Color Accent(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => Color.LimeGreen,
        PowerProfile.Performance => Color.OrangeRed,
        _ => Color.DodgerBlue,
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
