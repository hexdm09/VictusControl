using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace VictusControl.App;

internal static class Program
{
    // machine-wide single-instance mutex
    private const string MutexName = @"Global\VictusControl";
    // set by a second launch to ask the running instance to show its window
    private const string ShowSignalName = @"Local\VictusControlShowWindow";

    [STAThread]
    private static void Main()
    {
        Mutex mutex = new(false, MutexName, out _);
        bool owned;
        try
        {
            owned = mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        if (!owned)
        {
            ActivateExistingWindow();
            mutex.Dispose();
            return;
        }

        using (mutex)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject.ToString() ?? "");
            Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.ThreadException += (_, e) => WriteCrashLog(e.Exception.ToString());

            try
            {
                new App().Run();
            }
            catch (Exception ex)
            {
                WriteCrashLog(ex.ToString());
                System.Windows.Forms.MessageBox.Show(ex.Message, "VictusControl — fatal error",
                    System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
    }

    // second launch: signal the running instance (works even when its window is
    // hidden in the tray, where MainWindowHandle is 0); Win32 activation stays
    // as a fallback for older builds
    private static void ActivateExistingWindow()
    {
        try
        {
            using EventWaitHandle signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
            return;
        }
        catch
        {
            // first instance not ready yet or from an older build: fall through
        }

        foreach (Process process in Process.GetProcessesByName("VictusControlTray"))
        {
            if (process.Id == Environment.ProcessId)
                continue;
            IntPtr handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
                continue;
            ShowWindow(handle, SW_RESTORE);
            SetForegroundWindow(handle);
            break;
        }
    }

    // append (not overwrite): concurrent handlers and repeated crashes keep history;
    // %LocalAppData% stays writable even when the exe directory is not
    private static void WriteCrashLog(string text)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusControl");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the crash path
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private const int SW_RESTORE = 9;
}
