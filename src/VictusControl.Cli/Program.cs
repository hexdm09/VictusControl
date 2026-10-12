using VictusControl.Core;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine("Usage: VictusControl --get | --set eco|balanced|perf | --system | --gpu");
    Console.WriteLine("Must run elevated (Administrator) for WMI BIOS access.");
    return 0;
}

try
{
    using var client = new HpWmiClient();
    var controller = new PowerController(client);

    switch (args[0].ToLowerInvariant())
    {
        case "--system":
        {
            ThermalPolicy policy = controller.GetThermalPolicy();
            Console.WriteLine($"ThermalPolicy: {policy}");
            break;
        }
        case "--gpu":
        {
            GpuPowerData gpu = controller.GetGpuPower();
            Console.WriteLine($"cTGP={gpu.CustomTgp} PPAB={gpu.Ppab} DState={gpu.DState} Temp={gpu.PeakTemperature}");
            break;
        }
        case "--get":
        {
            GpuPowerData gpu = controller.GetGpuPower();
            string mode = (gpu.CustomTgp, gpu.Ppab) switch
            {
                (0x01, 0x01) => "Performance",
                (_, 0x01) => "Balanced",
                _ => "Eco",
            };
            Console.WriteLine($"Profile: {mode} (cTGP={gpu.CustomTgp} PPAB={gpu.Ppab})");
            break;
        }
        case "--set" when args.Length > 1:
        {
            PowerProfile profile = args[1].ToLowerInvariant() switch
            {
                "eco" or "cool" => PowerProfile.Eco,
                "balanced" or "default" or "bal" => PowerProfile.Balanced,
                "perf" or "performance" => PowerProfile.Performance,
                _ => throw new ArgumentException($"Unknown profile '{args[1]}'. Use eco|balanced|perf."),
            };
            controller.ApplyProfile(profile);
            Console.WriteLine($"Applied: {profile}");
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
