using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

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
        ApplyBatteryPolicy();
    }

    public static void Disable()
    {
        // exit code 1 from /Delete is ambiguous (failure or already absent);
        // asking first turns "missing" into a clean no-op
        if (!IsEnabled())
            return;
        using var process = Run($"/Delete /TN \"{TaskName}\" /F");
        if (!process.WaitForExit(ChangeTimeout))
        {
            Kill(process);
            throw new TimeoutException("schtasks /Delete timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to remove the autostart task (schtasks exit code {process.ExitCode}).");
    }

    // rejects relative paths and embedded quotes
    internal static string ValidateExePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            throw new ArgumentException("Executable path is empty.", nameof(exePath));
        if (exePath.Contains('"'))
            throw new ArgumentException("Executable path must not contain quotes.", nameof(exePath));
        if (!Path.IsPathRooted(exePath))
            throw new ArgumentException("Executable path must be absolute.", nameof(exePath));
        string full = Path.GetFullPath(exePath);
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Executable path must point to an .exe file.", nameof(exePath));
        if (!File.Exists(full))
            throw new FileNotFoundException("Executable not found.", full);
        return full;
    }

    // schtasks has no switch for battery policy, so patch the exported xml:
    // a laptop booting on battery would otherwise skip the logon task.
    // parsed with XDocument instead of string Replace: a formatting change in
    // a future schtasks would silently turn the patch into a no-op.
    private static void ApplyBatteryPolicy()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XDocument doc;
        try
        {
            doc = XDocument.Parse(QueryXml());
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("Task xml could not be parsed for the battery patch.");
        }

        XElement? settings = doc.Root?.Element(ns + "Settings");
        bool changed = false;
        foreach (string elementName in new[] { "DisallowStartIfOnBatteries", "StopIfGoingOnBatteries" })
        {
            XElement? element = settings?.Element(ns + elementName);
            if (element is not null && !string.Equals(element.Value, "false", StringComparison.OrdinalIgnoreCase))
            {
                element.Value = "false";
                changed = true;
            }
        }
        if (!changed)
            return;

        // random name: predictable %TEMP% file under an elevated process is a squat risk
        string file = Path.Combine(Path.GetTempPath(), $"VictusControl_{Path.GetRandomFileName()}.xml");
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        {
            doc.Save(stream);
        }
        try
        {
            using var process = Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            if (!process.WaitForExit(ChangeTimeout))
            {
                Kill(process);
                throw new TimeoutException("schtasks /Create /XML timed out.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Failed to apply battery policy (schtasks exit code {process.ExitCode}).");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }

        // verify the re-imported task really carries the patched values
        bool disallow = ReadBatteryFlag(ns + "DisallowStartIfOnBatteries");
        bool stop = ReadBatteryFlag(ns + "StopIfGoingOnBatteries");
        if (disallow || stop)
            throw new InvalidOperationException("Battery policy verification failed after re-import.");
    }

    private static bool ReadBatteryFlag(XName name)
    {
        try
        {
            XDocument doc = XDocument.Parse(QueryXml());
            XNamespace ns = name.Namespace;
            string? value = doc.Root?.Element(ns + "Settings")?.Element(name)?.Value;
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true; // unreadable == unverifiable == report failure
        }
    }

    private static string QueryXml()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = SchTasksPath,
            Arguments = $"/Query /TN \"{TaskName}\" /XML",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException($"Failed to start {SchTasksPath}.");

        // drain the pipe asynchronously: a hung schtasks that never writes
        // must still hit the timeout, not block ReadToEnd forever
        Task<string> readTask = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(QueryTimeout) || !readTask.Wait(QueryTimeout))
        {
            Kill(process);
            throw new TimeoutException("schtasks /Query /XML timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Failed to export task xml (schtasks exit code {process.ExitCode}).");
        return readTask.Result;
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
