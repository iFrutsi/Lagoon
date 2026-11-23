using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Windows battery saver (energy saver) on/off for the Control Center tile.
/// Windows turns it on by itself when the charge drops to a threshold ("Charge level", 20% here).
/// Switching it on now = raising that threshold to 100%; switching it off = putting the original
/// threshold back (remembered in settings, so your automatic 20% keeps working). Reading the state
/// is one cheap kernel call; nothing runs in the background.
/// </summary>
public static class BatterySaver
{
    static readonly Guid Sub = new("de830923-a562-41af-a086-e3a2c6bad2da");      // SUB_ENERGYSAVER
    static readonly Guid Threshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4"); // ESBATTTHRESHOLD (%)

    /// <summary>null = this PC has no battery.
    /// On when Lagoon switched it on (it remembered your own level), or Windows reports it on. Newer
    /// Windows 11 ("energy saver") no longer reports it through the old flag, so Lagoon's own record
    /// is what makes the tile show On and switch it back off.</summary>
    public static bool? IsOn(AppSettings settings)
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 /*no battery*/) return null;
        return settings.SaverThresholdRestoreTo != null || s.SystemStatusFlag == 1;
    }

    public static bool PluggedIn => GetSystemPowerStatus(out var s) && s.ACLineStatus == 1;

    public static void Set(bool on, AppSettings settings)
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return;
            var scheme = Marshal.PtrToStructure<Guid>(p);
            LocalFree(p);
            var sub = Sub; var set = Threshold;
            if (on)
            {
                if (settings.SaverThresholdRestoreTo == null) // remember the user's own thresholds once
                {
                    PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint dc);
                    PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out uint ac);
                    settings.SaverThresholdRestoreTo = (int)dc;
                    settings.SaverThresholdRestoreToAC = (int)ac;
                    settings.Save();
                }
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 100);
                PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, 100);
            }
            else
            {
                PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)(settings.SaverThresholdRestoreTo ?? 20));
                PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, (uint)(settings.SaverThresholdRestoreToAC ?? 0));
                settings.SaverThresholdRestoreTo = null;
                settings.SaverThresholdRestoreToAC = null;
                settings.Save();
            }
            PowerSetActiveScheme(IntPtr.Zero, ref scheme); // apply now
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
}
