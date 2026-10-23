using System.Diagnostics;
using VictusControl.Core;

if (args.Length == 0)
{
    try
    {
        string trayExe = Path.Combine(AppContext.BaseDirectory, "VictusControlTray.exe");
        if (File.Exists(trayExe))
        {
            Process.Start(new ProcessStartInfo(trayExe) { UseShellExecute = true });
            return 0;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        return 1;
    }
}

bool help = args.Length == 0
    || args[0].Equals("-h", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase)
    || args[0].Equals("help", StringComparison.OrdinalIgnoreCase);
if (help)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  VictusControl --get                      Show active profile (from GPU cTGP/PPAB)");
    Console.WriteLine("  VictusControl --set eco|balanced|perf   Apply a profile");
    Console.WriteLine("  VictusControl --cycle                    Eco -> Balanced -> Performance -> Eco");
    Console.WriteLine("  VictusControl --system                   Show thermal policy version");
    Console.WriteLine("  VictusControl --gpu                      Show raw GPU power state");
    Console.WriteLine("  VictusControl --fan max|auto|<1-100>    Set fans to 100%, auto curve, or a manual percent");
    Console.WriteLine("  VictusControl --fans                     Show current fan speeds (RPM) and temperatures");
    Console.WriteLine("  VictusControl --temp                     Show CPU and GPU temperatures");
    Console.WriteLine("  VictusControl --gpumode [hybrid|discrete]  Show or schedule the graphics mode (reboot to apply)");
    Console.WriteLine("  VictusControl --info                     Probe this board and report supported features");
    Console.WriteLine("  VictusControl --autostart on|off [exe]   Task Scheduler logon task (HighestAvailable)");
    Console.WriteLine("Must run elevated (Administrator) for WMI BIOS access.");
    return 0;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "--fan" when args.Length > 1:
        {
            string value = args[1];
            if (int.TryParse(value, out int percent))
            {
                if (percent is < 1 or > 100)
                    throw new ArgumentException("Manual fan percent must be 1..100 (or use max|auto).");
                using var client = new HpWmiClient();
                new PowerController(client).SetFanPercent(percent, percent);
                Console.WriteLine($"Fans: {percent}% (manual)");
                break;
            }
            bool max = value.Equals("max", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("on", StringComparison.OrdinalIgnoreCase);
            bool auto = value.Equals("auto", StringComparison.OrdinalIgnoreCase)
                     || value.Equals("off", StringComparison.OrdinalIgnoreCase);
            if (!max && !auto)
                throw new ArgumentException($"Unknown fan value '{value}'. Use max|auto|<1-100>.");
            using var client2 = new HpWmiClient();
            new PowerController(client2).SetMaxFan(max);
            Console.WriteLine(max ? "Max Fan: ON (100% speed)" : "Max Fan: OFF (Auto curve)");
            break;
        }
        case "--fans":
        {
            using var client = new HpWmiClient();
            var controller = new PowerController(client);
            var (f1, f2) = controller.GetFanRpm();
            var (cpu, gpu) = controller.GetTemperatures();
            Console.WriteLine($"Fan 1: {f1} RPM | Fan 2: {f2} RPM | CPU: {cpu}°C | GPU: {gpu}°C");
            break;
        }
        case "--temp":
        {
            using var client = new HpWmiClient();
            var (cpu, gpu) = new PowerController(client).GetTemperatures();
            Console.WriteLine($"CPU: {cpu}°C | GPU: {gpu}°C");
            break;
        }
        case "--system":
        {
            using var client = new HpWmiClient();
            Console.WriteLine($"ThermalPolicy: {new PowerController(client).GetThermalPolicy()}");
            break;
        }
        case "--info":
        {
            using var client = new HpWmiClient();
            var controller = new PowerController(client);
            BiosCapabilities caps = CapabilityProbe.Probe(client);
            Console.WriteLine($"Thermal policy : {caps.Policy?.ToString() ?? "unknown (Legacy fallback)"}");
            Console.WriteLine($"GPU power (cTGP/PPAB): {(caps.GpuPower ? "supported" : "NOT supported")}");
            Console.WriteLine($"Fan levels (RPM/percent): {(caps.FanLevels ? "supported" : "NOT supported")}");
            Console.WriteLine($"Temperatures  : {(caps.Temperatures ? "supported" : "NOT supported")}");
            Console.WriteLine($"Max fan mode  : {(caps.MaxFan ? "supported" : "NOT supported")}");
            Console.WriteLine($"Fan count     : {(caps.FanCount ? "supported" : "NOT supported")}");
            Console.WriteLine($"GPU mode switch: {(caps.GpuModeSwitch ? "supported" : "NOT supported")}");
            if (caps.GpuModeSwitch)
            {
                try { Console.WriteLine($"Current GPU mode: {controller.GetGpuMode().Describe()}"); }
                catch (Exception ex) { Console.WriteLine($"Current GPU mode: read failed ({ex.Message})"); }
            }
            break;
        }
        case "--gpumode" when args.Length == 1:
        {
            using var client = new HpWmiClient();
            Console.WriteLine($"GPU mode: {new PowerController(client).GetGpuMode().Describe()}");
            break;
        }
        case "--gpumode" when args.Length > 1:
        {
            GpuMode? mode = GpuModeExtensions.Parse(args[1]);
            if (mode is null)
                throw new ArgumentException($"Unknown GPU mode '{args[1]}'. Use hybrid|discrete.");
            using var client = new HpWmiClient();
            new PowerController(client).SetGpuMode(mode.Value);
            Console.WriteLine($"GPU mode scheduled: {mode.Value.Describe()}");
            Console.WriteLine("Reboot required for the change to take effect.");
            break;
        }
        case "--gpu":
        {
            using var client = new HpWmiClient();
            GpuPowerData gpu = new PowerController(client).GetGpuPower();
            Console.WriteLine($"cTGP={gpu.CustomTgp} PPAB={gpu.Ppab} DState={gpu.DState} Temp={gpu.PeakTemperature}");
            break;
        }
        case "--get":
            Console.WriteLine($"Profile: {Describe(ReadGpu())}");
            break;
        case "--set" when args.Length > 1:
            ApplyProfile(Parse(args[1]));
            break;
        case "--cycle":
            ApplyProfile(CurrentProfile().Next());
            break;
        case "--autostart" when args.Length > 1:
        {
            if (args[1].Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                string exe = args.Length > 2
                    ? args[2]
                    : Path.Combine(AppContext.BaseDirectory, "VictusControlTray.exe");
                AutostartManager.Enable(exe);
                Console.WriteLine($"Autostart enabled: {exe}");
            }
            else if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                AutostartManager.Disable();
                Console.WriteLine("Autostart disabled.");
            }
            else
            {
                throw new ArgumentException("Use --autostart on|off.");
            }
            break;
        }
        default:
            Console.Error.WriteLine($"Unknown args: {string.Join(' ', args)}");
            return 2;
    }

    return 0;
}
catch (ArgumentException ex)
{
    // argument problems are the caller's fault: the documented "invalid arguments" code
    Console.Error.WriteLine($"Invalid arguments: {ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static PowerProfile Parse(string name) => name.ToLowerInvariant() switch
{
    "eco" or "cool" => PowerProfile.Eco,
    "balanced" or "default" or "bal" => PowerProfile.Balanced,
    "perf" or "performance" => PowerProfile.Performance,
    _ => throw new ArgumentException($"Unknown profile '{name}'. Use eco|balanced|perf."),
};

static GpuPowerData ReadGpu()
{
    using var client = new HpWmiClient();
    return new PowerController(client).GetGpuPower();
}

static PowerProfile CurrentProfile() => ReadGpu().ToProfile();

static string Describe(GpuPowerData gpu) =>
    $"{gpu.ToProfile()} (cTGP={gpu.CustomTgp} PPAB={gpu.Ppab})";

static void ApplyProfile(PowerProfile profile)
{
    using var client = new HpWmiClient();
    var controller = new PowerController(client);
    controller.ApplyProfile(profile);
    Console.WriteLine($"Applied: {profile}");
}
