namespace VictusControl.App;

internal static class Program
{
    // Global\ = one instance per machine. The app is admin-only, so a
    // low-privilege pre-created mutex (squat attack) is out of scope.
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
            // Previous owner died without releasing; we acquired it.
            owned = true;
        }

        if (!owned)
        {
            mutex.Dispose();
            return; // Another instance is already running.
        }

        using (mutex)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new TrayContext());
        }
    }
}
