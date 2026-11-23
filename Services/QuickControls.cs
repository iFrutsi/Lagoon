namespace NotchApp.Services;

/// <summary>
/// The quick settings (Wi-Fi, Bluetooth, sound, mic, volume, brightness), shared between the
/// notch and the dock so both use the same code and stay in sync. Built by MainWindow.
/// </summary>
public sealed class QuickControls
{
    /// <summary>Make sure the Wi-Fi/Bluetooth radios have been found (quick if they already are).</summary>
    public required Func<Task> Ready { get; init; }
    public required Func<bool?> WifiOn { get; init; }            // null = no Wi-Fi radio
    public required Func<bool?> BluetoothOn { get; init; }       // null = no Bluetooth radio
    public required Func<bool, Task> SetWifi { get; init; }
    public required Func<bool, Task> SetBluetooth { get; init; }
    public required Func<bool> Muted { get; init; }
    public required Action<bool> SetMuted { get; init; }
    public required Func<bool> HasMic { get; init; }
    public required Func<bool> MicMuted { get; init; }
    public required Action<bool> SetMicMuted { get; init; }
    public required Func<float> Volume { get; init; }            // 0..1
    public required Action<float> SetVolume { get; init; }
    public required Func<Task<int?>> Brightness { get; init; }   // null = not adjustable (external monitor)
    public required Action<int> SetBrightness { get; init; }

    // now playing
    public required Func<(string Title, string Artist, System.Windows.Media.ImageSource? Art, bool Playing, bool HasSession)> NowPlaying { get; init; }
    public required Action PlayPause { get; init; }
    public required Action Next { get; init; }
    public required Action Previous { get; init; }

    // do not disturb: null = notifications through the notch is off (nothing to silence)
    public required Func<bool?> DoNotDisturb { get; init; }
    public required Action<bool> SetDoNotDisturb { get; init; }
    /// <summary>Windows battery saver: null = no battery on this PC.</summary>
    public Func<bool?> BatterySaverOn { get; init; } = () => null;
    public Action<bool> SetBatterySaver { get; init; } = _ => { };

    // focus timer
    public required Func<(string Text, bool Running)> Focus { get; init; }
    public required Action ToggleFocus { get; init; }
}
