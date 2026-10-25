using System.IO;
using System.Windows;
using System.Windows.Threading;
using VictusControl.Core;
using WinForms = System.Windows.Forms;

namespace VictusControl.App;

public enum FanMode
{
    Auto,
    Max,
    Manual,
}

// one sensor read result (fresh client per poll; WMI has no cancellation)
public sealed record SensorSnapshot(
    bool Success, int CpuTemp, int GpuTemp, int Fan1Rpm, int Fan2Rpm, ThermalPolicy? Policy);

// application engine: owns the main window, tray icon, apply queue, hotkeys,
// keep-alive watchdog, capability probe and the sensor poll loop.
public sealed class AppEngine : IDisposable
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SensorInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SensorLockTimeout = TimeSpan.FromSeconds(3);
    private const int CycleDebounceMs = 500;

    // the EC exposes a single ACPI mailbox: every firmware command sequence
    // (profile apply, fan write, sensor poll, gpu mode) must take this lock,
    // otherwise a concurrent read can interleave with the apply write-readback
    private readonly SemaphoreSlim _firmwareLock = new(1, 1);

    // named signal: a second app instance asks this one to show its window
    private readonly EventWaitHandle _showSignal;
    private readonly Thread _showSignalThread;

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _ecoItem;
    private readonly WinForms.ToolStripMenuItem _balancedItem;
    private readonly WinForms.ToolStripMenuItem _perfItem;
    private readonly HotkeyManager _hotkeys;
    private readonly DispatcherTimer _keepAliveTimer;
    private readonly DispatcherTimer _sensorTimer;
    private readonly SynchronizationContext _sync;

    private MainWindow? _main;

    private PowerProfile _current = PowerProfile.Balanced;
    private FanMode _fanMode = FanMode.Auto;
    private int _fanPercent;
    private bool _autostartEnabled;
    private int _lastCycleTick;
    private int _sensorPollsInFlight;

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

    /// <summary>Board capabilities from the startup probe; None until probed.</summary>
    public BiosCapabilities Caps { get; private set; } = BiosCapabilities.None;

    /// <summary>False when hpqBIntM is absent (not an OMEN/Victus board).</summary>
    public bool HardwarePresent { get; private set; } = true;

    public string HardwareError { get; private set; } = "";

    /// <summary>Graphics routing last read from firmware (null = unknown).</summary>
    public GpuMode? CurrentGpuMode { get; private set; }

    public PowerProfile Current => _current;
    public FanMode FanModeState => _fanMode;
    public int FanManualPercent => _fanPercent;
    public bool AutostartEnabled => _autostartEnabled;
    public SensorSnapshot? LastSensors { get; private set; }

    /// <summary>Fired on the UI thread whenever UI-relevant state changed.</summary>
    public event Action? StateChanged;

    /// <summary>Fired on the UI thread with a non-fatal error message.</summary>
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
        var maxFanItem = new WinForms.ToolStripMenuItem("Max fan speed (100%)", null, (_, _) => SetFanMode(_fanMode == FanMode.Max ? FanMode.Auto : FanMode.Max, 0));
        var openItem = new WinForms.ToolStripMenuItem("Open VictusControl", null, (_, _) => ShowMain());
        var exitItem = new WinForms.ToolStripMenuItem("Exit", null, (_, _) => ExitApp());

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange([
            openItem,
            new WinForms.ToolStripSeparator(),
            _ecoItem, _balancedItem, _perfItem,
            new WinForms.ToolStripSeparator(),
            maxFanItem,
            new WinForms.ToolStripSeparator(),
            exitItem,
        ]);
        menu.Opening += (_, _) =>
        {
            UpdateTrayIcon();
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

        // 75s tick: prevent the EC from silently resetting the custom fan profile
        _keepAliveTimer = new DispatcherTimer { Interval = KeepAliveInterval };
        _keepAliveTimer.Tick += (_, _) =>
        {
            if (Settings.KeepAlive && HardwarePresent)
                ApplyProfile(_current, silent: true);
        };
        _keepAliveTimer.Start();

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
            if (_main is null || !_main.IsLoaded) // closed windows cannot be reused; hidden ones stay loaded
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
            _main?.Close(); // bypasses the hide-to-tray branch via IsExiting
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
        _sensorTimer.Start();
        PollSensors();
    }

    public void SuspendSensors() => _sensorTimer.Stop();

    private void PollSensors()
    {
        if (!HardwarePresent || Interlocked.CompareExchange(ref _sensorPollsInFlight, 1, 0) != 0)
            return;
        long ticket = Interlocked.Read(ref _seq);
        bool wantTemps = Caps.Temperatures;
        bool wantFans = Caps.FanLevels;
        bool wantGpuPower = Caps.GpuPower;
        bool wantGpuMode = Caps.GpuModeSwitch;
        ThermalPolicy? policy = Caps.Policy;
        Task.Run(() =>
        {
            SensorSnapshot snapshot;
            PowerProfile? actual = null;
            GpuMode? gpuMode = CurrentGpuMode;
            if (!_firmwareLock.Wait(SensorLockTimeout))
            {
                // apply or fan write in flight: skip this tick entirely
                _sync.Post(_ => Interlocked.Exchange(ref _sensorPollsInFlight, 0), null);
                return;
            }
            try
            {
                using var client = new HpWmiClient();
                var controller = new PowerController(client);
                var (cpu, gpu) = wantTemps ? controller.GetTemperatures() : (0, 0);
                var (f1, f2) = wantFans ? controller.GetFanRpm() : (0, 0);
                if (wantGpuPower)
                    actual = controller.GetGpuPower().ToProfile();
                if (wantGpuMode)
                {
                    try { gpuMode = controller.GetGpuMode(); } catch { }
                }
                snapshot = new SensorSnapshot(true, cpu, gpu, f1, f2, policy);
            }
            catch
            {
                snapshot = LastSensors is { } last
                    ? last with { Success = false }
                    : new SensorSnapshot(false, 0, 0, 0, 0, null);
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
                // profile changed from outside (firmware, Fn+P handled by us): follow it,
                // unless our own apply finished while this poll was in flight
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
            try
            {
                using var client = new HpWmiClient();
                PowerProfile actual = new PowerController(client).GetGpuPower().ToProfile();
                _sync.Post(_ =>
                {
                    // a newer Apply finished while we were reading: stale answer, ignore
                    if (Interlocked.Read(ref _seq) != ticket)
                        return;
                    _current = actual;
                    UpdateTrayIcon();
                    StateChanged?.Invoke();
                }, null);
            }
            catch
            {
                // wmi temporarily busy; keep cached state
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
                    // wmi has no cancellation; a hung call trips the timeout instead of wedging the queue
                    await Task.Run(() =>
                    {
                        if (!_firmwareLock.Wait(ApplyTimeout))
                            throw new TimeoutException("Firmware busy (lock timeout).");
                        try
                        {
                            using var client = new HpWmiClient();
                            var controller = new PowerController(client);
                            controller.ApplyProfile(next, Caps);
                            ReassertFanMode(controller);
                        }
                        finally
                        {
                            _firmwareLock.Release();
                        }
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
                    // a timed-out apply may still land in the firmware after a newer
                    // one: schedule one silent retry of the same profile to converge
                    lock (_queueLock)
                    {
                        _pending = next;
                        _pendingSilent = true;
                    }
                    ErrorRaised?.Invoke($"Apply timed out: {error.Message} РІР‚вЂќ retrying.");
                }
                else if (silent)
                {
                    // keep-alive failures must never modal-block a game session
                    ErrorRaised?.Invoke($"Keep-alive failed: {error.Message}");
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
                // request landed in the exit gap: pump again instead of losing it
                if (_pending is not null)
                    Pump();
            }
        }
    }

    // ---- fan control -------------------------------------------------------

    public void SetFanMode(FanMode mode, int percent)
    {
        if (!HardwarePresent || (!Caps.FanLevels && !Caps.MaxFan))
        {
            StateChanged?.Invoke(); // board without fan control: UI reflects it, no firmware call
            return;
        }
        if (mode == FanMode.Manual && percent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(percent), "Manual fan percent must be 1..100.");
        if (mode == FanMode.Manual && !Caps.FanLevels)
            mode = FanMode.Max; // percent unsupported: nearest supported request
        if (mode == FanMode.Max && !Caps.MaxFan)
        {
            // no 0x27 on this board: 100% through the percent subcommand instead,
            // so the UI promise "Max 100%" still reaches the fans
            mode = FanMode.Manual;
            percent = 100;
        }
        _fanMode = mode;
        _fanPercent = mode == FanMode.Manual ? percent : 0;
        UpdateTrayIcon();
        StateChanged?.Invoke();
        if (Settings.ShowOsd)
            OsdWindow.ShowToast(FanLabel(mode, _fanPercent), _current);
        QueueFanApply();
    }

    // fan requests are coalesced the same way as profiles: a slider drag fires
    // many events, only the last must reach the firmware, never in parallel
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
                    // same timeout discipline as the profile pump: a hung WMI call
                    // must not wedge the fan worker with _fanRunning stuck true
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

    // re-applies the current fan mode after a profile switch or keep-alive tick:
    // every ApplyProfile resets the curve back to firmware auto
    private void ReassertFanMode(PowerController controller)
    {
        if (_fanMode == FanMode.Auto && !_fanOverrideActive)
            return; // pure auto with nothing overridden: nothing to reassert
        if (_fanMode == FanMode.Max && Caps.MaxFan)
        {
            _fanOverrideActive = true;
            controller.SetMaxFan(true);
        }
        else if (_fanMode == FanMode.Manual && _fanPercent is > 0 && Caps.FanLevels)
        {
            _fanOverrideActive = true;
            controller.SetFanPercent(_fanPercent, _fanPercent);
        }
        else
        {
            _fanOverrideActive = false;
            controller.SetMaxFan(false);
        }
    }

    /// <summary>Schedules the BIOS graphics mode; takes effect after a reboot.</summary>
    public void RequestGpuMode(GpuMode mode)
    {
        Window? owner = _main is { IsLoaded: true } && _main.IsVisible ? _main : null;
        var answer = System.Windows.MessageBox.Show(owner,
            $"Switch graphics mode to {mode}?\nThis changes a BIOS setting and takes effect after the next reboot.",
            "VictusControl РІР‚вЂќ graphics mode",
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
                    ErrorRaised?.Invoke($"GPU mode scheduled: {mode} РІР‚вЂќ reboot to apply.");
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
            // a hung schtasks must never kill the app at startup
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
        FanMode.Manual => $"Fans: {percent}%",
        _ => "Fans: Auto",
    };

    public void Dispose()
    {
        IsExiting = true;
        _sensorTimer.Stop();
        _keepAliveTimer.Stop();
        _hotkeys.Dispose();
        _icon.Dispose();
        _showSignal.Dispose();
        _firmwareLock.Dispose();
    }
}


