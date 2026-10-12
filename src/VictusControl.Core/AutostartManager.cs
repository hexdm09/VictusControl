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

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ChangeTimeout = TimeSpan.FromSeconds(15);

    private static string SchTasksPath => Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    public static bool IsEnabled()
    {
        using var process = Run($"/Query /TN \"{TaskName}\"");
        if (!process.WaitForExit(QueryTimeout))
        {
            Kill(process);
            throw new TimeoutException("schtasks /Query timed out.");
        }
        return process.ExitCode == 0;
    }

    public static void Enable(string exePath)
    {
        string full = ValidateExePath(exePath);
        using var process = Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{full}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        if (!process.WaitForExit(ChangeTimeout))
        {
            Kill(process);
            throw new TimeoutException("schtasks /Create timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to register the autostart task (schtasks exit code {process.ExitCode}).");
    }

    public static void Disable()
    {
        using var process = Run($"/Delete /TN \"{TaskName}\" /F");
        if (!process.WaitForExit(ChangeTimeout))
        {
            Kill(process);
            throw new TimeoutException("schtasks /Delete timed out.");
        }
        // Exit code 1 = task did not exist: already in the desired state.
        if (process.ExitCode is not (0 or 1))
            throw new InvalidOperationException($"Failed to remove the autostart task (schtasks exit code {process.ExitCode}).");
    }

    /// <summary>
    /// Rejects quote-breaking and relative paths: a crafted /TR tail would be
    /// parsed as extra schtasks switches, and a relative path is a hijack risk at logon.
    /// </summary>
    internal static string ValidateExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is empty.", nameof(exePath));
        if (exePath.Contains('"'))
            throw new ArgumentException("Executable path must not contain quotes.", nameof(exePath));
        string full = Path.GetFullPath(exePath);
        if (!Path.IsPathRooted(full))
            throw new ArgumentException("Executable path must be absolute.", nameof(exePath));
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Executable path must point to an .exe file.", nameof(exePath));
        if (!File.Exists(full))
            throw new FileNotFoundException("Executable not found.", full);
        return full;
    }

    private static Process Run(string args)
    {
        return Process.Start(new ProcessStartInfo
        {
            FileName = SchTasksPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException($"Failed to start {SchTasksPath}.");
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort: the process may already be gone.
        }
    }
}
