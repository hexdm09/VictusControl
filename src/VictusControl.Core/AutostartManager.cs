using System.Diagnostics;

namespace VictusControl.Core;

/// <summary>
/// Autostart via Windows Task Scheduler (logon trigger, HighestAvailable).
/// Uses schtasks.exe so no extra dependencies are needed.
/// Elevated rights are required to register the task (the tray app already runs elevated).
/// </summary>
public static class AutostartManager
{
    public const string TaskName = "VictusControl";

    public static bool IsEnabled()
    {
        using var process = Run("schtasks", $"/Query /TN \"{TaskName}\"");
        process.WaitForExit(10_000);
        return process.ExitCode == 0;
    }

    public static void Enable(string exePath)
    {
        using var process = Run("schtasks",
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        process.WaitForExit(15_000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Failed to register the autostart task (schtasks exit code " + process.ExitCode + ").");
    }

    public static void Disable()
    {
        using var process = Run("schtasks", $"/Delete /TN \"{TaskName}\" /F");
        process.WaitForExit(15_000);
    }

    private static Process Run(string file, string args)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException($"Failed to start {file}.");
    }
}
