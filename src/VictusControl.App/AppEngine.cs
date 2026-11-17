using System.IO;
using System.Windows;
using System.Windows.Threading;
using VictusControl.Core;
using WinForms = System.Windows.Forms;

namespace VictusControl.App;

public enum FanMode
{
    Auto,
    Curve,
    Max,
    Manual,
}

// one sensor read result (fresh client per poll; WMI has no cancellation)
public sealed record SensorSnapshot(
    bool Success,
    int CpuTemp,
    int GpuTemp,
    bool GpuSleepingInEco,
    int Fan1Rpm,
    int Fan2Rpm,
    bool OnBattery,
    int BatteryPercent,
    double? BatteryWatts,
    ThermalPolicy? Policy);

// application engine: owns the main window, tray icon, apply queue, hotkeys,
// fan-only keep-alive, temperature fan curve, capability probe and sensor poll loop.
public sealed class AppEngine : IDisposable
{
    // Matches Linux 7.0 hp-wmi 90s keep-alive (refreshes ONLY manual/max fan state, never GPU 0x22)
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CurveInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SensorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SensorLockTimeout = TimeSpan.FromSeconds(3);
    private const int CycleDebounceMs = 500;

    // the EC exposes a single ACPI mailbox: every firmware command sequence
    // must take this lock, otherwise concurrent reads interleave with writes
    private readonly SemaphoreSlim _firmwareLock = new(1, 1);

    private readonly EventWaitHandle _showSignal;
    private readonly Thread _showSignalThread;

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _ecoItem;
    private readonly WinForms.ToolStripMenuItem _balancedItem;
    private readonly WinForms.ToolStripMenuItem _perfItem;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _keepAliveTimer;
    private readonly DispatcherTimer _curveTimer;
    private readonly DispatcherTimer _sensorTimer;
    private readonly SynchronizationContext _sync;

    private MainWindow? _main;
    private bool _windowVisible;

    private PowerProfile _current = PowerProfile.Balanced;
    private FanMode _fanMode = FanMode.Auto;
    private int _fanPercent;
    private int? _curveAnchorTemp;
    private int _curveAppliedPercent;
    private bool _autostartEnabled;
    private int _lastCycleTick;
    private int _sensorPollsInFlight;
    private int _curveTicksInFlight;

    // coalescing queue: latest request wins, nothing is silently dropped
    private readonly object _queueLock = new();
    private PowerProfile? _pending;
    private bool _pendingSilent;
    private int _pumping;
    private long _seq;

    private readonly object _fanLock = new();
    private bool _fanRunning;
    private bool _fanQueued;
    private bool _fanOverrideActive;

    public AppSettings Settings { get; }
    public bool IsExiting { get; private set; }

    public BiosCapabilities Caps { get; private set; } = BiosCapabilities.None;
    public bool HardwarePresent { get; private set; } = true;
    public string HardwareError { get; private set; } = "";
    public GpuMode? CurrentGpuMode { get; private set; }

    public PowerProfile Current => _current;
    public FanMode FanModeState => _fanMode;
    public int FanManualPercent => _fanPercent;
    public int CurveCurrentPercent => _curveAppliedPercent;
    public bool AutostartEnabled => _autostartEnabled;
    public SensorSnapshot? LastSensors { get; private set; }

    public event Action? StateChanged;
    public event Action<string>? ErrorRaised;

    public AppEngine()
    {
        _sync = SynchronizationContext.Current
            ?? new DispatcherSynchronizationContext(System.Windows.Application.Current.Dispatcher);
        Settings = AppSettings.Load();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\VictusControlShowWindow");
        _showSignalThread = new Thread(ShowSignalLoop) { IsBackground = true, Name = "VictusControlShowSignal" };
        _showSignalThread.Start();

        _ecoItem = new WinForms.ToolStripMenuItem("ECO", null, (_, _) => ApplyProfile(PowerProfile.Eco));
        _balancedItem = new WinForms.ToolStripMenuItem("Balanced", null, (_, _) => ApplyProfile(PowerProfile.Balanced));
        _perfItem = new WinForms.ToolStripMenuItem("Performance", null, (_, _) => ApplyProfile(PowerProfile.Performance));
        var curveFanItem = new WinForms.ToolStripMenuItem("Smart fan curve", null, (_, _) => SetFanMode(_fanMode == FanMode.Curve ? FanMode.Auto : FanMode.Curve, 0));
        var maxFanItem = new WinForms.ToolStripMenuItem("Max fan speed (100%)", null, (_, _) => SetFanMode(_fanMode == FanMode.Max ? FanMode.Auto : FanMode.Max, 0));
        var openItem = new WinForms.ToolStripMenuItem("Open VictusControl", null, (_, _) => ShowMain());
        var exitItem = new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([
            openItem,
            new WinForms.ToolStripSeparator(),
            _ecoItem, _balancedItem, _perfItem,
            new WinForms.ToolStripSeparator(),
            curveFanItem,
            maxFanItem,
            new WinForms.ToolStripSeparator(),
            exitItem,
        ]);
        menu.Opening += (_, _) =>
        {
            curveFanItem.Checked = _fanMode == FanMode.Curve;
            maxFanItem.Checked = _fanMode == FanMode.Max;
            UpdateTrayIcon();
            if (_current != PowerProfile.Eco)
                RefreshProfileFromFirmware();
        };

        _icon = new WinForms.NotifyIcon
        {
            Text = "VictusControl",
            Icon = ProfileIcons.Idle,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowMain();

        _hotkeys = new HotkeyManager();
        _hotkeys.CycleRequested += Cycle;
        _hotkeys.Start();

        // 90s tick: ONLY refreshes non-Auto fan overrides so Eco/Auto never wakes dGPU or fights BIOS curve
        _keepAliveTimer = new DispatcherTimer { Interval = KeepAliveInterval };
        _keepAliveTimer.Tick += (_, _) =>
        {
            if (Settings.KeepAlive && HardwarePresent && _fanMode != FanMode.Auto)
                QueueFanApply();
        };
        _keepAliveTimer.Start();

        _curveTimer = new DispatcherTimer { Interval = CurveInterval };
        _curveTimer.Tick += (_, _) => StepFanCurve();

        _sensorTimer = new DispatcherTimer { Interval = SensorInterval };
        _sensorTimer.Tick += (_, _) => PollSensors();

        _autostartEnabled = QueryAutostartSafe();

        UpdateTrayIcon();
        ProbeCapabilities();
        RefreshProfileFromFirmware();
    }

    public void ShowMain()
    {
        void Show()
        {
            if (_main is null || !_main.IsLoaded)
                _main = new MainWindow(this);
            _main.ShowInTaskbar = true;
            _main.Show();
            _main.Activate();
            ResumeSensors();
        }

        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            Show();
        else
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(Show);
    }

    public void ExitApp()
    {
        IsExiting = true;
        void CloseAndShutdown()
        {
            _main?.Close();
            System.Windows.Application.Current.Shutdown();
        }
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
            CloseAndShutdown();
        else
            System.Windows.Application.Current?.Dispatcher.Invoke(CloseAndShutdown);
    }

    private void ShowSignalLoop()
    {
        try
        {
            while (_showSignal.WaitOne())
                _sync.Post(_ => ShowMain(), null);
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    // ---- sensors ---------------------------------------------------------

    public void ResumeSensors()
    {
        _windowVisible = true;
        _sensorTimer.Start();
        PollSensors();
    }

    public void SuspendSensors()
    {
        _windowVisible = false;
        _sensorTimer.Stop();
    }

    private void PollSensors()
    {
        if (!_windowVisible || !HardwarePresent || Interlocked.CompareExchange(ref _sensorPollsInFlight, 1, 0) != 0)
            return;

        long ticket = Interlocked.Read(ref _seq);
        bool wantTemps = Caps.Temperatures;
        bool wantFans = Caps.FanLevels;
        bool ecoMode = _current == PowerProfile.Eco;
        bool wantGpuPower = Caps.GpuPower && !ecoMode;
        bool wantGpuMode = Caps.GpuModeSwitch && !ecoMode && CurrentGpuMode is null;
        ThermalPolicy? policy = Caps.Policy;

        Task.Run(() =>
        {
            var (onBattery, batPct, batWatts) = WindowsPowerHelper.ReadBatteryState();
            bool skipGpuWmi = ecoMode && Settings.EcoPowerSave;

            SensorSnapshot snapshot;
            PowerProfile? actual = null;
            GpuMode? gpuMode = CurrentGpuMode;

            if (!_firmwareLock.Wait(SensorLockTimeout))
            {
                _sync.Post(_ => Interlocked.Exchange(ref _sensorPollsInFlight, 0), null);
                return;
            }
            try
            {
                using var client = new HpWmiClient();
                var controller = new PowerController(client);
                var (cpu, gpu) = wantTemps ? controller.GetTemperatures(includeGpu: !skipGpuWmi) : (0, 0);
                var (f1, f2) = wantFans ? controller.GetFanRpm() : (0, 0);
                if (wantGpuPower && !skipGpuWmi)
                    actual = controller.GetGpuPower().ToProfile();
                if (wantGpuMode && !skipGpuWmi)
                {
                    try { gpuMode = controller.GetGpuMode(); } catch { }
                }
                snapshot = new SensorSnapshot(true, cpu, gpu, skipGpuWmi, f1, f2, onBattery, batPct, batWatts, policy);
            }
            catch
            {
                snapshot = LastSensors is { } last
                    ? last with { Success = false, OnBattery = onBattery, BatteryPercent = batPct, BatteryWatts = batWatts }
                    : new SensorSnapshot(false, 0, 0, skipGpuWmi, 0, 0, onBattery, batPct, batWatts, null);
            }
            finally
            {
                _firmwareLock.Release();
            }

            _sync.Post(_ =>
            {
                Interlocked.Exchange(ref _sensorPollsInFlight, 0);
                LastSensors = snapshot;
                CurrentGpuMode = gpuMode;
                if (actual is { } profile && profile != _current && Interlocked.Read(ref _seq) == ticket)
                    _current = profile;
                StateChanged?.Invoke();
            }, null);
        });
    }

    private void RefreshProfileFromFirmware()
    {
        long ticket = Interlocked.Read(ref _seq);
        Task.Run(() =>
        {
            if (!_firmwareLock.Wait(SensorLockTimeout))
                return;
            try
            {
                using var client = new HpWmiClient();
                var controller = new PowerController(client);
                PowerProfile actual = controller.GetGpuPower().ToProfile();
                GpuMode? gpuMode = CurrentGpuMode;
                if (gpuMode is null)
                {
                    try { gpuMode = controller.GetGpuMode(); } catch { }
                }
                _sync.Post(_ =>
                {
                    if (Interlocked.Read(ref _seq) != ticket)
                        return;
                    _current = actual;
                    if (gpuMode is not null)
                        CurrentGpuMode = gpuMode;
                    UpdateTrayIcon();
                    StateChanged?.Invoke();
                }, null);
            }
            catch
            {
                // wmi temporarily busy; keep cached state
            }
            finally
            {
                _firmwareLock.Release();
            }
        });
    }

    // ---- capability probe --------------------------------------------------

    private void ProbeCapabilities()
    {
        Task.Run(() =>
        {
            if (!_firmwareLock.Wait(ApplyTimeout))
            {
                _sync.Post(_ => ErrorRaised?.Invoke("Capability probe timed out."), null);
                return;
            }
            try
            {
                using var client = new HpWmiClient();
                BiosCapabilities caps = CapabilityProbe.Probe(client);
                _sync.Post(_ =>
                {
                    Caps = caps;
                    StateChanged?.Invoke();
                }, null);
            }
            catch (Exception ex)
            {
                _sync.Post(_ =>
                {
                    HardwarePresent = false;
                    HardwareError = ex.Message;
                    StateChanged?.Invoke();
                }, null);
            }
            finally
            {
                _firmwareLock.Release();
            }
        });
    }

    // ---- profile apply queue ----------------------------------------------

    public void ApplyProfile(PowerProfile profile, bool silent = false)
    {
        lock (_queueLock)
        {
            _pending = profile;
            _pendingSilent = silent;
        }
        Pump();
    }

    private async void Pump()
    {
        if (Interlocked.Exchange(ref _pumping, 1) != 0)
            return;
        try
        {
            while (true)
            {
                PowerProfile next;
                bool silent;
                lock (_queueLock)
                {
                    if (_pending is null)
                        return;
                    next = _pending.Value;
                    silent = _pendingSilent;
                    _pending = null;
                }

                Exception? error = null;
                try
                {
                    await Task.Run(() =>
                    {
                        if (!_firmwareLock.Wait(ApplyTimeout))
                            throw new TimeoutException("Firmware busy (lock timeout).");
                        try
                        {
                            using var client = new HpWmiClient();
                            var controller = new PowerController(client);
                            controller.ApplyProfile(next, Caps);
                            if (_fanOverrideActive)
                                ReassertFanMode(controller);
                        }
                        finally
                        {
                            _firmwareLock.Release();
                        }

                        WindowsPowerHelper.ApplyCpuPowerPolicy(next, Settings.EcoPowerSave);
                        var (onBattery, _, _) = WindowsPowerHelper.ReadBatteryState();
                        bool use60Hz = next == PowerProfile.Eco && Settings.EcoPowerSave && onBattery;
                        DisplayRefreshManager.ApplyForEcoState(use60Hz);
                    }).WaitAsync(ApplyTimeout);
                }
                catch (Exception ex)
                {
                    error = ex;
                }

                Interlocked.Increment(ref _seq);
                if (error is null)
                {
                    _current = next;
                    UpdateTrayIcon();
                    if (!silent && Settings.ShowOsd)
                        OsdWindow.ShowToast(Label(next), next);
                    StateChanged?.Invoke();
                }
                else if (silent && error is TimeoutException)
                {
                    lock (_queueLock)
                    {
                        _pending = next;
                        _pendingSilent = true;
                    }
                    ErrorRaised?.Invoke($"Apply timed out: {error.Message} — retrying.");
                }
                else if (silent)
                {
                    ErrorRaised?.Invoke($"Background apply failed: {error.Message}");
                }
                else
                {
                    System.Windows.MessageBox.Show(_main, error.Message, "VictusControl",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    ErrorRaised?.Invoke(error.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
            lock (_queueLock)
            {
                if (_pending is not null)
                    Pump();
            }
        }
    }

    // ---- fan control & temperature curve ----------------------------------

    public void SetFanMode(FanMode mode, int percent)
    {
        if (!HardwarePresent || (!Caps.FanLevels && !Caps.MaxFan))
        {
            StateChanged?.Invoke();
            return;
        }
        if (mode == FanMode.Manual && percent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Manual fan percent must be 1..100.");
        if (mode is FanMode.Manual or FanMode.Curve && !Caps.FanLevels)
            mode = FanMode.Max;
        if (mode == FanMode.Curve && !Caps.Temperatures)
            mode = FanMode.Auto;
        if (mode == FanMode.Max && !Caps.MaxFan)
        {
            mode = FanMode.Manual;
            percent = 100;
        }

        _fanMode = mode;
        _fanPercent = mode == FanMode.Manual ? percent : 0;
        if (mode != FanMode.Curve)
        {
            _curveTimer.Stop();
            _curveAnchorTemp = null;
            _curveAppliedPercent = 0;
        }
        else
        {
            _curveTimer.Start();
        }

        UpdateTrayIcon();
        StateChanged?.Invoke();
        if (Settings.ShowOsd)
            OsdWindow.ShowToast(FanLabel(mode, _fanPercent), _current);
        QueueFanApply();
    }

    private void StepFanCurve()
    {
        if (_fanMode != FanMode.Curve || !HardwarePresent || !Caps.Temperatures || !Caps.FanLevels)
            return;
        if (Interlocked.CompareExchange(ref _curveTicksInFlight, 1, 0) != 0)
            return;

        bool skipGpuWmi = _current == PowerProfile.Eco && Settings.EcoPowerSave;
        VictusFanTable fanTable = Caps.EffectiveFanTable;

        Task.Run(() =>
        {
            try
            {
                if (!_firmwareLock.Wait(SensorLockTimeout))
                    return;
                try
                {
                    using var client = new HpWmiClient();
                    var controller = new PowerController(client);
                    var (cpu, gpu) = controller.GetTemperatures(includeGpu: !skipGpuWmi);
                    int hot = Math.Max(cpu, gpu);
                    int nextPct = FanCurve.BalancedDefault.Evaluate(hot, _curveAnchorTemp, _curveAppliedPercent);
                    if (nextPct != _curveAppliedPercent)
                    {
                        _fanOverrideActive = true;
                        controller.SetFanPercent(nextPct, nextPct, fanTable);
                        _curveAppliedPercent = nextPct;
                        _curveAnchorTemp = hot;
                        _sync.Post(_ =>
                        {
                            UpdateTrayIcon();
                            StateChanged?.Invoke();
                        }, null);
                    }
                }
                finally
                {
                    _firmwareLock.Release();
                }
            }
            catch
            {
                // transient WMI busy during curve step: retry on next 3s tick
            }
            finally
            {
                Interlocked.Exchange(ref _curveTicksInFlight, 0);
            }
        });
    }

    private void QueueFanApply()
    {
        lock (_fanLock)
        {
            _fanQueued = true;
            if (_fanRunning)
                return;
            _fanRunning = true;
        }
        Task.Run(() =>
        {
            while (true)
            {
                lock (_fanLock)
                {
                    if (!_fanQueued)
                    {
                        _fanRunning = false;
                        return;
                    }
                    _fanQueued = false;
                }
                Exception? error = null;
                try
                {
                    Task.Run(() =>
                    {
                        if (!_firmwareLock.Wait(ApplyTimeout))
                            throw new TimeoutException("Firmware busy (lock timeout).");
                        try
                        {
                            using var client = new HpWmiClient();
                            ReassertFanMode(new PowerController(client));
                        }
                        finally
                        {
                            _firmwareLock.Release();
                        }
                    }).Wait(ApplyTimeout);
                }
                catch (Exception ex)
                {
                    error = ex;
                }
                if (error is not null)
                    _sync.Post(_ => ErrorRaised?.Invoke($"Fan control failed: {error.Message}"), null);
            }
        });
    }

    private void ReassertFanMode(PowerController controller)
    {
        if (_fanMode == FanMode.Auto && !_fanOverrideActive)
            return;

        if (_fanMode == FanMode.Max && Caps.MaxFan)
        {
            _fanOverrideActive = true;
            controller.SetMaxFan(true);
        }
        else if (_fanMode == FanMode.Manual && _fanPercent > 0 && Caps.FanLevels)
        {
            _fanOverrideActive = true;
            controller.SetFanPercent(_fanPercent, _fanPercent, Caps.EffectiveFanTable);
        }
        else if (_fanMode == FanMode.Curve && Caps.FanLevels && Caps.Temperatures)
        {
            bool skipGpuWmi = _current == PowerProfile.Eco && Settings.EcoPowerSave;
            var (cpu, gpu) = controller.GetTemperatures(includeGpu: !skipGpuWmi);
            int hot = Math.Max(cpu, gpu);
            int pct = FanCurve.BalancedDefault.Evaluate(hot, _curveAnchorTemp, _curveAppliedPercent);
            _fanOverrideActive = true;
            controller.SetFanPercent(pct, pct, Caps.EffectiveFanTable);
            _curveAppliedPercent = pct;
            _curveAnchorTemp = hot;
        }
        else
        {
            controller.SetMaxFan(false);
            _fanOverrideActive = false;
        }
    }

    public void RequestGpuMode(GpuMode mode)
    {
        Window? owner = _main is { IsLoaded: true } && _main.IsVisible ? _main : null;
        var answer = System.Windows.MessageBox.Show(owner,
            $"Switch graphics mode to {mode}?\nThis changes a BIOS setting and takes effect after the next reboot.",
            "VictusControl — graphics mode",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
        {
            StateChanged?.Invoke();
            return;
        }

        Task.Run(() =>
        {
            try
            {
                if (!_firmwareLock.Wait(ApplyTimeout))
                    throw new TimeoutException("Firmware busy (lock timeout).");
                try
                {
                    using var client = new HpWmiClient();
                    new PowerController(client).SetGpuMode(mode);
                }
                finally
                {
                    _firmwareLock.Release();
                }
                _sync.Post(_ =>
                {
                    CurrentGpuMode = mode;
                    ErrorRaised?.Invoke($"GPU mode scheduled: {mode} — reboot to apply.");
                    StateChanged?.Invoke();
                }, null);
            }
            catch (Exception ex)
            {
                _sync.Post(_ => ErrorRaised?.Invoke($"GPU mode switch failed: {ex.Message}"), null);
            }
        });
    }

    // ---- misc actions ------------------------------------------------------

    public void SetKeepAlive(bool enabled)
    {
        Settings.KeepAlive = enabled;
        Settings.Save();
        StateChanged?.Invoke();
    }

    public void SetEcoPowerSave(bool enabled)
    {
        Settings.EcoPowerSave = enabled;
        Settings.Save();
        if (_current == PowerProfile.Eco)
            ApplyProfile(PowerProfile.Eco, silent: true);
        else
            StateChanged?.Invoke();
    }

    public void ToggleAutostart()
    {
        try
        {
            if (AutostartManager.IsEnabled())
                AutostartManager.Disable();
            else
                AutostartManager.Enable(WinForms.Application.ExecutablePath);
            _autostartEnabled = QueryAutostartSafe();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(_main, ex.Message, "VictusControl",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        StateChanged?.Invoke();
    }

    private static bool QueryAutostartSafe()
    {
        try
        {
            return AutostartManager.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    private void Cycle()
    {
        int now = Environment.TickCount;
        if (now - _lastCycleTick < CycleDebounceMs)
            return;
        _lastCycleTick = now;
        ApplyProfile(_current.Next());
    }

    private void UpdateTrayIcon()
    {
        _ecoItem.Checked = _current == PowerProfile.Eco;
        _balancedItem.Checked = _current == PowerProfile.Balanced;
        _perfItem.Checked = _current == PowerProfile.Performance;
        _icon.Icon = ProfileIcons.IconFor(_current);
        _icon.Text = $"VictusControl: {_current}{FanSuffix()}";
    }

    private string FanSuffix() => _fanMode switch
    {
        FanMode.Max => " [Max Fan]",
        FanMode.Curve => _curveAppliedPercent > 0 ? $" [Curve {_curveAppliedPercent}%]" : " [Curve]",
        FanMode.Manual => $" [Fan {_fanPercent}%]",
        _ => "",
    };

    private static string Label(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => "ECO mode",
        PowerProfile.Performance => "Performance mode",
        _ => "Balanced mode",
    };

    private static string FanLabel(FanMode mode, int percent) => mode switch
    {
        FanMode.Max => "Max Fan: ON (100%)",
        FanMode.Curve => "Fans: Smart Curve",
        FanMode.Manual => $"Fans: {percent}%",
        _ => "Fans: Auto (BIOS)",
    };

    public void Dispose()
    {
        IsExiting = true;
        _sensorTimer.Stop();
        _curveTimer.Stop();
        _keepAliveTimer.Stop();

        // Fail-safe: never leave manual low RPM locked if the user exits VictusControl
        if (_fanOverrideActive && HardwarePresent)
        {
            try
            {
                if (_firmwareLock.Wait(TimeSpan.FromSeconds(2)))
                {
                    try
                    {
                        using var client = new HpWmiClient();
                        new PowerController(client).SetMaxFan(false);
                    }
                    finally
                    {
                        _firmwareLock.Release();
                    }
                }
            }
            catch
            {
                // best effort on shutdown
            }
        }

        _hotkeys.Dispose();
        _icon.Dispose();
        _showSignal.Dispose();
        _firmwareLock.Dispose();
    }
}
