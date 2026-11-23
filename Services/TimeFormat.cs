using System.Globalization;

namespace NotchApp.Services;

/// <summary>Times follow your Windows "short time" setting (12-hour "10:28 PM" or 24-hour "22:28") everywhere.</summary>
public static class TimeFormat
{
    static string Pattern => CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
    public static bool TwelveHour => Pattern.Contains('t');

    /// <summary>"10:28 PM" or "22:28".</summary>
    public static string Time(DateTime t) => t.ToString(Pattern, CultureInfo.CurrentCulture);

    /// <summary>The digits alone ("10:28" / "22:28") and the AM/PM part ("PM" / "").</summary>
    public static (string Digits, string AmPm) Split(DateTime t)
    {
        if (!TwelveHour) return (t.ToString(Pattern.Replace("tt", "").Replace("t", "").Trim(), CultureInfo.CurrentCulture), "");
        var digits = Pattern.Replace("tt", "").Replace("t", "").Trim();
        return (t.ToString(digits, CultureInfo.CurrentCulture), t.ToString("tt", CultureInfo.CurrentCulture));
    }
}
