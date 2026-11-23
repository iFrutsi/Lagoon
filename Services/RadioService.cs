using Windows.Devices.Radios;

namespace NotchApp.Services;

/// <summary>Wi-Fi and Bluetooth radio switches.</summary>
public sealed class RadioService
{
    Radio? _wifi, _bluetooth;

    public async Task InitAsync()
    {
        try
        {
            await Radio.RequestAccessAsync();
            foreach (var r in await Radio.GetRadiosAsync())
            {
                if (r.Kind == RadioKind.WiFi) _wifi ??= r;
                else if (r.Kind == RadioKind.Bluetooth) _bluetooth ??= r;
            }
        }
        catch { }
    }

    public bool? WifiOn => _wifi == null ? null : _wifi.State == RadioState.On;
    public bool? BluetoothOn => _bluetooth == null ? null : _bluetooth.State == RadioState.On;

    public async Task SetAsync(RadioKind kind, bool on)
    {
        var radio = kind == RadioKind.WiFi ? _wifi : _bluetooth;
        if (radio == null) return;
        try { await radio.SetStateAsync(on ? RadioState.On : RadioState.Off); }
        catch { }
    }
}
