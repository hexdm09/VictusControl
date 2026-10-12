namespace VictusControl.App;

internal static class Program
{
    // machine-wide single-instance mutex
    private const string MutexName = @"Global\VictusControl";

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
            mutex.Dispose();
            return;
        }

        using (mutex)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
    }
}
