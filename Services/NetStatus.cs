using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace NotchApp.Services;

public enum NetKind { None, Wifi, WifiNoInternet, Ethernet, EthernetNoInternet }

/// <summary>
/// What the dock's network icon shows: Wi-Fi bars (0-4), Ethernet, or no internet.
/// Event-driven, nothing polls: Windows' Wi-Fi service tells us when the signal quality changes or
/// we connect / disconnect (WlanRegisterNotification), and NetworkStatusChanged covers internet
/// coming and going. Each event just re-reads the state once (after a short settle).
/// </summary>
public sealed class NetStatus : IDisposable
{
    public NetKind Kind { get; private set; } = NetKind.None;
    public int Bars { get; private set; } // 1-4 when on Wi-Fi
    public string? Name { get; private set; }
    public event Action? Changed;

    IntPtr _wlan;
    WlanCallback? _callback; // kept alive while registered
    readonly Timer _settle;
    bool _disposed;

    public NetStatus()
    {
        _settle = new Timer(_ => Read());
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out _wlan) == 0)
            {
                _callback = (_, _) => Poke();
                if (WlanRegisterNotification(_wlan, 0x08 /*ACM*/ | 0x10 /*MSM*/, false, _callback, IntPtr.Zero, IntPtr.Zero, out _) != 0)
                    _callback = null;
            }
            else _wlan = IntPtr.Zero; // no Wi-Fi adapter / service off: Ethernet + internet events still work
        }
        catch { _wlan = IntPtr.Zero; }
        NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        Poke();
    }

    void OnStatusChanged(object? sender) => Poke();

    /// <summary>Events come in bursts (connecting fires several): read once they settle.</summary>
    void Poke() { if (!_disposed) _settle.Change(600, Timeout.Infinite); }

    void Read()
    {
        if (_disposed) return;
        var (kind, bars, name) = Compute();
        if (kind == Kind && bars == Bars && name == Name) return;
        Kind = kind; Bars = bars; Name = name;
        Changed?.Invoke();
    }

    static (NetKind, int, string?) Compute()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            bool internet = p?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            // connected but no internet: the internet profile may be missing, so look at every profile
            if (p == null || !internet)
                p = NetworkInformation.GetConnectionProfiles()
                        .Where(c => c.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                        .OrderByDescending(c => c.IsWlanConnectionProfile)
                        .FirstOrDefault() ?? p;
            if (p == null || p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) return (NetKind.None, 0, null);
            internet = p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            if (p.IsWlanConnectionProfile)
            {
                // Windows' own 0-5 bars -> our 1-4 (5 and 4 both full, like the taskbar)
                int raw = p.GetSignalBars() ?? 5;
                int bars = raw >= 4 ? 4 : Math.Max(1, raw);
                var ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid();
                return (internet ? NetKind.Wifi : NetKind.WifiNoInternet, bars, string.IsNullOrWhiteSpace(ssid) ? p.ProfileName : ssid);
            }
            return (internet ? NetKind.Ethernet : NetKind.EthernetNoInternet, 0, p.ProfileName);
        }
        catch { return (NetKind.None, 0, null); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        if (_wlan != IntPtr.Zero)
        {
            try { WlanRegisterNotification(_wlan, 0, false, null, IntPtr.Zero, IntPtr.Zero, out _); } catch { }
            try { WlanCloseHandle(_wlan, IntPtr.Zero); } catch { }
            _wlan = IntPtr.Zero;
        }
        _settle.Dispose();
    }

    delegate void WlanCallback(IntPtr data, IntPtr context);
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanRegisterNotification(IntPtr handle, uint source, bool ignoreDuplicate,
        WlanCallback? callback, IntPtr context, IntPtr reserved, out uint previous);
}
