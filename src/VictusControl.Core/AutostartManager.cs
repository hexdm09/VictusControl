using System.Diagnostics;

namespace VictusControl.Core;

// registers elevated task on logon via schtasks.exe
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
        // exit code 1 means task was already absent
        if (process.ExitCode is not (0 or 1))
            throw new InvalidOperationException($"Failed to remove the autostart task (schtasks exit code {process.ExitCode}).");
    }

    // rejects relative paths and embedded quotes
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
