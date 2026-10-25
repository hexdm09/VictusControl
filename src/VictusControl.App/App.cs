using System.Windows;

namespace VictusControl.App;

// code-only WPF application (no ApplicationDefinition: Program.cs owns Main)
internal sealed class App : System.Windows.Application
{
    public AppEngine Engine { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Engine = new AppEngine();
        var window = new MainWindow(Engine);

        if (Engine.Settings.StartMinimized)
            window.ShowInTaskbar = false; // alive in tray only until opened
        else
            window.Show();

        MainWindow = window;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Engine.Dispose();
        base.OnExit(e);
    }
}

