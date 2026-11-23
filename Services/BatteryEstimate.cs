using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// "Full in 1h 05m" / "4h 20m left", read once when asked (nothing runs in the background).
/// On battery: Windows' own smoothed estimate. Charging: Windows gives no time-to-full, so it is
/// worked out from the charge rate right now: (full capacity - remaining) / rate.
/// </summary>
public static class BatteryEstimate
{
    /// <summary>Short text for the battery's state, or null if there is nothing to say (no battery,
    /// or Windows hasn't measured a rate yet: right after plugging in / unplugging).</summary>
    public static string? Text()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent > 100) return null;
        bool plugged = s.ACLineStatus == 1;
        if (plugged)
        {
            if (s.BatteryLifePercent >= 100) return "Fully charged";
            if (MinutesToFull() is { } m) return "Full in " + Span(m);
            return (s.BatteryFlag & 8) != 0 ? "Estimating…" : "Plugged in, not charging";
        }
        if (s.BatteryLifeTime > 0) return Span(s.BatteryLifeTime / 60) + " left";
        if (MinutesLeftFromRate() is { } left) return Span(left) + " left";
        return "Estimating…";
    }

    /// <summary>Minutes to a full charge at the rate right now; null if not charging or unknown.</summary>
    public static int? MinutesToFull()
    {
        try
        {
            var r = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (r.ChargeRateInMilliwatts is not int rate || rate <= 0) return null;
            if (r.FullChargeCapacityInMilliwattHours is not int full || r.RemainingCapacityInMilliwattHours is not int left) return null;
            if (full <= left) return null;
            return Math.Max(1, (int)Math.Round((full - left) * 60.0 / rate));
        }
        catch { return null; }
    }

    static int? MinutesLeftFromRate()
    {
        try
        {
            var r = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            if (r.ChargeRateInMilliwatts is not int rate || rate >= 0) return null;
            if (r.RemainingCapacityInMilliwattHours is not int left || left <= 0) return null;
            return Math.Max(1, (int)Math.Round(left * 60.0 / -rate));
        }
        catch { return null; }
    }

    public static string Span(int minutes) =>
        minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
