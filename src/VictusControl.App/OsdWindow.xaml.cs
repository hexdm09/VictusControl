using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using VictusControl.Core;

namespace VictusControl.App;

// top-center non-activating overlay toast shown on profile/fan switch
public partial class OsdWindow : Window
{
    private const int DisplayMs = 1200;
    private static OsdWindow? _active;

    private readonly DispatcherTimer _closeTimer;

    private OsdWindow(string text, System.Windows.Media.Color accent)
    {
        InitializeComponent();
        Message.Text = text;
        AccentBar.Background = new System.Windows.Media.SolidColorBrush(accent);

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisplayMs) };
        _closeTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => _closeTimer.Start();
        Loaded += PositionSelf;
    }

    private void PositionSelf(object sender, RoutedEventArgs e)
    {
        // show on the screen the user is looking at, not blindly on the primary
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        Left = area.X + (area.Width - ActualWidth) / 2;
        Top = area.Y + 90 * (Graphics.FromHwnd(IntPtr.Zero).DpiX / 96.0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // never take focus and never appear in alt-tab: gamesafe overlay
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int GWL_EXSTYLE = -20;
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE, style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public static void ShowToast(string text, PowerProfile profile)
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() != true)
        {
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => ShowToast(text, profile));
            return;
        }

        OsdWindow form = new(text, AccentOf(profile));
        // guard by reference: the previous toast closes asynchronously and must
        // not clear the pointer to this newer one when its Closed fires
        form.Closed += (_, _) =>
        {
            if (ReferenceEquals(_active, form))
                _active = null;
        };
        _active?.Close();
        _active = form;
        form.Show();
    }

    private static System.Windows.Media.Color AccentOf(PowerProfile profile) => profile switch
    {
        PowerProfile.Eco => System.Windows.Media.Color.FromRgb(0x5E, 0xC8, 0x71),
        PowerProfile.Performance => System.Windows.Media.Color.FromRgb(0xFF, 0x6C, 0x5C),
        _ => System.Windows.Media.Color.FromRgb(0x4C, 0xC9, 0xF0),
    };

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int style);
}

