using System.Management;
using System.Runtime.InteropServices;
using VictusControl.Core;

namespace VictusControl.App;

// OS-level battery telemetry and CPU boost / EPP tuning for true Eco battery life on Windows.
// Avoids touching NVIDIA WMI/NVAPI so a sleeping RTX dGPU stays in D3cold (0.0 W).
internal static class WindowsPowerHelper
{
    // Processor power management subgroup: 54533251-82be-4824-96c1-47b60b740d00
    private static readonly Guid SubgroupProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    // Processor performance boost mode: be337238-0d82-4146-a960-4f3749d470c7
    // 0 = Disabled, 2 = Aggressive, 3 = EfficientEnabled
    private static readonly Guid SettingPerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    // Energy Performance Preference (EPP): 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 (0..100)
    private static readonly Guid SettingEpp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");

    public static (bool OnBattery, int BatteryPercent, double? BatteryWatts) ReadBatteryState()
    {
        bool onBattery = false;
        int percent = -1;
        if (GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps))
        {
            onBattery = sps.ACLineStatus == 0;
            if (sps.BatteryLifePercent <= 100)
                percent = sps.BatteryLifePercent;
        }

        double? watts = QueryBatteryWattsSafe();
        return (onBattery, percent, watts);
    }

    public static void ApplyCpuPowerPolicy(PowerProfile profile, bool ecoPowerSaveEnabled)
    {
        uint boostMode = (profile, ecoPowerSaveEnabled) switch
        {
            (PowerProfile.Eco, true) => 0u,       // Disabled: stops 5.1 GHz / 1.4V spikes on Ryzen/Core
            (PowerProfile.Eco, false) => 3u,      // EfficientEnabled
            (PowerProfile.Balanced, _) => 3u,     // EfficientEnabled
            _ => 2u,                              // Aggressive / full boost
        };

        uint epp = profile switch
        {
            PowerProfile.Eco => 85u,              // Strong energy saving preference
            PowerProfile.Balanced => 50u,         // Balanced EPP
            _ => 0u,                              // Maximum performance EPP
        };

        IntPtr schemePtr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out schemePtr) != 0 || schemePtr == IntPtr.Zero)
                return;

            Guid scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            Guid subgroup = SubgroupProcessor;
            Guid boostGuid = SettingPerfBoostMode;
            Guid eppGuid = SettingEpp;

            _ = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref boostGuid, boostMode);
            _ = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref boostGuid, boostMode);
            _ = PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref eppGuid, epp);
            _ = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref eppGuid, profile == PowerProfile.Eco ? 70u : epp);
            _ = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        }
        catch
        {
            // Locked enterprise group policy or unsupported SKU: keep firmware profile working
        }
        finally
        {
            if (schemePtr != IntPtr.Zero)
                LocalFree(schemePtr);
        }
    }

    private static double? QueryBatteryWattsSafe()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT Discharging, Charging, DischargeRate, ChargeRate FROM BatteryStatus WHERE Active = TRUE");
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    bool discharging = obj["Discharging"] is bool d && d;
                    bool charging = obj["Charging"] is bool c && c;
                    if (discharging && obj["DischargeRate"] is uint disMw && disMw is > 0 and < 300_000)
                        return -Math.Round(disMw / 1000.0, 1);
                    if (charging && obj["ChargeRate"] is uint chgMw && chgMw is > 0 and < 300_000)
                        return Math.Round(chgMw / 1000.0, 1);
                }
            }
        }
        catch
        {
            // Desktop PC or battery WMI absent
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS lpSystemPowerStatus);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(
        IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);
}
