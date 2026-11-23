using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NotchApp.Services;

namespace NotchApp;

/// <summary>Calendar, notifications or quick-settings card that rises out of the dock, centred above it.</summary>
public partial class DockPopup : Window
{
    public enum Kind { Calendar, Notifications, Controls, Background }
    public Kind Mode { get; }
    readonly QuickControls? _controls;

    readonly double _bottomDip;   // card's bottom edge, in screen DIPs from the top
    DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    bool _closing;
    bool? _qPlaying;

    /// <summary>Our card couldn't do it: the dock opens Windows' own panel instead.</summary>
    public event Action? Fallback;

    public DockPopup(Kind mode, double bottomDip, QuickControls? controls = null)
    {
        Mode = mode;
        _bottomDip = bottomDip;
        _controls = controls;
        InitializeComponent();
        foreach (var sv in new[] { (ScrollViewer)NotesList.Parent, (ScrollViewer)BgList.Parent, (ScrollViewer)BtList.Parent, (ScrollViewer)WifiList.Parent, SoundScroll })
            Motion.RubberBand(sv);
        SourceInitialized += (_, _) => { WindowUtil.MakeToolWindow(this); Place(); };
        Loaded += async (_, _) =>
        {
            Card.Background = ThemeManager.Island; // same glass as the notch
            Card.BorderBrush = new SolidColorBrush(ThemeManager.Edge != null ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            if (mode == Kind.Calendar) BuildCalendar();
            else if (mode == Kind.Background) await LoadBackground();
            else if (mode == Kind.Controls)
            {
                if (_controls == null) { Fallback?.Invoke(); Close(); return; }
                await ShowControls();
            }
            else if (!await LoadNotifications()) { Fallback?.Invoke(); Close(); return; }
            Animate(show: true);
        };
        PrevMonth.Click += (_, _) => { _month = _month.AddMonths(-1); BuildCalendar(); };
        NextMonth.Click += (_, _) => { _month = _month.AddMonths(1); BuildCalendar(); };
        ClearAll.Click += (_, _) => { NotificationsService.ClearAll(); NotesList.Children.Clear(); NotesEmpty.Visibility = Visibility.Visible; };
    }

    void Place()
    {
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int screenW = GetSystemMetrics(0);
        int w = (int)Math.Round(Width * s), h = (int)Math.Round(Height * s);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, (screenW - w) / 2, (int)Math.Round(_bottomDip * s) - h, w, h, 0x14);
    }

    void Animate(bool show)
    {
        // grows out of the dock from 0.95; closing is ~20% quicker than opening
        var d = TimeSpan.FromMilliseconds(show ? 240 : 190);
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, d) { EasingFunction = ease });
        var scale = new DoubleAnimation(show ? 1 : 0.95, d) { EasingFunction = ease };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
        var slide = new DoubleAnimation(show ? 0 : 18, d) { EasingFunction = ease };
        if (!show) slide.Completed += (_, _) => Close();
        CardShift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    /// <summary>Slide back down, then close.</summary>
    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Animate(show: false);
    }

    // ------------------------------------------------------------------ calendar

    void BuildCalendar()
    {
        CalendarView.Visibility = Visibility.Visible;
        NotesView.Visibility = Visibility.Collapsed;
        var now = DateTime.Now;
        BigTime.Text = TimeFormat.Time(now);
        LongDate.Text = now.ToString("dddd, d MMMM yyyy");
        MonthTitle.Text = _month.ToString("MMMM yyyy");

        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        WeekDays.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)first + i) % 7);
            WeekDays.Children.Add(new TextBlock
            {
                Text = CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day)[..2],
                FontSize = 12, Foreground = (Brush)FindResource("Subtle"), HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        Days.Children.Clear();
        int lead = ((int)_month.DayOfWeek - (int)first + 7) % 7;
        for (int i = 0; i < lead; i++) Days.Children.Add(new Border());
        int count = DateTime.DaysInMonth(_month.Year, _month.Month);
        for (int d = 1; d <= count; d++)
        {
            bool today = _month.Year == now.Year && _month.Month == now.Month && d == now.Day;
            Days.Children.Add(new Button
            {
                Content = d.ToString(), Style = (Style)FindResource("DayCell"), Margin = new Thickness(0, 1, 0, 1),
                Background = today ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : Brushes.Transparent,
                Foreground = today ? Brushes.White : (Brush)FindResource("FgBrush"),
                FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
    }

    // ------------------------------------------------------------------ quick settings

    async Task ShowControls()
    {
        var c = _controls!;
        CalendarView.Visibility = Visibility.Collapsed;
        ControlsView.Visibility = Visibility.Visible;
        QBrightness.Glyph = "";
        QVolume.Glyph = "";
        QBrightness.Horizontal = QVolume.Horizontal = true; // macOS-style wide sliders

        await c.Ready();
        SyncToggles();
        SyncMedia();
        QVolume.Value = c.Volume();

        // connectivity
        QWifi.Click += async (_, _) => { await c.SetWifi(QWifi.IsChecked == true); SyncToggles(); };
        QBluetooth.Click += async (_, _) => { await c.SetBluetooth(QBluetooth.IsChecked == true); SyncToggles(); };
        QBluetooth.MouseRightButtonUp += (_, e) => { ShowBluetooth(); e.Handled = true; };
        QBtDevices.Click += (_, _) => ShowBluetooth();
        QMute.Click += (_, _) => { c.SetMuted(QMute.IsChecked == true); SyncToggles(); };
        QMic.Click += (_, _) => { c.SetMicMuted(QMic.IsChecked == true); SyncToggles(); };
        QDnd.Click += (_, _) => { c.SetDoNotDisturb(QDnd.IsChecked == true); SyncToggles(); };
        QSaver.Click += async (_, _) =>
        {
            c.SetBatterySaver(QSaver.IsChecked == true);
            await Task.Delay(600); // Windows takes a moment to switch; show what it actually did
            SyncToggles();
        };

        // media
        QPlay.Click += (_, _) => { c.PlayPause(); Later(SyncMedia); };
        QNext.Click += (_, _) => { c.Next(); Later(SyncMedia); };
        QPrev.Click += (_, _) => { c.Previous(); Later(SyncMedia); };

        // sliders
        QVolume.ValueChanged += v =>
        {
            c.SetVolume((float)v);
            if (QMute.IsChecked == true && v > 0) { QMute.IsChecked = false; c.SetMuted(false); SyncToggles(); }
        };
        QBrightness.ValueChanged += v => c.SetBrightness((int)Math.Round(v * 100));

        // shortcuts
        QSnip.Click += (_, _) => { Dismiss(); Later(() => Keys(0x5B, 0x10, (byte)'S'), 250); }; // Win+Shift+S
        QRotate.Click += (_, _) =>
        {
            bool want = QRotate.IsChecked == true;
            if (!RotationLock.TrySetLocked(want)) { Open("ms-settings:display"); return; } // no switch: open Display settings
            SyncRotation();
        };
        QCalc.Click += (_, _) => Open("calc.exe");
        QCamera.Click += (_, _) => Open("microsoft.windows.camera:");
        QFiles.Click += (_, _) => Open("explorer.exe");
        QAllSettings.Click += (_, _) => Open("ms-settings:");

        // Bluetooth page
        BtBack.Click += (_, _) => { BluetoothView.Visibility = Visibility.Collapsed; ControlsView.Visibility = Visibility.Visible; SyncToggles(); };
        BtPower.Click += async (_, _) => { await c.SetBluetooth(BtPower.IsChecked == true); LoadBluetooth(); };
        BtSettings.Click += (_, _) => Open("ms-settings:bluetooth");

        // Wi-Fi page
        QWifiList.Click += (_, _) => ShowPage(WifiView, () => _ = LoadWifi());
        WifiBack.Click += (_, _) => { ShowPage(ControlsView, null); SyncToggles(); };
        WifiPower.Click += async (_, _) => { await c.SetWifi(WifiPower.IsChecked == true); await LoadWifi(); };
        WifiSettings.Click += (_, _) => Open("ms-settings:network-wifi");

        // Speakers & microphones page
        QSoundDevices.Click += (_, _) => ShowPage(SoundView, LoadSound);
        SoundBack.Click += (_, _) => { ShowPage(ControlsView, null); SyncToggles(); };
        SoundSettings.Click += (_, _) => Open("ms-settings:sound");

        // brightness is a slower query (WMI); fill it in when it arrives
        var level = await c.Brightness();
        QBrightness.IsEnabled = level != null;
        QBrightness.Opacity = level != null ? 1 : 0.4;
        QBrightness.Value = (level ?? 0) / 100.0;

        switch (Environment.GetEnvironmentVariable("NOTCHAPP_DOCKPOPUP")) // dev: open a page directly
        {
            case "bluetooth": ShowBluetooth(); break;
            case "wifi": ShowPage(WifiView, () => _ = LoadWifi()); break;
            case "sound": ShowPage(SoundView, LoadSound); break;
        }
    }

    void SyncToggles()
    {
        var c = _controls!;
        QWifi.IsEnabled = c.WifiOn() != null;
        QWifi.IsChecked = c.WifiOn() == true;
        QBluetooth.IsEnabled = c.BluetoothOn() != null;
        QBluetooth.IsChecked = c.BluetoothOn() == true;
        QMute.IsChecked = c.Muted();
        QMic.IsEnabled = c.HasMic();
        QMic.IsChecked = c.MicMuted();

        // macOS-style status lines under each title
        QWifiSub.Text = c.WifiOn() == true ? WifiName() ?? "On" : c.WifiOn() == false ? "Off" : "Unavailable";
        QBtSub.Text = c.BluetoothOn() == true ? ConnectedAudioDevice() ?? "On" : c.BluetoothOn() == false ? "Off" : "Unavailable";
        QMicSub.Text = !c.HasMic() ? "None" : c.MicMuted() ? "Muted" : "On";
        QMuteSub.Text = c.Muted() ? "Muted" : "On";
        var dnd = c.DoNotDisturb();
        QDnd.IsEnabled = dnd != null;
        QDnd.IsChecked = dnd == true;
        QDndSub.Text = dnd == null ? "Set up in Settings" : dnd == true ? "On" : "Off";
        QDnd.ToolTip = dnd == null ? "Turn on notifications through the notch in Settings" : dnd == true ? "On · notifications wait quietly" : "Off";
        var saver = c.BatterySaverOn();
        QSaver.IsEnabled = saver != null;
        QSaver.IsChecked = saver == true;
        QSaverSub.Text = saver == null ? "No battery" : saver == true ? "On" : BatterySaver.PluggedIn ? "Off · plugged in" : "Off";
        QMuteGlyph.Text = c.Muted() ? "" : "";
        SyncRotation();
    }

    /// <summary>White when rotation is locked (like Control Center).</summary>
    void SyncRotation()
    {
        bool supported = RotationLock.Supported, locked = RotationLock.Locked;
        QRotate.IsEnabled = supported;
        QRotate.IsChecked = locked;
        QRotate.ToolTip = !supported ? "Rotation isn't available on this screen" : locked ? "Rotation locked" : "Rotation unlocked";
    }

    /// <summary>The Wi-Fi network you're on (or the profile name if Windows won't share it).</summary>
    static string? WifiName()
    {
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (p == null || !p.IsWlanConnectionProfile) return null;
            var ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid();
            return string.IsNullOrWhiteSpace(ssid) ? p.ProfileName : ssid;
        }
        catch { return null; }
    }

    static string? ConnectedAudioDevice()
    {
        try { return BluetoothAudio.List().FirstOrDefault(d => d.Connected)?.Name; }
        catch { return null; }
    }

    void SyncMedia()
    {
        var m = _controls!.NowPlaying();
        QTitle.Text = m.HasSession && m.Title.Length > 0 ? m.Title : "Not playing";
        QArtist.Text = m.HasSession ? m.Artist : "";
        QArt.ImageSource = m.HasSession ? m.Art : null;
        bool playing = m.HasSession && m.Playing;
        if (_qPlaying != playing)
        {
            UIElement show = playing ? QPause : QPlayGlyph, hide = playing ? QPlayGlyph : QPause;
            if (_qPlaying == null) { show.Visibility = Visibility.Visible; hide.Visibility = Visibility.Collapsed; }
            else Motion.Swap(hide, show); // crossfade + blur
            _qPlaying = playing;
        }
        // nothing loaded: controls dim (and do nothing)
        QPrev.Opacity = QPlay.Opacity = QNext.Opacity = m.HasSession ? 1 : 0.35;
        QPrev.IsEnabled = QPlay.IsEnabled = QNext.IsEnabled = m.HasSession;
    }

    void Open(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        Dismiss();
    }

    void Later(Action a, int ms = 400)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    // ------------------------------------------------------------------ Bluetooth devices

    void ShowBluetooth()
    {
        ControlsView.Visibility = Visibility.Collapsed;
        BluetoothView.Visibility = Visibility.Visible;
        LoadBluetooth();
    }

    /// <summary>On the UI thread on purpose: these audio COM objects can't be used from a background thread.</summary>
    void LoadBluetooth()
    {
        var c = _controls!;
        BtPower.IsEnabled = c.BluetoothOn() != null;
        BtPower.IsChecked = c.BluetoothOn() == true;
        BtList.Children.Clear();
        if (c.BluetoothOn() != true)
        {
            BtEmpty.Text = "Bluetooth is off.";
            BtEmpty.Visibility = Visibility.Visible;
            return;
        }
        var devices = BluetoothAudio.List();
        foreach (var d in devices) BtList.Children.Add(DeviceRow(d));
        Motion.Stagger(BtList);
        BtEmpty.Text = "No paired headphones or speakers. Pair one in Bluetooth settings first.";
        BtEmpty.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement DeviceRow(BtAudioDevice d)
    {
        var blue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        var icon = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(19),
            Background = d.Connected ? blue : (Brush)FindResource("ChipOff"),
            Child = new TextBlock
            {
                Text = d.IsHeadset ? "" : "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 16,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var status = new TextBlock { Text = d.Connected ? "Connected" : "Not connected", FontSize = 12, Foreground = (Brush)FindResource("Subtle") };
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = d.Name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(status);

        var action = new Button
        {
            Style = (Style)FindResource("CardButton"), VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = d.Connected ? "Disconnect" : "Connect", FontSize = 13 },
        };
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            status.Text = d.Connected ? "Disconnecting…" : "Connecting…";
            if (d.Connected) await BluetoothAudio.Disconnect(d); else await BluetoothAudio.Connect(d);
            // the radio link takes a few seconds; check back until it changes (or give up after ~8 s)
            for (int i = 0; i < 8; i++)
            {
                await Task.Delay(1000);
                var now = BluetoothAudio.List().FirstOrDefault(x => x.Name == d.Name);
                if (now != null && now.Connected != d.Connected) break;
            }
            LoadBluetooth();
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(action, 2); grid.Children.Add(action);
        return new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)FindResource("Card"), Child = grid,
        };
    }

    void ShowPage(FrameworkElement page, Action? load)
    {
        foreach (var v in new FrameworkElement[] { ControlsView, BluetoothView, WifiView, SoundView })
            v.Visibility = v == page ? Visibility.Visible : Visibility.Collapsed;
        load?.Invoke();
    }

    // ------------------------------------------------------------------ Wi-Fi networks

    async Task LoadWifi()
    {
        var c = _controls!;
        WifiPower.IsEnabled = c.WifiOn() != null;
        WifiPower.IsChecked = c.WifiOn() == true;
        WifiList.Children.Clear();
        if (c.WifiOn() != true) { WifiEmpty.Text = "Wi-Fi is off."; WifiEmpty.Visibility = Visibility.Visible; return; }
        WifiEmpty.Text = "Looking for networks…";
        WifiEmpty.Visibility = Visibility.Visible;
        await ShowWifiList();          // what Windows already knows, straight away
        WifiNetworks.Scan();           // then a fresh scan, like Windows' own panel
        await Task.Delay(4000);
        if (WifiView.Visibility == Visibility.Visible) await ShowWifiList();
    }

    async Task ShowWifiList()
    {
        var nets = await WifiNetworks.ListAsync();
        WifiList.Children.Clear();
        foreach (var n in nets.Take(20)) WifiList.Children.Add(WifiRow(n));
        Motion.Stagger(WifiList);
        WifiEmpty.Text = "No networks found.";
        WifiEmpty.Visibility = nets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement WifiRow(WifiNetwork n)
    {
        var blue = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
        // signal: Segoe Wi-Fi bars glyphs, strongest to weakest
        string bars = n.Signal >= 75 ? "" : n.Signal >= 50 ? "" : n.Signal >= 25 ? "" : "";
        var icon = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(17),
            Background = n.Connected ? blue : (Brush)FindResource("ChipOff"),
            Child = new TextBlock { Text = bars, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var status = new TextBlock
        {
            FontSize = 12, Foreground = (Brush)FindResource("Subtle"),
            Text = n.Connected ? "Connected" : n.Saved ? "Saved" : n.Secured ? "Secured" : "Open",
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = n.Ssid, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 170 });
        if (n.Secured) title.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 10, Foreground = (Brush)FindResource("Subtle"), Margin = new Thickness(6, 3, 0, 0) });
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(status);

        var action = new Button
        {
            Style = (Style)FindResource("CardButton"), VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = n.Connected ? "Disconnect" : "Connect", FontSize = 13 },
        };
        // password box for a new secured network (the card takes the keyboard only while typing)
        var password = new PasswordBox
        {
            Visibility = Visibility.Collapsed, Margin = new Thickness(46, 8, 0, 0), Padding = new Thickness(8, 5, 8, 5),
            Background = (Brush)FindResource("ChipOff"), Foreground = (Brush)FindResource("FgBrush"), BorderThickness = new Thickness(0),
            CaretBrush = (Brush)FindResource("FgBrush"), FontSize = 13, ToolTip = "Wi-Fi password, then Enter",
        };

        async Task Run(Func<Task> work, string busy)
        {
            action.IsEnabled = false;
            status.Text = busy;
            await work();
            await Task.Delay(2500); // the link takes a moment to settle
            WindowUtil.SetActivatable(this, false);
            SyncToggles();
            await LoadWifi();
        }

        action.Click += async (_, _) =>
        {
            if (n.Connected) await Run(WifiNetworks.Disconnect, "Disconnecting…");
            else if (n.Saved || !n.Secured) await Run(() => n.Saved ? WifiNetworks.Connect(n.Ssid) : WifiNetworks.ConnectNew(n, null), "Connecting…");
            else
            {
                password.Visibility = Visibility.Visible;
                WindowUtil.SetActivatable(this, true);
                Activate();
                password.Focus();
            }
        };
        password.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { password.Visibility = Visibility.Collapsed; WindowUtil.SetActivatable(this, false); }
            if (e.Key != Key.Enter || password.Password.Length == 0) return;
            var pw = password.Password;
            password.Clear();
            password.Visibility = Visibility.Collapsed;
            await Run(() => WifiNetworks.ConnectNew(n, pw), "Connecting…");
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(action, 2); grid.Children.Add(action);
        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(password);
        return new Border
        {
            CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)FindResource("Card"), Child = stack,
        };
    }

    // ------------------------------------------------------------------ speakers & microphones

    void LoadSound()
    {
        Fill(OutputList, AudioDevices.Outputs(), "");
        Fill(InputList, AudioDevices.Inputs(), "");
    }

    void Fill(StackPanel list, List<AudioDevice> devices, string glyph)
    {
        list.Children.Clear();
        if (devices.Count == 0)
        {
            list.Children.Add(new TextBlock { Text = "None connected", FontSize = 13, Foreground = (Brush)FindResource("Subtle"), Margin = new Thickness(4, 0, 0, 6) });
            return;
        }
        foreach (var d in devices)
        {
            var row = new Grid { Cursor = Cursors.Hand };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
                Background = d.IsDefault ? new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)) : (Brush)FindResource("ChipOff"),
                Child = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 14, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var name = new TextBlock { Text = d.Name, FontSize = 13.5, Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1); row.Children.Add(name);
            if (d.IsDefault)
            {
                var check = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 2); row.Children.Add(check);
            }
            var item = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(8, 7, 12, 7), Margin = new Thickness(0, 0, 0, 6), Background = (Brush)FindResource("Card"), Child = row };
            item.MouseEnter += (_, _) => item.Background = (Brush)FindResource("CardHover");
            item.MouseLeave += (_, _) => item.Background = (Brush)FindResource("Card");
            item.MouseLeftButtonUp += (_, _) =>
            {
                if (!d.IsDefault && !AudioDevices.SetDefault(d.Id)) { Open("ms-settings:sound"); return; }
                LoadSound();
            };
            list.Children.Add(item);
        }
        Motion.Stagger(list);
    }

    // ------------------------------------------------------------------ background apps

    /// <summary>A right-click menu is open (the pointer is on the menu, not the card: don't close).</summary>
    public bool MenuOpen { get; private set; }

    async Task LoadBackground()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        BgView.Visibility = Visibility.Visible;
        var apps = await Task.Run(TrayApps.Running); // a quick scan of running programs, off the UI thread
        BgList.Children.Clear();
        foreach (var a in apps) BgList.Children.Add(BgRow(a));
        Motion.Stagger(BgList);
        UpdateBgCount();
    }

    void UpdateBgCount()
    {
        int n = BgList.Children.Count;
        BgCount.Text = n == 0 ? "" : n == 1 ? "1 app" : $"{n} apps";
        BgEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    FrameworkElement BgRow(TrayApp app)
    {
        var subtle = (Brush)FindResource("Subtle");
        var icon = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7),
            Background = app.Icon != null ? new ImageBrush(app.Icon) { Stretch = Stretch.Uniform } : (Brush)FindResource("ChipOff"),
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = new TextBlock { Text = app.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var sub = new TextBlock { Text = System.IO.Path.GetFileName(app.Exe), FontSize = 11.5, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(name);
        text.Children.Add(sub);

        var quit = new Button
        {
            Style = (Style)FindResource("IconButton"), Content = "", Width = 30, Height = 30, FontSize = 11,
            Foreground = subtle, ToolTip = $"Quit {app.Name}", VerticalAlignment = VerticalAlignment.Center,
            Visibility = app.System ? Visibility.Collapsed : Visibility.Visible,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        Grid.SetColumn(quit, 2); grid.Children.Add(quit);

        var row = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 8, 6, 8), Margin = new Thickness(0, 0, 0, 6),
            Background = (Brush)FindResource("Card"), Child = grid, Cursor = Cursors.Hand,
            ToolTip = "Click to open · right-click for more",
        };
        var hover = (Brush)FindResource("CardHover");
        var card = (Brush)FindResource("Card");
        row.MouseEnter += (_, _) => row.Background = hover;
        row.MouseLeave += (_, _) => row.Background = card;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, quit)) return;
            if (row.Child != grid) return; // asking "Quit?" right now
            TrayApps.Open(app);
            Dismiss();
        };
        quit.Click += (_, e) => { e.Handled = true; AskQuit(app, row, grid); };

        // right-click: Lagoon's menu for it (Windows 11 only shows an app's own tray menu from the taskbar itself)
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = app.Name, IsEnabled = false });
        var open = new MenuItem { Header = "Open" };
        open.Click += (_, _) => { TrayApps.Open(app); Dismiss(); };
        menu.Items.Add(open);
        var closeWins = new MenuItem { Header = "Close its windows" };
        closeWins.Click += (_, _) => TrayApps.CloseWindows(app);
        menu.Items.Add(closeWins);
        if (!app.System)
        {
            var q = new MenuItem { Header = $"Quit {app.Name}…" };
            q.Click += (_, _) => AskQuit(app, row, grid);
            menu.Items.Add(q);
        }
        menu.Items.Add(new Separator());
        var folder = new MenuItem { Header = "Open file location" };
        folder.Click += (_, _) => TrayApps.ShowInFolder(app);
        menu.Items.Add(folder);
        menu.Opened += (_, _) => { MenuOpen = true; closeWins.IsEnabled = TrayApps.HasWindows(app); };
        menu.Closed += (_, _) => MenuOpen = false;
        row.ContextMenu = menu;
        return row;
    }

    /// <summary>The row turns into "Quit Discord?  [Quit] [Cancel]": nothing ends without a yes.</summary>
    void AskQuit(TrayApp app, Border row, Grid normal)
    {
        var ask = new TextBlock
        {
            Text = $"Quit {app.Name}?", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0, 8, 0),
        };
        TextBlock Label(string s) => new() { Text = s, FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var yes = new Button { Content = Label("Quit"), Style = (Style)FindResource("CardButton"), Width = 66, Height = 32, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)) };
        var no = new Button { Content = Label("Cancel"), Style = (Style)FindResource("CardButton"), Width = 70, Height = 32, Margin = new Thickness(6, 0, 0, 0), Background = (Brush)FindResource("ChipOff") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        var g = new Grid { MinHeight = 34 };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(ask);
        Grid.SetColumn(buttons, 1); g.Children.Add(buttons);
        row.Child = g;

        no.Click += (_, e) => { e.Handled = true; row.Child = normal; };
        yes.Click += async (_, e) =>
        {
            e.Handled = true;
            yes.IsEnabled = no.IsEnabled = false;
            bool ok = await Task.Run(() => TrayApps.Quit(app));
            if (ok)
            {
                BgList.Children.Remove(row);
                UpdateBgCount();
            }
            else
            {
                ask.Text = $"Couldn't quit {app.Name} (it needs admin)";
                ask.FontWeight = FontWeights.Normal;
                buttons.Children.Remove(yes);
                no.Content = Label("OK");
                no.IsEnabled = true;
            }
        };
    }

    static bool IsInside(DependencyObject d, DependencyObject container)
    {
        for (var x = d; x != null; x = x is Visual ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == container) return true;
        return false;
    }

    // ------------------------------------------------------------------ notifications

    async Task<bool> LoadNotifications()
    {
        CalendarView.Visibility = Visibility.Collapsed;
        NotesView.Visibility = Visibility.Visible;
        var notes = await NotificationsService.GetAsync();
        if (notes == null) return false;
        NotesList.Children.Clear();
        foreach (var n in notes.Take(30)) NotesList.Children.Add(NoteCard(n));
        Motion.Stagger(NotesList);
        NotesEmpty.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    FrameworkElement NoteCard(NoteItem n)
    {
        var subtle = (Brush)FindResource("Subtle");
        var logo = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top,
            Background = n.Logo != null ? new ImageBrush(n.Logo) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("ChipOff"),
        };

        var head = new Grid();
        head.Children.Add(new TextBlock { Text = n.App, FontSize = 12, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 60, 0) });
        var when = new TextBlock { Text = Ago(n.Time), FontSize = 12, Foreground = subtle, HorizontalAlignment = HorizontalAlignment.Right };
        head.Children.Add(when);

        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        text.Children.Add(head);
        if (n.Title.Length > 0) text.Children.Add(new TextBlock { Text = n.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
        if (n.Body.Length > 0) text.Children.Add(new TextBlock { Text = n.Body, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 38, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(logo);
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var card = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 8),
            Background = (Brush)FindResource("Card"), Child = grid, ToolTip = "Click to dismiss", Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) => when.Text = "Dismiss ✕";
        card.MouseLeave += (_, _) => when.Text = Ago(n.Time);
        card.MouseLeftButtonUp += (_, _) =>
        {
            NotificationsService.Remove(n.Id);
            NotesList.Children.Remove(card);
            if (NotesList.Children.Count == 0) NotesEmpty.Visibility = Visibility.Visible;
        };
        return card;
    }

    static string Ago(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}h ago";
        return t.LocalDateTime.ToString("d MMM");
    }

    static void Keys(params byte[] vks)
    {
        foreach (var k in vks) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in vks.Reverse()) keybd_event(k, 0, 2, UIntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
}
