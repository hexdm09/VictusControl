using VictusControl.Core;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  VictusControl --get                      Show active profile (from GPU cTGP/PPAB)");
    Console.WriteLine("  VictusControl --set eco|balanced|perf   Apply a profile");
    Console.WriteLine("  VictusControl --cycle                    Eco -> Balanced -> Performance -> Eco");
    Console.WriteLine("  VictusControl --system                   Show thermal policy version");
    Console.WriteLine("  VictusControl --gpu                      Show raw GPU power state");
    Console.WriteLine("  VictusControl --autostart on|off [exe]   Task Scheduler logon task (HighestAvailable)");
    Console.WriteLine("Must run elevated (Administrator) for WMI BIOS access.");
    return 0;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "--system":
        {
            using var client = new HpWmiClient();
            Console.WriteLine($"ThermalPolicy: {new PowerController(client).GetThermalPolicy()}");
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
            Console.WriteLine($"Profile: {CurrentProfileName()}");
            break;
        case "--set" when args.Length > 1:
            ApplyProfile(Parse(args[1]));
            break;
        case "--cycle":
        {
            PowerProfile next = CurrentProfile() switch
            {
                PowerProfile.Eco => PowerProfile.Balanced,
                PowerProfile.Balanced => PowerProfile.Performance,
                _ => PowerProfile.Eco,
            };
            ApplyProfile(next);
            break;
        }
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

static PowerProfile CurrentProfile()
{
    using var client = new HpWmiClient();
    GpuPowerData gpu = new PowerController(client).GetGpuPower();
    return (gpu.CustomTgp, gpu.Ppab) switch
    {
        (0x01, 0x01) => PowerProfile.Performance,
        (_, 0x01) => PowerProfile.Balanced,
        _ => PowerProfile.Eco,
    };
}

static string CurrentProfileName()
{
    using var client = new HpWmiClient();
    GpuPowerData gpu = new PowerController(client).GetGpuPower();
    string mode = (gpu.CustomTgp, gpu.Ppab) switch
    {
        (0x01, 0x01) => "Performance",
        (_, 0x01) => "Balanced",
        _ => "Eco",
    };
    return $"{mode} (cTGP={gpu.CustomTgp} PPAB={gpu.Ppab})";
}

static void ApplyProfile(PowerProfile profile)
{
    using var client = new HpWmiClient();
    new PowerController(client).ApplyProfile(profile);
    Console.WriteLine($"Applied: {profile}");
}
