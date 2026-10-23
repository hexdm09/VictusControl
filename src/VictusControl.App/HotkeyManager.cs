using System.Management;
using System.Runtime.InteropServices;

namespace VictusControl.App;

// listens for hardware Fn+P / OMEN key via hpqBEvnt, Ctrl+Shift+P fallback
internal sealed class HotkeyManager : IDisposable
{
    private const uint EventFnP = 27; // 0x1B HPWMI_FN_P_HOTKEY
    private const uint EventOmenKey = 29; // 0x1D, eventData 8613

    public event Action? CycleRequested;

    private readonly SynchronizationContext? _sync;
    private ManagementEventWatcher? _wmiWatcher;
    private HotkeyWindow? _fallbackWindow;
    private bool _disposed;

    public HotkeyManager()
    {
        _sync = SynchronizationContext.Current;
    }

    public void Start()
    {
        StartWmiWatcher();
        StartFallbackHotkey();
    }

    private void StartWmiWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"root\wmi");
            scope.Connect();
            _wmiWatcher = new ManagementEventWatcher(scope, new WqlEventQuery("SELECT * FROM hpqBEvnt"));
            _wmiWatcher.EventArrived += OnWmiEvent;
            _wmiWatcher.Start();
        }
        catch
        {
            // HP WMI events unavailable; the RegisterHotKey fallback below still works.
        }
    }

    private void OnWmiEvent(object sender, EventArrivedEventArgs e)
    {
        try
        {
            object? raw = e.NewEvent?["eventId"];
            if (raw is null)
                return;
            uint id = Convert.ToUInt32(raw);
            if (id is EventFnP or EventOmenKey)
                RaiseCycle();
        }
        catch
        {
            // Ignore malformed events.
        }
    }

    private void StartFallbackHotkey()
    {
        try
        {
            _fallbackWindow = new HotkeyWindow();
            _fallbackWindow.Pressed += RaiseCycle;
        }
        catch
        {
            // Hotkey already taken or unavailable; WMI path may still work.
        }
    }

    private void RaiseCycle()
    {
        // the caller constructed us on the UI thread, so the context is always
        // present; if that invariant ever breaks we must NOT touch UI from here
        System.Diagnostics.Debug.Assert(_sync is not null, "HotkeyManager must be created on the UI thread");
        if (_sync is not null)
            _sync.Post(_ => CycleRequested?.Invoke(), null);
        else
            CycleRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_wmiWatcher is not null)
        {
            try
            {
                _wmiWatcher.EventArrived -= OnWmiEvent;
                _wmiWatcher.Stop();
            }
            catch
            {
                // Watcher never started; still dispose the fallback below.
            }
            _wmiWatcher.Dispose();
            _wmiWatcher = null;
        }
        _fallbackWindow?.Dispose();
        _fallbackWindow = null;
    }

    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;

        public event Action? Pressed;

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
            try
            {
                if (!RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_SHIFT, (uint)Keys.P))
                    throw new InvalidOperationException("RegisterHotKey failed.");
            }
            catch
            {
                // never leak the window we just created when hotkey registration fails
                DestroyHandle();
                throw;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
                Pressed?.Invoke();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, 1);
            DestroyHandle();
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
