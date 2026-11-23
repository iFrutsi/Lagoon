using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NotchApp.Services;

namespace NotchApp;

/// <summary>
/// Little alerts that slide out of the notch: charger plugged in / unplugged, low battery,
/// fully charged, Caps Lock on/off, microphone muted/unmuted (from anywhere, not just Lagoon).
/// All event-driven: Windows tells us; nothing polls.
/// </summary>
public partial class MainWindow
{
    static readonly FontFamily SymbolFont = new("Segoe UI Symbol");
    static readonly Color AlertGreen = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly Color AlertOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    static readonly Color AlertRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    const int WM_INPUT = 0x00FF, WM_POWERBROADCAST = 0x0218;
    const int PBT_APMPOWERSTATUSCHANGE = 0x000A, PBT_POWERSETTINGCHANGE = 0x8013;
    static Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    static Guid GUID_BATTERY_PERCENTAGE_REMAINING = new("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1");

    readonly List<IntPtr> _powerNotifies = new();

    void InitAlerts(IntPtr hwnd)
    {
        // battery: Windows sends WM_POWERBROADCAST when the charger or the percentage changes
        var (pct, charging) = Battery.Read();
        _battPct = pct;
        _battCharging = charging;
        _lowWarned = pct is { } p0 && !charging ? (p0 <= 10 ? 2 : p0 <= 20 ? 1 : 0) : 0;
        if (pct != null)
        {
            _powerNotifies.Add(RegisterPowerSettingNotification(hwnd, ref GUID_ACDC_POWER_SOURCE, 0));
            _powerNotifies.Add(RegisterPowerSettingNotification(hwnd, ref GUID_BATTERY_PERCENTAGE_REMAINING, 0));
        }

        // Caps Lock: raw keyboard input arrives after the key has already gone through, so (unlike a
        // keyboard hook) it can never slow down typing. RIDEV_INPUTSINK: even when we're not in front.
        var rid = new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = 0x100 /*INPUTSINK*/, hwndTarget = hwnd };
        RegisterRawInputDevices(new[] { rid }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _capsOn = (GetKeyState(VK_CAPITAL) & 1) != 0;

        // mic mute from anywhere (Windows quick settings, Teams, a mute key...)
        _audio.MicMuteChanged += muted => Dispatcher.BeginInvoke(() => OnMicMuteChanged(muted));
        // speakers: the dock's icon follows the level and mute (fires as the volume moves; cheap)
        _audio.SpeakerChanged += (muted, level, changed) => Dispatcher.BeginInvoke(() => OnSpeakerChanged(muted, level, changed));
        _audio.WatchMicMute();

        // earbuds: which headsets are connected now (no pop-out for these), then watch for new ones
        foreach (var b in BluetoothBattery.ReadAll()) _buds.Add(b.Name);
        _budsCheck.Tick += (_, _) => CheckBuds();
        _audio.DevicesChanged += () => Dispatcher.BeginInvoke(() =>
        {
            _budsTries = 0;
            _budsCheck.Interval = TimeSpan.FromSeconds(2);
            _budsCheck.Stop();
            _budsCheck.Start();
        });

        UpdateCompact();
        if (Environment.GetEnvironmentVariable("NOTCHAPP_BATTERYTEST") == "1") RunBatteryTest();
        if (Environment.GetEnvironmentVariable("NOTCHAPP_BUDSTEST") == "1") RunBudsTest();
        if (Environment.GetEnvironmentVariable("NOTCHAPP_ALARMTEST") == "1") RunAlarmTest();
    }

    void StopAlerts()
    {
        foreach (var h in _powerNotifies) if (h != IntPtr.Zero) UnregisterPowerSettingNotification(h);
        _powerNotifies.Clear();
        _audio.StopWatching();
    }

    /// <summary>Called from WndProc. True if the message was one of ours.</summary>
    bool HandleAlertMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) { OnRawKey(lParam); return true; }
        if (msg == WM_POWERBROADCAST && (wParam.ToInt32() is PBT_POWERSETTINGCHANGE or PBT_APMPOWERSTATUSCHANGE))
        {
            OnPowerChanged();
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ battery

    int? _battPct;
    bool _battCharging;
    int _lowWarned;        // 0 none, 1 = told at 20%, 2 = told at 10% (reset by charging)
    bool _fullToldThisCharge;

    void OnPowerChanged()
    {
        if (_fakePower) return; // dev test running
        var (pct, charging) = Battery.Read();
        OnPower(pct, charging);
        if (_mode == Mode.Expanded && _card.WidgetsPage.Visibility == Visibility.Visible) UpdateBatteryDetail();
    }

    bool _fakePower;

    /// <summary>dev: NOTCHAPP_BUDSTEST=1 shows the earbuds pop-out as if headphones just connected.</summary>
    async void RunBudsTest()
    {
        await Task.Delay(2000);
        var b = BluetoothBattery.Read();
        ShowBudsConnected(b?.Name ?? "Test Buds", b?.Percent ?? 40);
    }

    /// <summary>dev: NOTCHAPP_BATTERYTEST=1 plays plug in, unplug, 20%, 10%, full (2.5 s apart).</summary>
    async void RunBatteryTest()
    {
        _fakePower = true;
        _battPct = 54; _battCharging = false; _lowWarned = 0;
        foreach (var (p, c) in new[] { (54, true), (54, false), (19, false), (9, false), (99, true), (100, true) })
        {
            await Task.Delay(2500);
            OnPower(p, c);
        }
        await Task.Delay(2500);
        _fakePower = false;
        (_battPct, _battCharging) = Battery.Read();
    }

    void OnPower(int? pct, bool charging)
    {
        if (pct is not { } p) return;
        bool plugged = charging && !_battCharging, unplugged = !charging && _battCharging;
        _battCharging = charging;
        int? before = _battPct;
        _battPct = p;

        if (plugged)
        {
            _lowWarned = 0;
            _fullToldThisCharge = p >= 100;
            Alert("", $"Charging · {p}%", AlertGreen);
            if (p < 100) AddChargeEstimate($"Charging · {p}%");
        }
        else if (unplugged)
        {
            _fullToldThisCharge = false;
            Alert(BatteryGlyph(p), $"On battery · {p}%", ThemeManager.Foreground);
        }
        else if (charging && p >= 100 && before < 100 && !_fullToldThisCharge)
        {
            _fullToldThisCharge = true;
            Alert("", "Fully charged", AlertGreen);
        }
        else if (!charging && p <= 10 && _lowWarned < 2)
        {
            _lowWarned = 2;
            Alert(BatteryGlyph(p), $"Battery very low · {p}%", AlertRed, evenFullscreen: true);
        }
        else if (!charging && p <= 20 && _lowWarned < 1)
        {
            _lowWarned = 1;
            Alert(BatteryGlyph(p), $"Low battery · {p}%", AlertOrange, evenFullscreen: true);
        }
    }

    /// <summary>Windows only knows the charge rate a few seconds after plugging in: look a few
    /// times and, while the "Charging" pill is still up, add "· full in 1h 20m" to it.</summary>
    async void AddChargeEstimate(string shown)
    {
        if (_fakePower) return;
        for (int i = 0; i < 4; i++)
        {
            int? m = BatteryEstimate.MinutesToFull();
            if (m != null)
            {
                if (_mode == Mode.Toast && ToastText.Text == shown)
                {
                    ToastText.Text = $"{shown} · full in {BatteryEstimate.Span(m.Value)}";
                    _toastTimer.Stop(); _toastTimer.Start(); // a moment longer to read it
                }
                return;
            }
            await Task.Delay(1000);
            if (_mode != Mode.Toast || ToastText.Text != shown) return; // the pill already left
        }
    }

    /// <summary>Segoe battery glyphs: Battery0..9 = E850..E859, full = E83F.</summary>
    static string BatteryGlyph(int pct) => pct >= 95 ? "" : ((char)(0xE850 + Math.Clamp(pct / 10, 0, 9))).ToString();

    // ------------------------------------------------------------------ earbuds connected

    readonly HashSet<string> _buds = new();
    readonly DispatcherTimer _budsCheck = new();
    int _budsTries;

    /// <summary>An audio device came or went: look (a few times, the battery level shows up a bit
    /// after connecting) for headsets that weren't connected before.</summary>
    void CheckBuds()
    {
        _budsCheck.Stop();
        var now = BluetoothBattery.ReadAll();
        foreach (var b in now)
            if (!_buds.Contains(b.Name)) ShowBudsConnected(b.Name, b.Percent);
        _buds.Clear();
        foreach (var b in now) _buds.Add(b.Name); // ones that left drop out, so reconnecting pops again
        if (++_budsTries < 3)
        {
            _budsCheck.Interval = TimeSpan.FromSeconds(_budsTries + 2); // ~2, 5, 9 s after the change
            _budsCheck.Start();
        }
    }

    void ShowBudsConnected(string name, int pct)
    {
        // Lagoon's own pop-out (not a notification): shows even with do not disturb on
        _noticeQueue.Enqueue(new NoteItem(0, "Connected", name, $"Battery {pct}%", DateTimeOffset.Now, null, Glyph: ""));
        if (_mode == Mode.Notice) UpdateNoticeMore(); else TryShowQueued();
    }

    // ------------------------------------------------------------------ Caps Lock

    const int VK_CAPITAL = 0x14;
    bool _capsOn;

    void OnRawKey(IntPtr lParam)
    {
        uint size = 0, header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0 || size > 64) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buf, ref size, header) != size) return;
            if (Marshal.ReadInt32(buf) != 1 /*RIM_TYPEKEYBOARD*/) return;
            // RAWKEYBOARD after the header: MakeCode(2) Flags(2) Reserved(2) VKey(2) ...
            int off = (int)header;
            ushort flags = (ushort)Marshal.ReadInt16(buf, off + 2);
            ushort vkey = (ushort)Marshal.ReadInt16(buf, off + 6);
            if (vkey != VK_CAPITAL || (flags & 1 /*RI_KEY_BREAK*/) == 0) return;
        }
        finally { Marshal.FreeHGlobal(buf); }
        // the toggle flips on key down; read it once the key is back up
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            bool on = (GetKeyState(VK_CAPITAL) & 1) != 0;
            if (on == _capsOn) return;
            _capsOn = on;
            UpdateCompact();
            Alert("⇪", on ? "Caps Lock on" : "Caps Lock off", on ? AlertOrange : ThemeManager.Foreground);
        });
    }

    // ------------------------------------------------------------------ mic mute (from anywhere)

    void OnMicMuteChanged(bool muted)
    {
        if (muted == _micMuted) return; // Lagoon did it (already shown) or nothing changed
        _micMuted = muted;
        MicToggle.IsChecked = muted;
        UpdateCompact();
        if (muted) Alert("", "Microphone muted", AlertRed);
        else Alert("", "Microphone on", AlertGreen);
    }

    // ------------------------------------------------------------------ speakers

    /// <summary>The dock's speaker icon follows the level and mute.</summary>
    void OnSpeakerChanged(bool muted, float level, bool changed) => _dock?.SetVolume(muted, level);


    /// <summary>Show a toast unless a fullscreen app (video, game) is in front - except important ones.</summary>
    void Alert(string glyph, string text, Color color, bool evenFullscreen = false)
    {
        if (!evenFullscreen && _rest == Mode.Hidden) return;
        ShowToast(glyph, text, color);
    }

    // ---- Win32 ----

    const uint RID_INPUT = 0x10000003;
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [DllImport("user32.dll")] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
