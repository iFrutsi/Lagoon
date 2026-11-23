using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NotchApp.Services;

/// <summary>Weather from Open-Meteo (free, no account, no key). Only the city you type is sent.</summary>
public sealed class WeatherService
{
    public sealed record Now(double Temp, double High, double Low, int Code, string City);

    Now? _cached;
    DateTime _at;
    string? _city;

    public async Task<Now?> GetAsync(string city)
    {
        if (string.IsNullOrWhiteSpace(city)) return null;
        if (_cached != null && _city == city && DateTime.Now - _at < TimeSpan.FromMinutes(30)) return _cached;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var geo = JsonDocument.Parse(await http.GetStringAsync(
                $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(city)}"));
            if (!geo.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var place = res[0];
            double lat = place.GetProperty("latitude").GetDouble(), lon = place.GetProperty("longitude").GetDouble();
            string name = place.GetProperty("name").GetString() ?? city;

            var fc = JsonDocument.Parse(await http.GetStringAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat:0.###}&longitude={lon:0.###}" +
                "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1"));
            var cur = fc.RootElement.GetProperty("current");
            var daily = fc.RootElement.GetProperty("daily");
            _cached = new Now(cur.GetProperty("temperature_2m").GetDouble(),
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble(),
                cur.GetProperty("weather_code").GetInt32(), name);
            _city = city;
            _at = DateTime.Now;
            return _cached;
        }
        catch { return _cached; }
    }

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        >= 51 and <= 57 => "Drizzle",
        >= 61 and <= 67 or >= 80 and <= 82 => "Rain",
        >= 71 and <= 77 or 85 or 86 => "Snow",
        >= 95 => "Thunderstorm",
        _ => "—",
    };

    /// <summary>Segoe icon: sun for clear, cloud otherwise.</summary>
    public static string Glyph(int code) => code <= 1 ? "" : "";
}

public static class Battery
{
    /// <summary>(percent or null if no battery, charging).</summary>
    public static (int? Percent, bool Charging) Read()
    {
        if (!GetSystemPowerStatus(out var s)) return (null, false);
        int? pct = s.BatteryLifePercent == 255 || (s.BatteryFlag & 128) != 0 ? null : s.BatteryLifePercent;
        return (pct, s.ACLineStatus == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
}
