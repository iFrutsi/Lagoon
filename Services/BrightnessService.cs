using System.Management;

namespace NotchApp.Services;

/// <summary>Built-in display brightness through WMI (laptops and some all-in-ones).</summary>
public sealed class BrightnessService
{
    readonly object _lock = new();
    int _pending = -1;
    bool _running;

    int? _cached;
    DateTime _cachedAt;
    bool _probed;

    /// <summary>WMI queries are slow, so the value is cached for 30 s between hovers.</summary>
    public async Task<int?> GetAsync()
    {
        if (_probed && (_cached == null || DateTime.Now - _cachedAt < TimeSpan.FromSeconds(30)))
            return _cached;
        _cached = await Task.Run<int?>(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
                foreach (ManagementObject o in searcher.Get())
                    return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return null;
        });
        _probed = true;
        _cachedAt = DateTime.Now;
        return _cached;
    }

    /// <summary>Sets brightness; rapid slider moves collapse into the latest value.</summary>
    public void Set(int level)
    {
        lock (_lock)
        {
            _pending = Math.Clamp(level, 0, 100);
            _cached = _pending;
            _cachedAt = DateTime.Now;
            if (_running) return;
            _running = true;
        }
        Task.Run(Worker);
    }

    void Worker()
    {
        while (true)
        {
            int level;
            lock (_lock)
            {
                level = _pending;
                _pending = -1;
                if (level < 0) { _running = false; return; }
            }
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods");
                foreach (ManagementObject o in searcher.Get())
                    o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
            }
            catch { }
        }
    }
}
