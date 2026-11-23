using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NotchApp.Services;

namespace NotchApp;

/// <summary>
/// Floating dock. It floats over everything (no reserved strip). When the window in front
/// reaches down into the dock's area, the dock slides out of the way; pushing the mouse to the
/// bottom edge brings it back. Fullscreen apps hide it.
/// </summary>
public partial class DockWindow : Window
{
    const double StripH = 66;         // window height (bar 48 + margins)
    const double BarH = 48, BarBottom = 10;

    DockPopup? _popup;
    /// <summary>A dock card (quick settings...) is open: its own sliders show the level already.</summary>
    public bool PopupOpen => _popup?.IsVisible == true;
    readonly OutsideTap _popupTap;
    int _bottomPx;                    // bottom of the usable screen area, physical px
    const int SPI_GETWORKAREA = 0x30;
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref RECT r, int winIni);
    readonly DispatcherTimer _popupLeave = new() { Interval = TimeSpan.FromMilliseconds(900) };

    readonly DockApps _apps = new();
    readonly DispatcherTimer _clock = new();
    // 0.5 s: is a window in the way? keep the taskbar hidden. Cheap: a couple of Win32 calls.
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // only while tucked away: is the pointer at the bottom edge?
    readonly DispatcherTimer _edge = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer _leave = new() { Interval = TimeSpan.FromMilliseconds(700) };

    bool _tucked, _peeking;
    public bool KeepTaskbarHidden { get; set; }
    /// <summary>Wi-Fi, Bluetooth, sound... shared with the notch (set by MainWindow).</summary>
    public QuickControls? Controls { get; set; }

    public event Action? PlayPause;

    public DockWindow()
    {
        InitializeComponent();
        _popupTap = new OutsideTap(() => Array.Empty<Rect>(), () => { _popup?.Dismiss(); ClosePreview(); });
        SourceInitialized += (_, _) =>
        {
            WindowUtil.MakeToolWindow(this);
            Place();
        };

        TaskbarHider.WatchStart();
        StartButton.Click += (_, _) =>
        {
            if (TaskbarHider.StartOpenOrJustClosed)
            {
                // Start is open: this tap closes it (Win key toggles). If the tap already closed it, leave it closed.
                if (TaskbarHider.StartIsOpen()) Keys(VK_LWIN);
                return;
            }
            if (KeepTaskbarHidden) TaskbarHider.ShowForStart();
            Keys(VK_LWIN);
        };
        _net.Changed += () => Dispatcher.BeginInvoke(ShowNet);
        QuickButton.Click += (_, _) => TogglePopup(DockPopup.Kind.Controls);
        DesktopButton.Click += (_, _) => { DesktopPeek(false); ToggleDesktop(); };
        // hover a moment: see through every window to the desktop (Windows' own peek); leave: back
        _peekHover.Tick += (_, _) => { _peekHover.Stop(); DesktopPeek(true); };
        DesktopButton.MouseEnter += (_, e) => { if (e.StylusDevice == null) { _peekHover.Stop(); _peekHover.Start(); } };
        DesktopButton.MouseLeave += (_, _) => { _peekHover.Stop(); DesktopPeek(false); };
        TrayButton.Click += (_, _) => TogglePopup(DockPopup.Kind.Background);
        ClockButton.Click += (_, _) => TogglePopup(DockPopup.Kind.Calendar);
        BellButton.Click += (_, _) => TogglePopup(DockPopup.Kind.Notifications);
        _popupLeave.Tick += (_, _) =>
        {
            _popupLeave.Stop();
            if (_popup == null || IsMouseOver || _popup.IsMouseOver) return;
            if (_popup.MenuOpen) { _popupLeave.Start(); return; } // check again once the menu is closed
            _popup.Dismiss();
        };
        MouseLeave += (_, _) => { if (_popup != null) _popupLeave.Start(); };
        MouseEnter += (_, _) => _popupLeave.Stop();
        MediaPlay.Click += (_, _) => PlayPause?.Invoke();

        _apps.Changed += () => { if (_dragging) { _rebuildAfterDrag = true; return; } BuildApps(); CheckOverlap(); };
        InitPreviews(); // after BuildApps' handler: it looks up the freshly built icons
        InitDrag();
        InitPill(); // the home pill turns black over light screens
        // keep the apps fitting when the bar changes (music controls appear, timer...) or the screen does
        Bar.SizeChanged += (_, _) => FitApps();
        SizeChanged += (_, _) => FitApps();
        // a scrolling app row: the mouse wheel moves it sideways
        AppsScroll.PreviewMouseWheel += (_, e) =>
        {
            if (AppsScroll.ScrollableWidth <= 0) return;
            AppsScroll.ScrollToHorizontalOffset(AppsScroll.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        };
        BuildApps();

        _clock.Tick += (_, _) => UpdateClock();
        UpdateClock();

        _watch.Tick += (_, _) =>
        {
            CheckOverlap();
            // the usable area changed (taskbar hidden/shown, posture): move to the new bottom
            var wa = new RECT();
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0) && wa.bottom > 0 && wa.bottom != _bottomPx) Place();
            if (!KeepTaskbarHidden) return;
            TaskbarHider.EnforceHidden();
            // Tablet/tent posture switches Windows to its tablet taskbar and turns auto-hide off,
            // which reserves an empty strip at the bottom. Put auto-hide back.
            // In tablet posture Windows' touch taskbar ignores auto-hide and re-asserts its strip if the
            // work area is changed (that flickers), so leave it alone there. Turning off "Optimize
            // taskbar for touch interactions" (ExpandableTaskbar) keeps the normal, auto-hiding taskbar.
            if (!TaskbarHider.InTabletPosture) TaskbarHider.CheckAutoHideInBackground(); // off the UI thread
        };
        // taskbar shown/hidden, posture change, resolution change: sit at the new bottom
        SystemParameters.StaticPropertyChanged += (_, e) => { if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.InvokeAsync(Place); };
        _watch.Start();
        _edge.Tick += (_, _) => CheckEdge();
        _leave.Tick += (_, _) => { _leave.Stop(); if (!IsMouseOver || _touchPeek) EndPeek(); };
        MouseLeave += (_, _) => { if (_peeking) { _leave.Interval = TimeSpan.FromMilliseconds(700); _leave.Start(); } };
        MouseEnter += (_, _) => { if (!_touchPeek) _leave.Stop(); };
        // any touch on the peeking dock keeps it up a little longer
        PreviewMouseDown += (_, _) => { if (_touchPeek && _leave.Interval.TotalMilliseconds > 1000) { _leave.Stop(); _leave.Start(); } };
        // home handle: a tap (or the start of a swipe up) brings the dock up
        // (no hover-to-open: after a tap the pointer stays parked on the pill, so hover re-opened the
        //  dock every time it tucked away - it bounced. The mouse has the bottom edge for that.)
        Handle.PreviewMouseDown += (_, e) => { Peek(touch: true); e.Handled = true; };
        // the pointer reaching the bottom edge (Windows tries to show its taskbar) -> bring up the dock
        TaskbarHider.EdgeRevealed += () => Dispatcher.InvokeAsync(() => { if (_tucked) Peek(touch: true); });

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;

        // dev: NOTCHAPP_DOCKPOPUP=calendar|notifications opens that card at start (for screenshots)
        if (Environment.GetEnvironmentVariable("NOTCHAPP_DOCKPOPUP") is { } pop)
            Loaded += (_, _) => TogglePopup(pop switch
            {
                "notifications" => DockPopup.Kind.Notifications,
                "background" => DockPopup.Kind.Background,
                "controls" or "bluetooth" or "wifi" or "sound" => DockPopup.Kind.Controls,
                _ => DockPopup.Kind.Calendar,
            });
    }

    void OnDisplayChanged(object? s, EventArgs e) => Dispatcher.InvokeAsync(Place);

    /// <summary>A strip along the very bottom of the primary screen; the bar floats inside it.</summary>
    void Place()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = GetSystemMetrics(0);
        // Bottom of the usable area: the screen's bottom when the Windows taskbar is hidden,
        // just above the taskbar when it's showing (so the dock never sits on top of it).
        var wa = new RECT();
        _bottomPx = SystemParametersInfo(SPI_GETWORKAREA, 0, ref wa, 0) && wa.bottom > 0 ? wa.bottom : GetSystemMetrics(1);
        int sh = (int)Math.Round(StripH * scale);
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, 0, _bottomPx - sh, w, sh, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ calendar / notifications popup

    /// <summary>Opens the card above the dock; clicking the same button again closes it.</summary>
    void TogglePopup(DockPopup.Kind kind)
    {
        ClosePreview();
        var open = _popup;
        if (open != null)
        {
            open.Dismiss();
            _popup = null;
            if (open.Mode == kind) return;
        }
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double screenH = _bottomPx / s;
        var p = new DockPopup(kind, screenH - BarBottom - BarH - 10, Controls);
        // if our card can't do it, open Windows' own panel instead
        p.Fallback += () => Keys(VK_LWIN, kind == DockPopup.Kind.Controls ? (byte)'A' : (byte)'N');
        p.Closed += (_, _) => { if (_popup == p) { _popup = null; _popupTap.Stop(); } };
        p.MouseLeave += (_, _) => _popupLeave.Start();
        p.MouseEnter += (_, _) => _popupLeave.Stop();
        _popup = p;
        _popupTap.Start(); // touch: tap anywhere else closes it
        p.Show();
    }

    // ------------------------------------------------------------------ smart hide

    /// <summary>Tuck the dock away if the window in front overlaps it (or is fullscreen).</summary>
    void CheckOverlap()
    {
        if (_peeking || _popup != null || _preview != null || _dragging) return; // stay put while you're using it
        if (StayUp) return;
        bool inTheWay = ForegroundOverlapsBar(out bool fullscreen);
        _fullscreen = fullscreen;
        Tuck(inTheWay || fullscreen);
        // the home handle lets touch bring the dock back; not over fullscreen video/games
        Handle.Visibility = _tucked && !fullscreen ? Visibility.Visible : Visibility.Collapsed;
        // a different window in front: the pill may now sit on something lighter or darker
        var fg = GetForegroundWindow();
        if (fg != _pillFg) { _pillFg = fg; PillMaybeChanged(); }
    }

    /// <summary>dev: NOTCHAPP_DOCKSTAY=1 never tucks the dock away (for automated tests).</summary>
    static readonly bool StayUp = Environment.GetEnvironmentVariable("NOTCHAPP_DOCKSTAY") == "1";

    bool ForegroundOverlapsBar(out bool fullscreen)
    {
        fullscreen = false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsIconic(fg) || !IsWindowVisible(fg)) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, 64);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow") return false;
        if (DwmGetWindowAttribute(fg, 9, out RECT r, Marshal.SizeOf<RECT>()) != 0) return false;

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        // maximized and tablet-filled windows fill the screen too, but keep their resizable frame; real
        // fullscreen (video, F11, games) drops it. Not "maximized": Chrome maximized -> fullscreen video
        // stays flagged maximized.
        const int WS_THICKFRAME = 0x00040000;
        fullscreen = (GetWindowLong(fg, -16 /*GWL_STYLE*/) & WS_THICKFRAME) == 0
                     && r.left <= 0 && r.top <= 0 && r.right >= sw && r.bottom >= sh
                     && FullscreenCheck.WindowsAgrees(); // screen-sized overlays (Click to Do...) look fullscreen too
        if (fullscreen != _fullscreen) Log.Write($"dock: fullscreen={fullscreen} ({cls})");

        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double bw = Bar.ActualWidth * s;
        double left = (sw - bw) / 2, right = left + bw;
        double bottom = _bottomPx - BarBottom * s, top = bottom - BarH * s;
        return r.left < right && r.right > left && r.top < bottom && r.bottom > top;
    }

    bool _fullscreen; // last known: is a fullscreen app in front (updated by CheckOverlap / Peek)

    void CheckEdge()
    {
        if (_fullscreen) return; // nothing to reveal over fullscreen video: skip the pointer check
        if (!GetCursorPos(out var p)) return;
        int sh = GetSystemMetrics(1);
        // a pointer parked at the edge (a touch leaves it there) must not re-open the dock forever
        if (p.y >= sh - 2 && (p.x != _parked.x || p.y != _parked.y)) Peek(touch: false);
    }

    POINT _parked = new() { x = -1, y = -1 };

    bool _touchPeek;

    /// <summary>Show the tucked dock for a moment. Mouse: stays while the pointer is on it.
    /// Touch: there's no hover, so it stays a few seconds (or until an app is tapped).</summary>
    void Peek(bool touch)
    {
        if (_peeking && !touch) return;
        // fullscreen video/game: the bottom edge belongs to it (YouTube's seek bar lives there)
        ForegroundOverlapsBar(out bool fullscreen);
        _fullscreen = fullscreen;
        if (fullscreen) return;
        _peeking = true;
        _touchPeek = touch;
        Tuck(false);
        Handle.Visibility = Visibility.Collapsed;
        _leave.Stop();
        _leave.Interval = TimeSpan.FromMilliseconds(touch ? 7000 : 700);
        _leave.Start(); // hide again unless the pointer moves onto the dock
    }

    void EndPeek()
    {
        _leave.Stop();
        if (!_peeking) return;
        _peeking = _touchPeek = false;
        GetCursorPos(out _parked); // where the pointer was when the dock went away
        CheckOverlap();
    }

    void Tuck(bool tuck)
    {
        if (tuck == _tucked) return;
        _tucked = tuck;
        double s = 1; // DIPs
        var a = new DoubleAnimation(tuck ? (BarH + BarBottom + 6) * s : 0, TimeSpan.FromMilliseconds(tuck ? 220 : 260))
        {
            EasingFunction = new CubicEase { EasingMode = tuck ? EasingMode.EaseIn : EasingMode.EaseOut },
        };
        BarShift.BeginAnimation(TranslateTransform.YProperty, a);
        if (tuck) _edge.Start(); else if (!_peeking) _edge.Stop();
    }

    public void ApplyTheme()
    {
        Bar.Background = ThemeManager.Island;
        Bar.BorderBrush = new SolidColorBrush(ThemeManager.Edge != null ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
    }

    // ------------------------------------------------------------------ apps

    void BuildApps()
    {
        AppsPanel.Children.Clear();
        foreach (var app in _apps.Apps) AppsPanel.Children.Add(AppButton(app));
        // dev: NOTCHAPP_FAKEAPPS=N adds N copies of the icons, to see a very full dock
        if (int.TryParse(Environment.GetEnvironmentVariable("NOTCHAPP_FAKEAPPS"), out int fake) && _apps.Apps.Count > 0)
            for (int i = 0; i < fake; i++) AppsPanel.Children.Add(AppButton(_apps.Apps[i % _apps.Apps.Count]));
        FitApps();
    }

    const double MinAppScale = 0.6;   // icons never get smaller than this; past it the row scrolls
    const double ScreenEdge = 24;     // keep the bar this far from the screen edges

    /// <summary>
    /// Lots of apps open: shrink the icons so the whole dock (clock, quick settings...) stays on screen,
    /// like the macOS dock. Below <see cref="MinAppScale"/> they stop shrinking and the app row scrolls.
    /// </summary>
    void FitApps()
    {
        int n = AppsPanel.Children.Count;
        if (n == 0 || ActualWidth <= 0) { AppsScale.ScaleX = AppsScale.ScaleY = 1; AppsScroll.MaxWidth = double.PositiveInfinity; return; }
        var first = (FrameworkElement)AppsPanel.Children[0];
        double slot = first.Width is > 0 and var w0 ? w0 : 50;
        slot += first.Margin.Left + first.Margin.Right;
        double natural = n * slot;
        // everything on the bar except the apps row (measured item by item: once the bar is wider than
        // the screen, its own width is reported clipped)
        double others = Bar.Padding.Left + Bar.Padding.Right + Bar.BorderThickness.Left + Bar.BorderThickness.Right;
        if (AppsScroll.Parent is Panel row)
            foreach (UIElement c in row.Children)
                if (c != AppsScroll && c.Visibility != Visibility.Collapsed) others += c.DesiredSize.Width;
        double available = ActualWidth - 2 * ScreenEdge - others;
        if (available <= 0) return;
        double scale = Math.Clamp(available / natural, MinAppScale, 1);
        if (Math.Abs(AppsScale.ScaleX - scale) > 0.005) AppsScale.ScaleX = AppsScale.ScaleY = scale;
        // still too wide at the smallest size: the row scrolls instead of pushing the bar off screen
        bool scrolls = natural * scale > available + 0.5;
        AppsScroll.MaxWidth = scrolls ? available : double.PositiveInfinity;
        // finger panning only when there's something to scroll (otherwise it would fight drag-to-rearrange)
        AppsScroll.PanningMode = scrolls ? PanningMode.HorizontalOnly : PanningMode.None;
    }

    FrameworkElement AppButton(DockApp app)
    {
        var icon = new Image
        {
            Source = app.Icon, Width = 28, Height = 28,
            RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 1),
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var fallback = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = app.Icon == null ? Visibility.Visible : Visibility.Collapsed,
        };
        var indicator = new Rectangle
        {
            Height = 2.5, RadiusX = 1.25, RadiusY = 1.25, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0),
            Width = app.Active ? 16 : 6,
            Fill = (Brush)FindResource(app.Active ? "FgBrush" : "Subtle"),
            Visibility = app.Running ? Visibility.Visible : Visibility.Hidden,
        };

        var grid = new Grid { Width = 50, Height = 42 };
        grid.Children.Add(icon);
        grid.Children.Add(fallback);
        grid.Children.Add(indicator);

        var button = new Button
        {
            Style = (Style)FindResource("DockButton"), Content = grid, Padding = new Thickness(0), Height = 44, Margin = new Thickness(2, 0, 2, 0),
            ToolTip = app.Windows.Count > 1 ? null : app.Name, // several windows: the previews say it
        };
        bool several = app.Windows.Count > 1;
        button.Click += (_, _) =>
        {
            if (_justDragged) { _justDragged = false; return; } // that press was a drag, not a click
            // several windows (e.g. 3 Chrome windows): show their previews to pick from
            if (several)
            {
                _previewHover.Stop();
                if (_preview?.App.Key == app.Key && !_previewByHover) ClosePreview();
                else if (_preview?.App.Key == app.Key) _previewByHover = false; // hovered open, now clicked: keep it
                else OpenPreview(app, button, byHover: false);
                return;
            }
            _apps.Activate(app);
            AfterPick();
        };
        button.PreviewMouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { _apps.Launch(app); e.Handled = true; } };
        button.MouseEnter += (_, _) =>
        {
            Lift(icon, 1.14);
            if (!several) return;
            _previewLeave.Stop();
            if (_preview?.App.Key == app.Key) return;
            _previewHoverTarget = (app, button);
            _previewHover.Stop();
            _previewHover.Interval = TimeSpan.FromMilliseconds(_preview != null ? 120 : 450); // sliding along: switch fast
            _previewHover.Start();
        };
        button.MouseLeave += (_, _) =>
        {
            Lift(icon, 1.0);
            _previewHover.Stop();
            if (_preview != null) _previewLeave.Start();
        };

        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = app.Name, IsEnabled = false });
        var open = new MenuItem { Header = app.Running ? "New window" : "Open" };
        open.Click += (_, _) => _apps.Launch(app);
        menu.Items.Add(open);
        var pin = new MenuItem { Header = app.Pinned ? "Unpin from dock" : "Pin to dock" };
        pin.Click += (_, _) => { if (app.Pinned) _apps.Unpin(app); else _apps.Pin(app); };
        menu.Items.Add(pin);
        if (app.Running)
        {
            var close = new MenuItem { Header = app.Windows.Count > 1 ? "Close all windows" : "Close window" };
            close.Click += (_, _) => _apps.CloseAll(app);
            menu.Items.Add(close);
        }
        button.ContextMenu = menu;
        menu.Opened += (_, _) => { _previewHover.Stop(); ClosePreview(); };

        // drag sideways to move it (the panel follows the pointer from here)
        button.RenderTransform = new TranslateTransform();
        button.Tag = app.Key;
        button.PreviewMouseLeftButtonDown += (_, e) => { _dragBtn = button; _dragStart = e.GetPosition(AppsPanel); };
        return button;
    }

    /// <summary>An app was picked from a peeking dock: tuck away again once it's in front.</summary>
    void AfterPick()
    {
        if (_peeking) { _leave.Stop(); _leave.Interval = TimeSpan.FromMilliseconds(350); _touchPeek = true; _leave.Start(); }
    }

    // ------------------------------------------------------------------ drag to rearrange

    Button? _dragBtn;          // pressed icon (may become a drag)
    Point _dragStart;
    bool _dragging, _justDragged, _rebuildAfterDrag;
    int _dragFrom, _dragTo;
    double _dragSlot;

    void InitDrag()
    {
        AppsPanel.PreviewMouseMove += (_, e) =>
        {
            if (_dragBtn == null) return;
            double dx = e.GetPosition(AppsPanel).X - _dragStart.X;
            if (e.LeftButton != MouseButtonState.Pressed) { _dragBtn = null; return; }
            if (!_dragging)
            {
                if (Math.Abs(dx) < 8) return; // a small wobble is still a click
                BeginDrag();
            }
            DragTo(dx);
        };
        AppsPanel.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (_dragging) { EndDrag(commit: true); e.Handled = true; }
            _dragBtn = null;
        };
        // only the row itself losing the mouse cancels (the icon letting go when the row takes over bubbles here too)
        AppsPanel.LostMouseCapture += (_, e) => { if (_dragging && e.OriginalSource == AppsPanel) EndDrag(commit: false); };
    }

    void BeginDrag()
    {
        _dragging = true;
        _previewHover.Stop();
        ClosePreview();
        var buttons = AppsPanel.Children.OfType<Button>().ToList();
        _dragFrom = _dragTo = buttons.IndexOf(_dragBtn!);
        var m = _dragBtn!.Margin;
        _dragSlot = _dragBtn.ActualWidth + m.Left + m.Right;
        ((TranslateTransform)_dragBtn.RenderTransform).BeginAnimation(TranslateTransform.XProperty, null); // follow the pointer directly
        Panel.SetZIndex(_dragBtn, 1);
        _dragBtn.Opacity = 0.85;
        AppsPanel.CaptureMouse(); // the icon follows the pointer even off the dock; no click at the end
    }

    void DragTo(double dx)
    {
        var buttons = AppsPanel.Children.OfType<Button>().ToList();
        // keep it inside the row of apps
        dx = Math.Clamp(dx, -_dragFrom * _dragSlot, (buttons.Count - 1 - _dragFrom) * _dragSlot);
        ((TranslateTransform)_dragBtn!.RenderTransform).X = dx;
        int to = Math.Clamp(_dragFrom + (int)Math.Round(dx / _dragSlot), 0, buttons.Count - 1);
        if (to == _dragTo) return;
        _dragTo = to;
        // the others slide aside to show where it will land
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i] == _dragBtn) continue;
            double shift = i > _dragFrom && i <= to ? -_dragSlot : i < _dragFrom && i >= to ? _dragSlot : 0;
            Slide(buttons[i], shift);
        }
    }

    void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        _justDragged = true; // swallow a Click if one still arrives (touch)
        Dispatcher.InvokeAsync(() => _justDragged = false, DispatcherPriority.Input);
        var btn = _dragBtn!;
        _dragBtn = null;
        if (AppsPanel.IsMouseCaptured) AppsPanel.ReleaseMouseCapture();
        if (commit && _dragTo != _dragFrom && btn.Tag is string key)
        {
            _rebuildAfterDrag = false;
            _apps.Move(key, _dragTo); // saves the order; the dock rebuilds from its Changed event
        }
        else
        {
            foreach (var b in AppsPanel.Children.OfType<Button>()) Slide(b, 0);
            btn.Opacity = 1;
            Panel.SetZIndex(btn, 0);
        }
        if (_rebuildAfterDrag) { _rebuildAfterDrag = false; BuildApps(); }
        CheckOverlap();
    }

    static void Slide(Button b, double x)
    {
        var t = (TranslateTransform)b.RenderTransform;
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(160)) { EasingFunction = new QuadraticEase() });
    }

    // ------------------------------------------------------------------ window previews

    WindowPreviews? _preview;
    bool _previewByHover;
    (DockApp App, Button Button)? _previewHoverTarget;
    readonly DispatcherTimer _previewHover = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _previewLeave = new() { Interval = TimeSpan.FromMilliseconds(350) };

    void InitPreviews()
    {
        _previewHover.Tick += (_, _) =>
        {
            _previewHover.Stop();
            if (_previewHoverTarget is { } t && t.Button.IsMouseOver) OpenPreview(t.App, t.Button, byHover: true);
        };
        _previewLeave.Tick += (_, _) =>
        {
            _previewLeave.Stop();
            // only hover-opened previews close on their own; a tapped one waits for a pick or a tap elsewhere
            if (_preview != null && _previewByHover && !_preview.IsMouseOver && !AppsPanel.IsMouseOver) ClosePreview();
        };
        // a window opened or closed: keep the previews in step (or close them if one window is left)
        _apps.Changed += () =>
        {
            if (_preview == null) return;
            var app = _apps.Apps.FirstOrDefault(a => a.Key == _preview.App.Key);
            if (app == null || app.Windows.Count < 2) { ClosePreview(); return; }
            if (app.Windows.SequenceEqual(_preview.App.Windows)) return;
            var button = AppsPanel.Children.OfType<Button>().ElementAtOrDefault(_apps.Apps.IndexOf(app));
            if (button != null) OpenPreview(app, button, _previewByHover);
        };
    }

    void OpenPreview(DockApp app, Button button, bool byHover)
    {
        _popup?.Dismiss();
        _previewHover.Stop();
        _previewLeave.Stop();
        var old = _preview;
        _preview = null;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var centre = button.PointToScreen(new Point(button.ActualWidth / 2, 0));
        double screenW = GetSystemMetrics(0) / s;
        double bottom = _bottomPx / s - BarBottom - BarH - 10;
        var p = new WindowPreviews(app, centre.X / s, bottom, screenW);        p.Picked += h => { ClosePreview(); _apps.ActivateWindow(h); AfterPick(); };
        p.CloseRequested += h => _apps.CloseWindow(h); // the list refreshes when the window is gone
        p.MouseEnter += (_, _) => _previewLeave.Stop();
        p.MouseLeave += (_, _) => _previewLeave.Start();
        _preview = p;
        _previewByHover = byHover;
        _popupTap.Start(); // touch: tap anywhere else closes it
        p.Show();
        old?.Close(); // after the new one is up: no gap when sliding from icon to icon
    }

    void ClosePreview()
    {
        _previewHover.Stop();
        _previewLeave.Stop();
        if (_preview == null) return;
        var p = _preview;
        _preview = null;        p.Close();
        if (_popup == null) _popupTap.Stop();
        CheckOverlap();
    }

    static void Lift(Image icon, double to)
    {
        var st = (ScaleTransform)icon.RenderTransform;
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(140)) { EasingFunction = new QuadraticEase() };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    // ------------------------------------------------------------------ media, timer, clock

    public void SetMedia(ImageSource? art, bool playing, bool hasSession)
    {
        MediaPanel.Visibility = hasSession ? Visibility.Visible : Visibility.Collapsed;
        MediaArtBrush.ImageSource = art;
        DockPause.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        DockPlay.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetTimer(string? text)
    {
        TimerPanel.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text != null) TimerText.Text = text;
    }

    bool _muted;
    float _volume = 1;

    public void SetMuted(bool muted) { _muted = muted; ShowVolume(); }

    /// <summary>Windows says the speakers changed (keys, Windows, any app): follow the level.</summary>
    public void SetVolume(bool muted, float level) { _muted = muted; _volume = level; ShowVolume(); }

    /// <summary>Speaker icon by level, like the taskbar: none / one / two / three waves, or crossed out.</summary>
    void ShowVolume()
    {
        string g = _muted || _volume <= 0.005f ? ""
                 : _volume < 0.34f ? ""
                 : _volume < 0.67f ? ""
                 : "";
        if (VolumeGlyph.Text != g) VolumeGlyph.Text = g;
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = TimeFormat.Time(now);
        ClockDate.Text = now.ToString("M/d/yyyy");
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second + 0.05); // next minute boundary
        _clock.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        _clock.Stop(); _watch.Stop(); _edge.Stop(); _leave.Stop(); _popupLeave.Stop(); _popupTap.Stop();
        _popup?.Close();
        _previewHover.Stop(); _previewLeave.Stop();
        _peekHover.Stop(); DesktopPeek(false); // never leave Windows in peek
        _preview?.Close();
        _apps.Dispose();
        _net.Dispose();
        base.OnClosed(e);
    }

    readonly NetStatus _net = new();

    readonly DispatcherTimer _peekHover = new() { Interval = TimeSpan.FromSeconds(1.5) };
    bool _deskPeeking;

    /// <summary>Windows' desktop peek: every window turns to glass so the desktop shows through,
    /// the dock stays solid. The same call the old taskbar's corner used (dwmapi, ordinal 113).</summary>
    void DesktopPeek(bool on)
    {
        if (on == _deskPeeking) return;
        _deskPeeking = on;
        try
        {
            var me = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            DwmpActivateLivePreview(on ? 1 : 0, FindWindow("Progman", null), me, 1 /*desktop*/, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Write("peek: " + ex.Message); }
    }

    [DllImport("dwmapi.dll", EntryPoint = "#113")]
    static extern int DwmpActivateLivePreview(int peekOn, IntPtr peekWindow, IntPtr topmostWindow, uint peekType, IntPtr reserved);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string? cls, string? title);

    /// <summary>Windows' own "show desktop" (the same as Win+D): minimises everything, and a second
    /// tap puts every window back exactly as it was.</summary>
    void ToggleDesktop()
    {
        _popup?.Dismiss();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            shell.ToggleDesktop();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex) { Log.Write("show desktop: " + ex.Message); }
    }

    /// <summary>Wi-Fi bars by signal strength, Ethernet, Wi-Fi without internet, or no internet at all.</summary>
    void ShowNet()
    {
        bool wifi = _net.Kind is NetKind.Wifi;
        NetGlyph.Text = _net.Kind switch
        {
            NetKind.Wifi => _net.Bars switch { >= 4 => "", 3 => "", 2 => "", _ => "" },
            NetKind.WifiNoInternet => "",              // Wi-Fi with a warning sign
            NetKind.Ethernet => "",
            NetKind.EthernetNoInternet => "",
            _ => "",                                    // globe with a no sign: offline
        };
        // the faint full fan only behind partial Wi-Fi bars
        WifiBack.Visibility = wifi && _net.Bars < 4 ? Visibility.Visible : Visibility.Collapsed;
        QuickButton.ToolTip = _net.Kind switch
        {
            NetKind.Wifi => $"{_net.Name} · Quick settings",
            NetKind.WifiNoInternet => $"{_net.Name} · No internet",
            NetKind.Ethernet => "Ethernet · Quick settings",
            NetKind.EthernetNoInternet => "Ethernet · No internet",
            _ => "Not connected · Quick settings",
        };
    }

    // ---- Win32 ----
    const byte VK_LWIN = 0x5B;
    const uint KEYEVENTF_KEYUP = 2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }

    static void Keys(params byte[] vks)
    {
        foreach (var k in vks) keybd_event(k, 0, 0, UIntPtr.Zero);
        foreach (var k in vks.Reverse()) keybd_event(k, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
}
