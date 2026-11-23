using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchApp.Services;
using Windows.Devices.Radios;

namespace NotchApp;

public partial class MainWindow : Window
{
    // Hidden  : a fullscreen app is in front (videos, games)
    // Mini    : an app window is under the notch -> just a thin line on the top edge
    // Compact : desktop -> the pill with artwork / equalizer
    // Expanded: the full card, reached step by step while hovering
    enum Mode { Hidden, Mini, Compact, Expanded, Toast, Notice }

    // Island sizes (DIPs)
    const double ExpandedW = 604, ExpandedH = 256, ExpandedR = 34;
    const double ToastW = 290, ToastH = 38, ToastR = 19;
    const double NoticeW = 420, NoticeH = 84, NoticeR = 28; // a notification sliding out of the notch
    const double CompactH = 32, CompactR = 14;
    const double MiniW = 130, MiniH = 5, MiniR = 2.5;

    // hover dwell before each step, so sweeping across Chrome's tabs doesn't pop it open
    static readonly TimeSpan MiniToPill = TimeSpan.FromMilliseconds(250);
    static readonly TimeSpan PillToCard = TimeSpan.FromMilliseconds(400);

    Mode _rest = Mode.Compact; // where the notch settles when not hovered
    ContextWatcher? _context;
    PrivacyWatcher? _privacy;
    static readonly SolidColorBrush MicRed = Frozen(0xFF, 0x45, 0x3A);
    static readonly SolidColorBrush MicOrange = Frozen(0xFF, 0x9F, 0x0A);
    static readonly SolidColorBrush CamGreen = Frozen(0x30, 0xD1, 0x58);
    static readonly SolidColorBrush ScreenBlue = Frozen(0x0A, 0x84, 0xFF);

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    readonly MediaService _media = new();
    readonly AudioService _audio = new();
    readonly RadioService _radios = new();
    readonly BrightnessService _brightness = new();

    Mode _mode = Mode.Compact;

    // spring-animated island geometry
    double _w = 150, _h = CompactH, _r = CompactR, _vw, _vh, _vr;
    double _compactOpacity = 1, _expandedOpacity, _toastOpacity, _noticeOpacity;
    TimeSpan _lastFrame;
    bool _animating;

    readonly DispatcherTimer _stepTimer = new();
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1700) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly Random _rng = new();

    // countdown timer (the Timer tab, and the Controls tab's quick button); length picked in the Timer tab
    TimeSpan FocusLength => TimeSpan.FromMinutes(Math.Clamp(_settings.TimerMinutes, 1, 180));
    TimeSpan _focusLeft = TimeSpan.FromMinutes(25); // set from the saved length in InitTimers
    DateTime _focusEnd;
    bool _focusRunning;

    bool _scrubbing;
    bool _syncingUi; // true while code (not the user) moves sliders/toggles
    bool _micMuted;

    // Two fixed-size windows, never resized: resizing a see-through (layered) window blanks
    // it for a frame, which looked like the notch vanishing. This window (380x46) holds the
    // line, pill and toasts; the card is drawn by _card (680x300), which stays transparent
    // and click-through until it's needed. Opening/closing hands the island from one window
    // to the other only after the receiving window has drawn it, so one is always on screen.
    const double SmallW = 380, SmallH = 46;
    readonly CardWindow _card = new();
    bool _cardActive;            // the island is (also) being drawn by the card window
    enum Handoff { None, ToCard, ToPill }
    Handoff _handoff;
    int _handoffFrames;

    // card elements, so the rest of the code reads naturally
    Border CardIsland => _card.CardIsland;
    Grid ExpandedView => _card.ExpandedView;
    ImageBrush ArtBrush => _card.ArtBrush;
    TextBlock TitleText => _card.TitleText;
    TextBlock ArtistText => _card.ArtistText;
    Slider ProgressSlider => _card.ProgressSlider;
    TextBlock ElapsedText => _card.ElapsedText;
    TextBlock RemainingText => _card.RemainingText;
    Button PrevButton => _card.PrevButton;
    Button PlayButton => _card.PlayButton;
    Button NextButton => _card.NextButton;
    TextBlock ClockText => _card.ClockText;
    TextBlock DateText => _card.DateText;
    StackPanel PrivacyStatus => _card.PrivacyStatus;
    Ellipse StatusDot => _card.StatusDot;
    TextBlock StatusText => _card.StatusText;
    TextBlock FocusIcon => _card.FocusIcon;
    TextBlock FocusText => _card.FocusText;
    System.Windows.Controls.Primitives.ToggleButton WifiToggle => _card.WifiToggle;
    System.Windows.Controls.Primitives.ToggleButton BluetoothToggle => _card.BluetoothToggle;
    System.Windows.Controls.Primitives.ToggleButton MuteToggle => _card.MuteToggle;
    System.Windows.Controls.Primitives.ToggleButton MicToggle => _card.MicToggle;
    Slider VolumeSlider => _card.VolumeSlider;
    Slider BrightnessSlider => _card.BrightnessSlider;

    bool Hovered => Island.IsMouseOver || (_cardActive && CardIsland.IsMouseOver);

    readonly AppSettings _settings = AppSettings.Load();
    SettingsWindow? _settingsWindow;

    // ------------------------------------------------------------------ floating dock

    DockWindow? _dock;
    readonly WinKeyWatcher _winKeys = new();

    /// <summary>Turns the dock and the taskbar auto-hide on/off to match Settings.</summary>
    void ApplyDockSettings()
    {
        if (_settings.DockEnabled && _dock == null)
        {
            _dock = new DockWindow { Controls = BuildQuickControls() };
            _dock.PlayPause += () => _media.TogglePlayPause();
            _dock.Show();
            _dock.ApplyTheme();
            _dock.SetVolume(_audio.GetSpeakerMute(), _audio.GetVolume());
            OnMediaChanged();
            UpdateFocusUi();
        }
        else if (!_settings.DockEnabled && _dock != null)
        {
            _dock.Close();
            _dock = null;
        }

        bool hide = _settings.DockEnabled && _settings.HideTaskbar;
        if (hide)
        {
            if (_settings.TaskbarRestoreTo == null)
            {
                _settings.TaskbarRestoreTo = AppBar.TaskbarAutoHide; // remember how it was
                _settings.Save();
            }
            if (_settings.TaskbarAlignRestoreTo == null)
            {
                _settings.TaskbarAlignRestoreTo = TaskbarHider.Alignment; // remember left/centre
                _settings.Save();
            }
            if (_settings.TabletAutoHideRestoreTo == null)
            {
                _settings.TabletAutoHideRestoreTo = TaskbarHider.TabletAutoHide; // remember the tablet-mode setting
                _settings.Save();
            }
            if (_settings.TouchTaskbarRestoreTo == null)
            {
                _settings.TouchTaskbarRestoreTo = TaskbarHider.TouchTaskbar; // remember the touch-taskbar option
                _settings.Save();
            }
            TaskbarHider.TabletAutoHide = 1; // tablet/tent posture uses this one instead of normal auto-hide
            // Windows' touch taskbar (tablet posture) ignores auto-hide and reserves a strip; the normal one hides
            TaskbarHider.TouchTaskbar = 0;
            TaskbarHider.Hide();
            // centred: Start and Search open in the middle, above the dock. Only nudge Explorer when it isn't
            // centred yet - the nudge briefly shows the taskbar and flips its icons left/right.
            if (TaskbarHider.Alignment != 1) _ = TaskbarHider.ApplyCentredAsync();
            _winKeys.Start();                     // Windows key: un-hide the taskbar so Start can open
        }
        else { _winKeys.Stop(); RestoreTaskbar(); }
        if (_dock != null) _dock.KeepTaskbarHidden = hide;
    }

    void RestoreTaskbar() => RestoreTaskbar(_settings);

    /// <summary>The dock's quick settings use the same code as the notch, and update the notch too.</summary>
    QuickControls BuildQuickControls() => new()
    {
        Ready = () => _radios.WifiOn == null && _radios.BluetoothOn == null ? _radios.InitAsync() : Task.CompletedTask,
        WifiOn = () => _radios.WifiOn,
        BluetoothOn = () => _radios.BluetoothOn,
        SetWifi = on => _radios.SetAsync(RadioKind.WiFi, on),
        SetBluetooth = on => _radios.SetAsync(RadioKind.Bluetooth, on),
        Muted = _audio.GetSpeakerMute,
        SetMuted = muted =>
        {
            _audio.SetSpeakerMute(muted);
            MuteToggle.IsChecked = muted;
            _dock?.SetMuted(muted);
        },
        HasMic = () => _audio.HasMicrophone,
        MicMuted = _audio.GetMicMute,
        SetMicMuted = muted =>
        {
            _micMuted = muted;
            _audio.SetMicMute(muted);
            MicToggle.IsChecked = muted;
            UpdateCompact(); // the notch's mic indicator
        },
        Volume = _audio.GetVolume,
        SetVolume = v => _audio.SetVolume(v),
        Brightness = _brightness.GetAsync,
        SetBrightness = _brightness.Set,
        NowPlaying = () => (_media.Title, _media.Artist, _media.Art, _media.IsPlaying, _media.HasSession),
        PlayPause = _media.TogglePlayPause,
        Next = _media.Next,
        Previous = _media.Previous,
        Focus = () =>
        {
            var left = _focusLeft < TimeSpan.Zero ? TimeSpan.Zero : _focusLeft;
            return ($"{(int)left.TotalMinutes:00}:{left.Seconds:00}", _focusRunning);
        },
        ToggleFocus = () => Focus_Click(this, new RoutedEventArgs()),
        DoNotDisturb = () => _settings.NotchNotifications ? _settings.DoNotDisturb : null,
        SetDoNotDisturb = on => { if (on != _settings.DoNotDisturb) ToggleDnd(); },
        BatterySaverOn = () => BatterySaver.IsOn(_settings),
        SetBatterySaver = on => BatterySaver.Set(on, _settings),
    };

    /// <summary>Show the Windows taskbar again (also used by the crash handler).</summary>
    public static void RestoreTaskbar(AppSettings settings)
    {
        if (settings.TouchTaskbarRestoreTo is int touch)
        {
            TaskbarHider.TouchTaskbar = touch;
            settings.TouchTaskbarRestoreTo = null;
            settings.Save();
        }
        if (settings.TabletAutoHideRestoreTo is int tablet)
        {
            TaskbarHider.TabletAutoHide = tablet;
            settings.TabletAutoHideRestoreTo = null;
            settings.Save();
        }
        if (settings.TaskbarAlignRestoreTo is int align)
        {
            TaskbarHider.Alignment = align;
            settings.TaskbarAlignRestoreTo = null;
            settings.Save();
        }
        if (settings.TaskbarRestoreTo is not bool previous) return;
        TaskbarHider.Restore(previous);
        settings.TaskbarRestoreTo = null;
        settings.Save();
    }

    void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplyDockSettings, NoticeSettingsChanged);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; _trimTimer.Start(); };
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // ------------------------------------------------------------------ theme

    void ApplyTheme()
    {
        var edge = ThemeManager.Edge;
        var thickness = edge != null ? new Thickness(1, 0, 1, 1) : new Thickness(0);
        Island.BorderBrush = edge; Island.BorderThickness = thickness;
        CardIsland.BorderBrush = edge; CardIsland.BorderThickness = thickness;

        CardIsland.Background = ThemeManager.Island;
        UpdateIslandFill();
        ApplyGeometry();
        OnMediaChanged();
        _dock?.ApplyTheme();
    }

    void WireCard()
    {
        _card.SettingsButton.Click += (_, _) => OpenSettings();
        _card.PaletteButton.Click += Palette_Click;
        _card.PageChanged += OnPageChanged;
        CardIsland.MouseEnter += Island_MouseEnter;
        CardIsland.MouseLeave += Island_MouseLeave;
        PlayButton.Click += Play_Click;
        NextButton.Click += Next_Click;
        PrevButton.Click += Prev_Click;
        ProgressSlider.PreviewMouseLeftButtonDown += Progress_MouseDown;
        ProgressSlider.PreviewMouseLeftButtonUp += Progress_MouseUp;
        _card.FocusButton.Click += Focus_Click;
        _card.FocusResetButton.Click += FocusReset_Click;
        WifiToggle.Click += Wifi_Click;
        BluetoothToggle.Click += Bluetooth_Click;
        MuteToggle.Click += Mute_Click;
        MicToggle.Click += Mic_Click;
        VolumeSlider.ValueChanged += Volume_Changed;
        BrightnessSlider.ValueChanged += Brightness_Changed;
    }

    // After the notch settles, give memory back to Windows (what Task Manager shows).
    readonly DispatcherTimer _trimTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _idleTrimTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    public MainWindow()
    {
        InitializeComponent();
        WireCard();
        // touch: a tap anywhere else closes the open card (there's no "mouse leave" with a finger)
        _cardTap = new OutsideTap(() => Array.Empty<Rect>(), () => { if (_mode == Mode.Expanded && !_typingTodo) Collapse(); });
        _card.CardHandle.MouseLeftButtonUp += (_, e) => { Collapse(); e.Handled = true; };
        WireSwipes();
        InitPages();
        InitTimers();

        _stepTimer.Tick += (_, _) => StepOpen();
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!Hovered && Mouse.Captured == null) Collapse();
        };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); if (_mode == Mode.Toast) SetMode(_rest); };
        _tick.Tick += (_, _) => OnTick();
        _trimTimer.Tick += (_, _) => { _trimTimer.Stop(); TrimMemory(); };
        _idleTrimTimer.Tick += (_, _) => { if (_mode is Mode.Compact or Mode.Mini or Mode.Hidden && !_animating) TrimMemory(); };
        _idleTrimTimer.Start();

        _media.Changed += OnMediaChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            _card.Show(); // invisible and click-through until the card opens
            PlaceAtTop();
            ThemeManager.Changed += ApplyTheme;
            ThemeManager.Apply(_settings.Theme);
            ApplyDockSettings();
            _micMuted = _audio.GetMicMute();
            UpdateCompact();
            StartAnimation(); // settle (and shrink the window) whatever the starting mode is
            _privacy = new PrivacyWatcher();
            _privacy.Changed += OnPrivacyChanged;
            OnPrivacyChanged();
            if (Environment.GetEnvironmentVariable("NOTCHAPP_SELFTEST") == "1") RunSelfTest();
            if (Environment.GetEnvironmentVariable("NOTCHAPP_SHOWSETTINGS") == "1") OpenSettings(); // dev
            if (Environment.GetEnvironmentVariable("NOTCHAPP_OPENCARD") is { } tab) // dev: hold the card open
            {
                _context?.Dispose();
                var t = tab switch
                {
                    "controls" => _card.TabControls, "clipboard" => _card.TabClipboard,
                    "widgets" => _card.TabWidgets, "agents" => _card.TabAgents, "timer" => _card.TabTimer, _ => _card.TabMedia,
                };
                t.IsChecked = true;
                SetMode(Mode.Expanded);
            }
            await _media.InitAsync();
            await _radios.InitAsync();
            if (_mode == Mode.Expanded) RefreshControls(); // radios found after the card opened
            OnMediaChanged();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.InvokeAsync(PlaceAtTop);
    }

    // ------------------------------------------------------------------ window plumbing

    const int WM_HOTKEY = 0x0312, HOTKEY_MIC = 1, WM_CLIPBOARDUPDATE = 0x031D;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }


    /// <summary>
    /// Over apps the line itself is the indicator: red = mic muted, green = camera,
    /// orange = mic in use, blue = screen being captured.
    /// </summary>
    void UpdateIslandFill()
    {
        Brush fill = ThemeManager.Island;
        if (_mode == Mode.Mini)
        {
            fill = ThemeManager.Line;
            if (_micMuted) fill = MicRed;
            else if (_privacy?.Camera != null) fill = CamGreen;
            else if (_privacy?.Mic != null) fill = MicOrange;
            else if (_privacy?.Screen != null) fill = ScreenBlue;
        }
        Island.Background = fill;
    }

    // ------------------------------------------------------------------ privacy dots & call timer

    int PrivacyDotCount() =>
        (DotCamera.Visibility == Visibility.Visible ? 1 : 0) + (DotMic.Visibility == Visibility.Visible ? 1 : 0)
        + (DotScreen.Visibility == Visibility.Visible ? 1 : 0);

    void OnPrivacyChanged()
    {
        var p = _privacy;
        DotCamera.Visibility = p?.Camera != null ? Visibility.Visible : Visibility.Collapsed;
        DotMic.Visibility = p?.Mic != null ? Visibility.Visible : Visibility.Collapsed;
        DotScreen.Visibility = p?.Screen != null ? Visibility.Visible : Visibility.Collapsed;
        PrivacyDots.Visibility = p?.AnyInUse == true ? Visibility.Visible : Visibility.Collapsed;
        CallTimer.Visibility = p?.Mic != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateCallTimer();
        UpdateCompact();
        UpdateTickState();
    }

    /// <summary>Call timer = how long any app has had the microphone open.</summary>
    void UpdateCallTimer()
    {
        var p = _privacy;
        if (p == null || !p.AnyInUse)
        {
            PrivacyStatus.Visibility = Visibility.Collapsed;
            return;
        }
        string Since(Usage u) => Format(DateTime.Now - u.Since);

        if (p.Mic != null)
        {
            CallTimer.Text = Since(p.Mic);
            StatusDot.Fill = _micMuted ? MicRed : MicOrange;
            StatusText.Text = $"{p.Mic.App} · {Since(p.Mic)}" + (_micMuted ? " (muted)" : "");
        }
        else if (p.Camera != null)
        {
            StatusDot.Fill = CamGreen;
            StatusText.Text = $"Camera · {p.Camera.App}";
        }
        else
        {
            StatusDot.Fill = ScreenBlue;
            StatusText.Text = $"Screen · {p.Screen!.App}";
        }

        var lines = new List<string>();
        if (p.Mic != null) lines.Add($"Microphone: {p.Mic.App} for {Since(p.Mic)}");
        if (p.Camera != null) lines.Add($"Camera: {p.Camera.App}");
        if (p.Screen != null) lines.Add($"Screen capture: {p.Screen.App}");
        PrivacyStatus.ToolTip = string.Join("\n", lines);
        PrivacyStatus.Visibility = Visibility.Visible;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowUtil.MakeToolWindow(this);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        _clips.Attach(hwnd); // Windows tells us when something is copied
        PlaceAtTop(); // centred before the first frame is ever drawn
        RegisterHotKey(hwnd, HOTKEY_MIC, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(Key.M));

        _context = new ContextWatcher(hwnd);
        _context.Changed += _ => ApplySurroundings();
        _context.WindowsMoved += KeepOnTop;
        ApplySurroundings();
        InitAlerts(hwnd);
        InitExtras(); // codes, downloads, Wi-Fi pills, long-press peek
        InitNotices();
    }

    /// <summary>Notch, card and dock back above other windows if Windows let them slip under.</summary>
    internal void KeepOnTop()
    {
        // same order as they're stacked normally (card, notch, dock: the last one ends up in front)
        WindowUtil.KeepOnTop(_card);
        WindowUtil.KeepOnTop(this);
        WindowUtil.KeepOnTop(_dock);
    }

    void ApplySurroundings()
    {
        _rest = _context?.Current switch
        {
            Surroundings.Fullscreen => Mode.Hidden,
            Surroundings.App => Mode.Mini,
            _ => Mode.Compact,
        };
        UpdateNotesPaused();
        // Only move if we're resting; an open card or a toast settles into the new rest later.
        bool resting = _mode is Mode.Hidden or Mode.Mini or Mode.Compact;
        if (resting && !Hovered && _mode != _rest) SetMode(_rest);
        // went fullscreen while the alarm rings: hush it. Left fullscreen: a timer that ended meanwhile.
        if (_rest == Mode.Hidden) StopAlarm(); else ShowMissedTimer();
    }

    protected override void OnClosed(EventArgs e)
    {
        _card.Close();
        _dock?.Close();
        _winKeys.Dispose();
        // never leave the taskbar hidden after we quit (re-applied on next start) - except when Windows
        // itself is shutting down: then it stays hidden, so the next boot doesn't flash the old taskbar
        if (!App.WindowsShuttingDown) RestoreTaskbar();
        _agents.Dispose();
        _clips.Detach(new WindowInteropHelper(this).Handle);
        _context?.Dispose();
        _privacy?.Dispose();
        StopAlerts();
        StopExtras();
        _notes?.Dispose();
        // Windows' own notification banners back on (kept off through a Windows restart, like the taskbar)
        if (!App.WindowsShuttingDown) NotificationBanners.RestoreAll(_settings);
        UnregisterHotKey(new WindowInteropHelper(this).Handle, HOTKEY_MIC);
        base.OnClosed(e);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_MIC)
        {
            ToggleMic();
            handled = true;
        }
        else if (msg == WM_CLIPBOARDUPDATE)
        {
            _clips.OnClipboardUpdate();
        }
        else HandleAlertMessage(msg, wParam, lParam); // battery, Caps Lock (not marked handled: Windows cleans up)
        return IntPtr.Zero;
    }

    void PlaceAtTop()
    {
        WindowUtil.PlaceTopCentre(this, SmallW, SmallH);
        _card.Place();
    }

    void Quit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>Dev only (NOTCHAPP_SELFTEST=1): walks through every transition, then quits.</summary>
    async void RunSelfTest()
    {
        _context?.Dispose(); // keep the real surroundings from interfering
        var steps = new[] { Mode.Mini, Mode.Compact, Mode.Expanded, Mode.Compact, Mode.Mini, Mode.Hidden, Mode.Mini, Mode.Compact, Mode.Mini };
        await Task.Delay(1500);
        foreach (var m in steps)
        {
            Log.Write($"selftest {m}");
            if (m != Mode.Expanded && m != Mode.Toast) _rest = m;
            SetMode(m);
            if (m == Mode.Expanded)
            {
                _scrubbing = true; // freeze progress at 55% so the wave is visible
                ProgressSlider.Value = ProgressSlider.Maximum * 0.55;
                await Task.Delay(1300);
                Log.Write("selftest Controls tab");
                _card.TabControls.IsChecked = true;
                await Task.Delay(1300);
                _card.TabMedia.IsChecked = true;
                _scrubbing = false;
                continue;
            }
            await Task.Delay(1300);
        }
        Log.Write("selftest done");
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ modes & hover

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        ScheduleStep();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        _stepTimer.Stop();
        if (_mode != _rest && _mode != Mode.Notice) _collapseTimer.Start(); // a notification has its own timer
    }

    /// <summary>
    /// Over apps:   hover grows the line into the pill; the card opens only on click.
    /// On desktop:  hovering the pill opens the card.
    /// </summary>
    void ScheduleStep()
    {
        _stepTimer.Stop();
        TimeSpan? delay = _mode switch
        {
            Mode.Mini => MiniToPill,
            Mode.Compact or Mode.Toast when _rest == Mode.Compact => PillToCard,
            _ => null,
        };
        if (delay == null) return;
        _stepTimer.Interval = delay.Value;
        _stepTimer.Start();
    }

    void StepOpen()
    {
        _stepTimer.Stop();
        if (!Hovered) return;
        if (_mode == Mode.Mini) SetMode(Mode.Compact);
        else OpenCard();
    }

    void OpenCard()
    {
        _stepTimer.Stop();
        _collapseTimer.Stop();
        RefreshControls();
        SetMode(Mode.Expanded);
    }

    void Island_Click(object sender, MouseButtonEventArgs e)
    {
        if (_mode is Mode.Expanded or Mode.Hidden) return;
        OpenCard();
        e.Handled = true;
    }

    void Collapse() => SetMode(_rest);

    /// <summary>
    /// Touch gestures. Both hold mouse capture for the drag, so the gesture survives the finger
    /// sliding off the window (the notch is only 46px tall, so it always does), and both fire as
    /// soon as the distance is reached instead of waiting for the finger to lift.
    ///  - swipe down on the pill / line  -> open the card
    ///  - swipe up on the card           -> close it (not when the drag starts in a list, slider or text box)
    /// </summary>
    void WireSwipes()
    {
        Point? down = null;
        Island.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_mode is Mode.Expanded or Mode.Hidden) return;
            down = e.GetPosition(Island);
            Island.CaptureMouse();
        };
        Island.PreviewMouseMove += (_, e) =>
        {
            if (down is not { } s || !Island.IsMouseCaptured) return;
            var p = e.GetPosition(Island);
            if (p.Y - s.Y < 18 || Math.Abs(p.X - s.X) > 60) return;
            down = null;
            Island.ReleaseMouseCapture();
            OpenCard();
        };
        Island.PreviewMouseLeftButtonUp += (_, _) =>
        {
            down = null;
            if (Island.IsMouseCaptured) Island.ReleaseMouseCapture(); // a plain tap still reaches Island_Click
        };

        Point? up = null;
        CardIsland.PreviewMouseLeftButtonDown += (_, e) =>
        {
            up = null;
            if (_mode == Mode.Notice) { up = e.GetPosition(CardIsland); Mouse.Capture(CardIsland, CaptureMode.SubTree); return; }
            if (_mode != Mode.Expanded || InScrollable(e.OriginalSource as DependencyObject)) return;
            up = e.GetPosition(CardIsland);
            // SubTree: buttons inside still get their clicks; only presses that leave the card come to us
            Mouse.Capture(CardIsland, CaptureMode.SubTree);
        };
        CardIsland.PreviewMouseMove += (_, e) =>
        {
            if (up is not { } s) return;
            var p = e.GetPosition(CardIsland);
            bool notice = _mode == Mode.Notice;
            if (s.Y - p.Y < (notice ? 24 : 50) || Math.Abs(p.X - s.X) > 80) return; // the notification is short
            up = null;
            if (Mouse.Captured == CardIsland) Mouse.Capture(null);
            _swipedNotice = notice;
            if (notice) DismissNotice(); else Collapse();
        };
        CardIsland.PreviewMouseLeftButtonUp += (_, _) =>
        {
            up = null;
            if (Mouse.Captured == CardIsland) Mouse.Capture(null);
        };
    }

    static bool InScrollable(DependencyObject? d)
    {
        for (; d != null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer or Slider or TextBox or System.Windows.Controls.Primitives.ScrollBar) return true;
        return false;
    }

    OutsideTap? _cardTap;

    void SetMode(Mode mode)
    {
        Log.Write($"mode {_mode} -> {mode} (rest {_rest}, visible {IsVisible})");
        _mode = mode;
        if (mode == Mode.Expanded) _cardTap?.Start(); else _cardTap?.Stop();
        Island.Cursor = mode == Mode.Expanded ? null : Cursors.Hand; // hand = "click to open"
        UpdateIslandFill();
        ExpandedView.IsHitTestVisible = mode == Mode.Expanded;
        _card.NoticeView.IsHitTestVisible = mode == Mode.Notice;
        _card.CardHandle.Visibility = mode == Mode.Notice ? Visibility.Collapsed : Visibility.Visible;
        // the card and a notification both draw in the card window (the pill window is too small)
        if (mode is Mode.Expanded or Mode.Notice)
        {
            if (!_cardActive)
            {
                // Card window starts drawing the island exactly where the pill is;
                // the pill hides only once that frame is on screen.
                _cardActive = true;
                _handoff = Handoff.ToCard;
                _handoffFrames = 2;
                CardIsland.Visibility = Visibility.Visible;
                ApplyGeometry();
            }
            else if (_handoff == Handoff.ToPill)
            {
                _handoff = Handoff.None; // reopened mid-close: keep the card
            }
        }
        if (mode == Mode.Expanded) { _toastTimer.Stop(); UpdateClock(); UpdateProgress(); }
        UpdateTickState();
        UpdateWave();
        if (mode == Mode.Expanded)
        {
            OnPageChanged();   // fill the visible tab
        }
        StartAnimation();
        // back to rest (card closed, toast over): notifications that waited can come out now
        if (mode is Mode.Compact or Mode.Mini) TryShowQueued();
    }

    void ShowToast(string glyph, string text, Color color)
    {
        if (_mode is Mode.Expanded or Mode.Notice) return; // the card already shows the change / a notification is up
        ToastIcon.Text = glyph;
        // the Claude Code mark is plain text in a code font; everything else is an icon-font glyph
        bool code = glyph == CodeGlyph;
        // icon-font glyphs live in the private-use range; ordinary symbols (⇪) come from Segoe UI Symbol
        bool symbol = !code && glyph.Length > 0 && glyph[0] < '';
        ToastIcon.FontFamily = code ? CodeFont : symbol ? SymbolFont : (FontFamily)FindResource("IconFont");
        ToastIcon.FontWeight = code ? FontWeights.Bold : FontWeights.Normal;
        ToastIcon.Foreground = new SolidColorBrush(color);
        ToastText.Text = text;
        SetMode(Mode.Toast);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    (double w, double h, double r) Target()
    {
        switch (_mode)
        {
            case Mode.Expanded: return (ExpandedW, ExpandedH, ExpandedR);
            case Mode.Toast: return (ToastW, ToastH, ToastR);
            case Mode.Notice: return (NoticeW, NoticeH, NoticeR);
            case Mode.Mini: return (MiniW, MiniH, MiniR);
            case Mode.Hidden: return (MiniW, 0, 0);
            default:
                bool left = CompactArt.Visibility == Visibility.Visible;
                bool right = EqBars.Visibility == Visibility.Visible || CompactTimer.Visibility == Visibility.Visible
                             || CompactMic.Visibility == Visibility.Visible || PrivacyDots.Visibility == Visibility.Visible
                             || CompactStopwatch.Visibility == Visibility.Visible || CompactCaps.Visibility == Visibility.Visible
                             || CompactDnd.Visibility == Visibility.Visible;
                double w = 150;
                if (left || right) w = 210;
                if (CompactTimer.Visibility == Visibility.Visible) w += 30;
                if (CompactStopwatch.Visibility == Visibility.Visible) w += CompactStopwatch.Text.Length > 5 ? 50 : 34;
                if (CompactCaps.Visibility == Visibility.Visible) w += 10;
                if (CompactDnd.Visibility == Visibility.Visible) w += 10;
                if (CallTimer.Visibility == Visibility.Visible) w += 40;
                if (PrivacyDots.Visibility == Visibility.Visible) w += 10 * PrivacyDotCount();
                return (w, CompactH, CompactR);
        }
    }

    // ------------------------------------------------------------------ spring animation

    void StartAnimation()
    {
        _trimTimer.Stop();
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame) return;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - _lastFrame).TotalSeconds, 1 / 30.0);
        _lastFrame = now;

        if (_handoff != Handoff.None && --_handoffFrames <= 0) CompleteHandoff();

        var (tw, th, tr) = Target();
        Spring(ref _w, ref _vw, tw, dt);
        Spring(ref _h, ref _vh, th, dt);
        Spring(ref _r, ref _vr, tr, dt);

        // content fades: out fast, in slightly later so it doesn't squash while the island grows
        double fadeIn = 1 - Math.Exp(-dt * 14), fadeOut = 1 - Math.Exp(-dt * 30);
        _compactOpacity = Approach(_compactOpacity, _mode == Mode.Compact ? 1 : 0, fadeIn, fadeOut);
        _toastOpacity = Approach(_toastOpacity, _mode == Mode.Toast ? 1 : 0, fadeIn, fadeOut);
        double expandedTarget = _mode == Mode.Expanded && _h > ExpandedH * 0.6 ? 1 : 0;
        _expandedOpacity = Approach(_expandedOpacity, expandedTarget, fadeIn, fadeOut);
        double noticeTarget = _mode == Mode.Notice && _h > NoticeH * 0.6 ? 1 : 0;
        _noticeOpacity = Approach(_noticeOpacity, noticeTarget, fadeIn, fadeOut);

        bool settled = Math.Abs(_w - tw) < 0.3 && Math.Abs(_vw) < 2
                    && Math.Abs(_h - th) < 0.3 && Math.Abs(_vh) < 2
                    && Math.Abs(_r - tr) < 0.3 && Math.Abs(_vr) < 2
                    && Math.Abs(_compactOpacity - (_mode == Mode.Compact ? 1 : 0)) < 0.01
                    && Math.Abs(_toastOpacity - (_mode == Mode.Toast ? 1 : 0)) < 0.01
                    && Math.Abs(_expandedOpacity - expandedTarget) < 0.01
                    && Math.Abs(_noticeOpacity - noticeTarget) < 0.01
                    && (_mode != Mode.Expanded || expandedTarget == 1)
                    && (_mode != Mode.Notice || noticeTarget == 1);
        if (settled)
        {
            _w = tw; _h = th; _r = tr; _vw = _vh = _vr = 0;
            _compactOpacity = Math.Round(_compactOpacity);
            _toastOpacity = Math.Round(_toastOpacity);
            _expandedOpacity = Math.Round(_expandedOpacity);
            _noticeOpacity = Math.Round(_noticeOpacity);
        }
        ApplyGeometry();

        if (settled && _handoff == Handoff.None)
        {
            if (_cardActive && _mode is not (Mode.Expanded or Mode.Notice))
            {
                // Card has shrunk back to the pill: show the pill, drop the card next frame.
                _handoff = Handoff.ToPill;
                _handoffFrames = 2;
                ApplyGeometry();
                return; // keep rendering until the handoff completes
            }
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            if (_mode is not (Mode.Expanded or Mode.Toast or Mode.Notice)) _trimTimer.Start();
        }
    }

    void CompleteHandoff()
    {
        if (_handoff == Handoff.ToPill)
        {
            CardIsland.Visibility = Visibility.Hidden;
            ExpandedView.Opacity = 0;
            _card.NoticeView.Opacity = 0;
            _cardActive = false;
            ReleasePages();
        }
        _handoff = Handoff.None;
        ApplyGeometry();
    }

    void ApplyGeometry()
    {
        double w = Math.Max(1, _w), h = Math.Max(1, _h), r = Math.Max(0, _r);
        var corner = new CornerRadius(0, 0, r, r);

        if (_cardActive)
        {
            CardIsland.Width = w;
            CardIsland.Height = h;
            CardIsland.CornerRadius = corner;
            ExpandedView.Opacity = _expandedOpacity;
            _card.NoticeView.Opacity = _noticeOpacity;
        }

        // The pill is hidden only while the card fully owns the island (or in Hidden mode,
        // which shrinks to nothing instead of hiding the window: no Hide/Show flash).
        bool pillShown = !(_cardActive && _handoff == Handoff.None) && !(_mode == Mode.Hidden && _h < 1);
        Island.Visibility = pillShown ? Visibility.Visible : Visibility.Hidden;
        double pw = Math.Min(w, SmallW), ph = Math.Min(h, SmallH);
        if (!pillShown) return;
        Island.Width = pw;
        Island.Height = ph;
        Island.CornerRadius = corner;
        CompactView.Opacity = _compactOpacity;
        ToastView.Opacity = _toastOpacity;
    }

    static void Spring(ref double x, ref double v, double target, double dt)
    {
        const double stiffness = 420, damping = 31; // slightly under-damped: a small, soft overshoot
        double force = stiffness * (target - x) - damping * v;
        v += force * dt;
        x += v * dt;
    }

    static double Approach(double value, double target, double kIn, double kOut) =>
        value + (target - value) * (target > value ? kIn : kOut);

    // ------------------------------------------------------------------ periodic updates

    void UpdateTickState()
    {
        // As a line or hidden, nothing animates: the only ticking is the focus countdown.
        bool callVisible = _privacy?.Mic != null && _mode is Mode.Compact or Mode.Expanded or Mode.Notice;
        bool needed = _mode == Mode.Expanded || (_mode == Mode.Compact && _media.IsPlaying) || _focusRunning || _swRunning || callVisible;
        // tenths of a second on the open stopwatch; 4x a second is plenty for everything else
        bool fast = _swRunning && _mode == Mode.Expanded && _card.CurrentPage == _card.TimerPage;
        var interval = TimeSpan.FromMilliseconds(fast ? 100 : 250);
        if (_tick.Interval != interval) _tick.Interval = interval;
        if (needed && !_tick.IsEnabled) _tick.Start();
        else if (!needed && _tick.IsEnabled) _tick.Stop();
    }

    void OnTick()
    {
        if (_privacy?.AnyInUse == true) UpdateCallTimer();
        if (_mode == Mode.Notice) UpdateNoticeCall(); // the call timer inside a notification
        if (_focusRunning)
        {
            _focusLeft = _focusEnd - DateTime.Now;
            if (_focusLeft <= TimeSpan.Zero) FocusFinished();
            UpdateFocusUi();
        }
        if (_swRunning) UpdateStopwatchUi();
        if (_mode == Mode.Expanded)
        {
            UpdateClock();
            UpdateProgress();
            TickPages();
        }
        else if (_mode == Mode.Compact && _media.IsPlaying && EqBars.Visibility == Visibility.Visible)
        {
            AnimateEq();
        }
    }

    void AnimateEq()
    {
        foreach (Rectangle bar in EqBars.Children)
        {
            var anim = new DoubleAnimation(4 + _rng.NextDouble() * 12, TimeSpan.FromMilliseconds(230))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(anim, 20); // 20 fps is plenty for 3px bars, a third of the CPU of 60
            bar.BeginAnimation(HeightProperty, anim);
        }
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = TimeFormat.Time(now);
        DateText.Text = now.ToString("ddd, d MMM");
    }

    // ------------------------------------------------------------------ media

    void OnMediaChanged()
    {
        bool has = _media.HasSession && (_media.Title.Length > 0 || _media.Art != null);
        TitleText.Text = has ? _media.Title : "Nothing playing";
        ArtistText.Text = has ? _media.Artist : "Play something in any app";
        ArtBrush.ImageSource = has ? _media.Art : null;
        CompactArtBrush.ImageSource = _media.Art;
        _card.SetPlaying(_media.IsPlaying);
        PrevButton.IsEnabled = PlayButton.IsEnabled = NextButton.IsEnabled = _media.HasSession;
        ProgressSlider.IsEnabled = _media.CanSeek && _media.Duration > TimeSpan.Zero;

        // Tint from the artwork; with no artwork, use the theme's text colour (white on white = invisible).
        var accent = new SolidColorBrush(_media.Art != null ? _media.ArtColor : ThemeManager.Foreground);
        accent.Freeze();
        foreach (Rectangle bar in EqBars.Children) bar.Fill = accent;
        _card.SetAccent(accent, _media.Art != null ? accent : (Brush)FindResource("Subtle"));
        _dock?.SetMedia(_media.Art, _media.IsPlaying, _media.HasSession);

        UpdateProgress();
        UpdateCompact();
        UpdateTickState();
        UpdateWave();
    }

    /// <summary>The wave moves only while the card is open on the media tab and music plays.</summary>
    void UpdateWave() =>
        _card.SetWaveRunning(_mode == Mode.Expanded && _media.IsPlaying && _card.MediaTabShown);

    void Palette_Click(object sender, RoutedEventArgs e)
    {
        var next = ThemeManager.Current == Theme.Black ? Theme.Glass : Theme.Black;
        _settings.Theme = next;
        _settings.Save();
        ThemeManager.Apply(next);
    }

    void UpdateProgress()
    {
        if (_scrubbing) return;
        var dur = _media.Duration;
        var pos = _media.Position;
        _syncingUi = true;
        ProgressSlider.Maximum = Math.Max(1, dur.TotalSeconds);
        ProgressSlider.Value = Math.Min(pos.TotalSeconds, ProgressSlider.Maximum);
        _syncingUi = false;
        ElapsedText.Text = Format(pos);
        RemainingText.Text = dur > TimeSpan.Zero ? "-" + Format(dur - pos) : "";
    }

    static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    void Play_Click(object sender, RoutedEventArgs e) => _media.TogglePlayPause();
    void Next_Click(object sender, RoutedEventArgs e) => _media.Next();
    void Prev_Click(object sender, RoutedEventArgs e) => _media.Previous();

    void Progress_MouseDown(object sender, MouseButtonEventArgs e) => _scrubbing = true;

    void Progress_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        _media.Seek(TimeSpan.FromSeconds(ProgressSlider.Value));
        UpdateProgress();
    }

    // ------------------------------------------------------------------ compact pill contents

    void UpdateCompact()
    {
        CompactArt.Visibility = _media.HasSession && _media.Art != null ? Visibility.Visible : Visibility.Collapsed;
        CompactMic.Visibility = _micMuted ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = _focusRunning ? Visibility.Visible : Visibility.Collapsed;
        CompactStopwatch.Visibility = _swRunning ? Visibility.Visible : Visibility.Collapsed;
        CompactCaps.Visibility = _capsOn ? Visibility.Visible : Visibility.Collapsed;
        bool onCall = _privacy?.Mic != null;
        EqBars.Visibility = _media.IsPlaying && !_focusRunning && !_swRunning && !onCall ? Visibility.Visible : Visibility.Collapsed;
        UpdateIslandFill();
        // Only re-animate if the pill's size actually changes (media updates arrive every second or so).
        var (tw, th, _) = Target();
        if (_mode is Mode.Compact or Mode.Toast && (Math.Abs(tw - _w) > 0.5 || Math.Abs(th - _h) > 0.5))
            StartAnimation();
    }

    // ------------------------------------------------------------------ quick controls

    async void RefreshControls()
    {
        _syncingUi = true;
        VolumeSlider.Value = Math.Round(_audio.GetVolume() * 100);
        MuteToggle.IsChecked = _audio.GetSpeakerMute();
        _micMuted = _audio.GetMicMute();
        MicToggle.IsChecked = _micMuted;
        MicToggle.IsEnabled = _audio.HasMicrophone;
        WifiToggle.IsEnabled = _radios.WifiOn != null;
        WifiToggle.IsChecked = _radios.WifiOn == true;
        BluetoothToggle.IsEnabled = _radios.BluetoothOn != null;
        BluetoothToggle.IsChecked = _radios.BluetoothOn == true;
        _syncingUi = false;

        var level = await _brightness.GetAsync();
        _syncingUi = true;
        BrightnessSlider.IsEnabled = level != null;
        BrightnessSlider.Value = level ?? 0;
        _syncingUi = false;
    }

    void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi) return;
        _audio.SetVolume((float)(e.NewValue / 100));
        if (MuteToggle.IsChecked == true && e.NewValue > 0)
        {
            _audio.SetSpeakerMute(false);
            MuteToggle.IsChecked = false;
        }
    }

    void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncingUi) _brightness.Set((int)Math.Round(e.NewValue));
    }

    void Mute_Click(object sender, RoutedEventArgs e)
    {
        _audio.SetSpeakerMute(MuteToggle.IsChecked == true);
        _dock?.SetMuted(MuteToggle.IsChecked == true);
    }

    void Mic_Click(object sender, RoutedEventArgs e)
    {
        _micMuted = MicToggle.IsChecked == true;
        _audio.SetMicMute(_micMuted);
        UpdateCompact();
    }

    void ToggleMic()
    {
        if (!_audio.HasMicrophone) { ShowToast("", "No microphone found", ThemeManager.Foreground); return; }
        _micMuted = !_audio.GetMicMute();
        _audio.SetMicMute(_micMuted);
        MicToggle.IsChecked = _micMuted;
        UpdateCompact();
        if (_micMuted) ShowToast("", "Microphone muted", Color.FromRgb(0xFF, 0x45, 0x3A));
        else ShowToast("", "Microphone on", Color.FromRgb(0x30, 0xD1, 0x58));
    }

    async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        await _radios.SetAsync(RadioKind.WiFi, WifiToggle.IsChecked == true);
        WifiToggle.IsChecked = _radios.WifiOn == true;
    }

    async void Bluetooth_Click(object sender, RoutedEventArgs e)
    {
        await _radios.SetAsync(RadioKind.Bluetooth, BluetoothToggle.IsChecked == true);
        BluetoothToggle.IsChecked = _radios.BluetoothOn == true;
    }

    // ------------------------------------------------------------------ focus timer

    void Focus_Click(object sender, RoutedEventArgs e)
    {
        if (_focusRunning)
        {
            _focusLeft = _focusEnd - DateTime.Now;
            _focusRunning = false;
        }
        else
        {
            if (_focusLeft <= TimeSpan.Zero) _focusLeft = FocusLength;
            _focusEnd = DateTime.Now + _focusLeft;
            _focusRunning = true;
        }
        UpdateFocusUi();
        UpdateCompact();
        UpdateTickState();
    }

    void FocusReset_Click(object sender, RoutedEventArgs e)
    {
        _focusRunning = false;
        _focusLeft = FocusLength;
        UpdateFocusUi();
        UpdateCompact();
        UpdateTickState();
    }

    void FocusFinished()
    {
        _focusRunning = false;
        _focusLeft = FocusLength;
        UpdateCompact();
        UpdateTickState();
        UpdateFocusUi();
        // fullscreen video/game: no sound, no pop-out now; a quiet one once fullscreen ends
        if (_rest == Mode.Hidden) { _timerMissed = DateTime.Now; return; }
        StartAlarm();
        ShowTimerDone(ringing: true);
    }

    // ------------------------------------------------------------------ timer alarm

    DateTime? _timerMissed;
    System.Media.SoundPlayer? _alarm;
    readonly DispatcherTimer _alarmLimit = new() { Interval = TimeSpan.FromSeconds(30) };

    void ShowTimerDone(bool ringing)
    {
        string body = ringing ? "Tap to stop" : $"Finished at {TimeFormat.Time(_timerMissed ?? DateTime.Now)}";
        PopOut(new NoteItem(0, "Timer", $"Timer done · {LengthText(FocusLength)}", body, DateTimeOffset.Now, null,
            Glyph: "", GlyphColor: Color.FromRgb(0xFF, 0x9F, 0x0A),
            Tap: StopAlarm,
            ActionLabel: "Again", Act: () => { StopAlarm(); Focus_Click(this, new RoutedEventArgs()); }, // same length, start again
            StaySeconds: ringing ? 30 : 8,
            Gone: StopAlarm));
    }

    /// <summary>Windows' own alarm tone, looping until the pop-out is tapped or swiped away (30 s at most).
    /// The file is loaded only now and released when it stops.</summary>
    void StartAlarm()
    {
        StopAlarm();
        try
        {
            var wav = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Media\Alarm01.wav");
            if (!System.IO.File.Exists(wav)) { System.Media.SystemSounds.Exclamation.Play(); return; }
            _alarm = new System.Media.SoundPlayer(wav);
            _alarm.PlayLooping();
            _alarmLimit.Tick -= AlarmTimeout;
            _alarmLimit.Tick += AlarmTimeout;
            _alarmLimit.Start();
        }
        catch { System.Media.SystemSounds.Exclamation.Play(); }
    }

    void AlarmTimeout(object? s, EventArgs e) => StopAlarm();

    /// <summary>dev: NOTCHAPP_ALARMTEST=1 rings the timer alarm 5 s after start and hushes it 4 s later.</summary>
    void RunAlarmTest()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            StartAlarm();
            ShowTimerDone(ringing: true);
            var off = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            off.Tick += (_, _) => { off.Stop(); Log.Write($"alarm test: ringing={_alarm != null}"); DismissNotice(); Log.Write($"alarm test: after dismiss ringing={_alarm != null}"); };
            off.Start();
        };
        t.Start();
    }

    void StopAlarm()
    {
        _alarmLimit.Stop();
        if (_alarm == null) return;
        try { _alarm.Stop(); _alarm.Dispose(); } catch { }
        _alarm = null;
    }

    /// <summary>Fullscreen ended: tell about a timer that finished meanwhile (quietly, within 30 minutes).</summary>
    void ShowMissedTimer()
    {
        if (_timerMissed is not { } at || _rest == Mode.Hidden) return;
        if (DateTime.Now - at < TimeSpan.FromMinutes(30)) ShowTimerDone(ringing: false);
        _timerMissed = null;
    }

    void UpdateFocusUi()
    {
        var left = _focusLeft < TimeSpan.Zero ? TimeSpan.Zero : _focusLeft;
        // round up: a timer only shows 00:00 once it's done
        string text = Clock(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)));
        FocusText.Text = "Timer " + text;
        FocusIcon.Text = _focusRunning ? "\uE769" : "\uE768"; // pause / play
        CompactTimer.Text = text;
        bool partWay = _focusLeft != FocusLength;
        // the ruler follows the time left (unless you're dragging it); it can't be dragged mid-run
        _card.TimerRuler.Interactive = !_focusRunning;
        if (!_card.TimerRuler.IsDragging)
        {
            _card.TimerBig.Text = text;
            _card.TimerRuler.Value = left.TotalMinutes;
        }
        _card.TimerStartIcon.Text = _focusRunning ? "\uE769" : "\uE768";
        _card.TimerStartText.Text = _focusRunning ? "Pause" : partWay ? "Resume" : "Start";
        UpdateDockTimer();
    }
}
