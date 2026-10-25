using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VictusControl.Core;
using WinForms = System.Windows.Forms;

namespace VictusControl.App;

public partial class MainWindow : Window
{
    private readonly AppEngine _engine;
    private bool _suppress;
    private readonly System.Windows.Threading.DispatcherTimer _fanDebounce;

    public MainWindow(AppEngine engine)
    {
        _engine = engine;
        InitializeComponent();
        Icon = ProfileIcons.AppIconImageSource;

        RestoreWindowPosition();

        _fanDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _fanDebounce.Tick += (_, _) =>
        {
            _fanDebounce.Stop();
            _engine.SetFanMode(FanMode.Manual, (int)FanSlider.Value);
        };

        _engine.StateChanged += RefreshFromEngine;
        _engine.ErrorRaised += OnEngineError;
        RefreshFromEngine();
    }

    private void RestoreWindowPosition()
    {
        if (_engine.Settings.WindowLeft == int.MinValue)
            return;
        double x = _engine.Settings.WindowLeft, y = _engine.Settings.WindowTop;
        var screen = WinForms.Screen.FromPoint(new System.Drawing.Point((int)x, (int)y));
        var wa = screen.WorkingArea;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(x, wa.Left - Width + 120, wa.Right - 120);
        Top = Math.Clamp(y, wa.Top, wa.Bottom - 60);
    }

    // ---- header / window ---------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Exit_Click(object sender, RoutedEventArgs e) => _engine.ExitApp();

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        bool expanded = ExtraPanel.Visibility == Visibility.Collapsed;
        ExtraPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SettingsChevron.Content = expanded ? "\uE70E" : "\uE70D"; // chevron up/down
    }

    private void HideToTray()
    {
        _engine.SuspendSensors();
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // close button / alt-f4: hide to tray unless the engine is exiting
        if (!_engine.IsExiting && _engine.Settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _engine.Settings.WindowLeft = (int)Left;
        _engine.Settings.WindowTop = (int)Top;
        _engine.Settings.Save();
        _engine.SuspendSensors();
        _engine.StateChanged -= RefreshFromEngine;
        _engine.ErrorRaised -= OnEngineError;
        base.OnClosing(e);
    }

    // ---- profile pills -----------------------------------------------------

    private void Eco_Checked(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.ApplyProfile(PowerProfile.Eco);
    }

    private void Balanced_Checked(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.ApplyProfile(PowerProfile.Balanced);
    }

    private void Perf_Checked(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.ApplyProfile(PowerProfile.Performance);
    }

    // ---- fan ---------------------------------------------------------------

    private void FanAuto_Checked(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.SetFanMode(FanMode.Auto, 0);
    }

    private void FanMax_Checked(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.SetFanMode(FanMode.Max, 0);
    }

    private void FanSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // ValueChanged fires during XAML parse before FanPercent exists
        if (FanPercent is null)
            return;
        FanPercent.Text = $"{(int)FanSlider.Value}%";
        if (!_suppress)
        {
            _fanDebounce.Stop();
            _fanDebounce.Start();
        }
    }

    // ---- graphics ----------------------------------------------------------

    private void Hybrid_Click(object sender, RoutedEventArgs e) => _engine.RequestGpuMode(GpuMode.Hybrid);

    private void Discrete_Click(object sender, RoutedEventArgs e) => _engine.RequestGpuMode(GpuMode.Discrete);

    // ---- settings ----------------------------------------------------------

    private void Osd_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
        {
            _engine.Settings.ShowOsd = OsdToggle.IsChecked == true;
            _engine.Settings.Save();
        }
    }

    private void KeepAlive_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.SetKeepAlive(KeepAliveToggle.IsChecked == true);
    }

    private void Autostart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
            _engine.ToggleAutostart();
    }

    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
        {
            _engine.Settings.StartMinimized = StartMinimizedToggle.IsChecked == true;
            _engine.Settings.Save();
        }
    }

    private void CloseToTray_Changed(object sender, RoutedEventArgs e)
    {
        if (!_suppress)
        {
            _engine.Settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
            _engine.Settings.Save();
        }
    }

    // ---- refresh from engine -------------------------------------------------

    private void OnEngineError(string message) => HeaderStatus.Text = message;

    private void RefreshFromEngine()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(RefreshFromEngine);
            return;
        }

        _suppress = true;

        bool hw = _engine.HardwarePresent;
        PowerProfile current = _engine.Current;
        EcoPill.IsChecked = hw && current == PowerProfile.Eco;
        BalancedPill.IsChecked = hw && current == PowerProfile.Balanced;
        PerfPill.IsChecked = hw && current == PowerProfile.Performance;
        EcoPill.IsEnabled = BalancedPill.IsEnabled = PerfPill.IsEnabled = hw;

        FanSection.Visibility = hw && (_engine.Caps.FanLevels || _engine.Caps.MaxFan)
            ? Visibility.Visible : Visibility.Collapsed;
        FanAutoPill.IsChecked = _engine.FanModeState == FanMode.Auto;
        FanMaxPill.IsChecked = _engine.FanModeState == FanMode.Max;
        if (FanAutoPill.IsChecked != true && FanMaxPill.IsChecked != true)
        {
            // manual: the slider itself signals it; highlight none of the pills
        }
        FanAutoPill.Visibility = _engine.Caps.MaxFan ? Visibility.Visible : Visibility.Collapsed;
        if (_engine.FanModeState == FanMode.Manual && _engine.FanManualPercent is > 0)
            FanSlider.Value = Math.Min(_engine.FanManualPercent, 100);
        FanSlider.IsEnabled = hw && _engine.Caps.FanLevels;

        GpuMode? gpu = _engine.CurrentGpuMode;
        HybridPill.IsChecked = gpu == GpuMode.Hybrid;
        DiscretePill.IsChecked = gpu == GpuMode.Discrete;
        GraphicsNote.Text = gpu switch
        {
            GpuMode.Hybrid => "current: hybrid · applies after reboot",
            { } mode => $"current: {mode} · applies after reboot",
            null => "reading… · applies after reboot",
        };

        OsdToggle.IsChecked = _engine.Settings.ShowOsd;
        KeepAliveToggle.IsChecked = _engine.Settings.KeepAlive;
        AutostartToggle.IsChecked = _engine.AutostartEnabled;
        StartMinimizedToggle.IsChecked = _engine.Settings.StartMinimized;
        CloseToTrayToggle.IsChecked = _engine.Settings.CloseToTray;

        _suppress = false;

        // recolor the active profile pill by mode (eco green / balanced cyan / perf red)
        System.Windows.Media.Brush accent = current switch
        {
            PowerProfile.Eco => (System.Windows.Media.Brush)FindResource("Eco"),
            PowerProfile.Performance => (System.Windows.Media.Brush)FindResource("Perf"),
            _ => (System.Windows.Media.Brush)FindResource("Accent"),
        };
        SetActivePillFill(EcoPill, current == PowerProfile.Eco && hw, accent);
        SetActivePillFill(BalancedPill, current == PowerProfile.Balanced && hw, accent);
        SetActivePillFill(PerfPill, current == PowerProfile.Performance && hw, accent);

        if (_engine.LastSensors is { Success: true } s)
        {
            HeaderStatus.Text = $"CPU {s.CpuTemp}°  GPU {s.GpuTemp}°";
            FanReadout.Text = s.Fan1Rpm > 0 ? $"{s.Fan1Rpm}/{s.Fan2Rpm} RPM" : "";
        }
        FooterNote.Text = hw
            ? $"Thermal policy {_engine.Caps.Policy?.ToString() ?? "unknown"} · Fn+P cycles modes"
            : $"Not supported: {_engine.HardwareError}";
    }

    // pill template hides its Root; nudge the fill through the visual tree
    private void SetActivePillFill(System.Windows.Controls.RadioButton pill, bool active, System.Windows.Media.Brush accent)
    {
        if (pill.Template?.FindName("Root", pill) is Border root)
            root.Background = active ? accent : (System.Windows.Media.Brush)FindResource("Pill");
        if (active)
            pill.Foreground = (System.Windows.Media.Brush)FindResource("AccentText");
    }
}
