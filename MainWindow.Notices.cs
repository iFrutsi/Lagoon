using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NotchApp.Services;

namespace NotchApp;

/// <summary>
/// Notifications through the notch: each new notification slides out of the notch (app icon,
/// name, title, text). Tap to open the app, swipe up to dismiss, hover to keep it up. Several at
/// once show one after another. On a call the call timer stays visible inside the notification.
/// Windows' own banners are switched off meanwhile (see NotificationBanners) and put back after.
/// Do not disturb: nothing pops out; notifications wait quietly in the notification centre.
/// </summary>
public partial class MainWindow
{
    NoteWatcher? _notes;
    readonly Queue<NoteItem> _noticeQueue = new();
    NoteItem? _notice;
    bool _swipedNotice;
    readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    async void InitNotices()
    {
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); if (!Hovered) NextNotice(); else _noticeTimer.Start(); };
        _card.NoticeView.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_swipedNotice) { _swipedNotice = false; return; } // the end of a swipe, not a tap
            OpenNoticeApp();
        };
        _card.NoticeView.PreviewMouseLeftButtonDown += (_, _) => _swipedNotice = false;
        _card.NoticeAction.Click += (_, e) =>
        {
            e.Handled = true;
            var n = _notice;
            DismissNotice(); // on to the next one, if any
            try { n?.Act?.Invoke(); } catch (Exception ex) { Log.Write("pop-out action: " + ex.Message); }
        };
        // the button's release must not also count as a tap on the pop-out
        _card.NoticeAction.PreviewMouseLeftButtonUp += (_, _) => _swipedNotice = true;
        _card.CardIsland.MouseEnter += (_, _) => { if (_mode == Mode.Notice) _noticeTimer.Stop(); };
        _card.CardIsland.MouseLeave += (_, _) => { if (_mode == Mode.Notice) { _noticeTimer.Interval = TimeSpan.FromSeconds(2.5); _noticeTimer.Start(); } };
        UpdateDndUi();
        await ApplyNoticeSettings();
    }

    /// <summary>Start or stop to match Settings (also called when you flip the switch).</summary>
    async Task ApplyNoticeSettings()
    {
        if (_settings.NotchNotifications)
        {
            if (_notes == null)
            {
                var w = new NoteWatcher();
                w.Arrived += list => Dispatcher.BeginInvoke(() => OnNotesArrived(list));
                if (!await w.StartAsync()) { w.Dispose(); Log.Write("notes: no access to notifications"); return; }
                _notes = w;
                UpdateNotesPaused();
            }
            NotificationBanners.HideAll(_settings); // Windows' own pop-ups off; the centre still gets everything
        }
        else
        {
            _notes?.Dispose();
            _notes = null;
            _noticeQueue.Clear();
            if (_mode == Mode.Notice) EndNotices();
            NotificationBanners.RestoreAll(_settings);
        }
    }

    void OnNotesArrived(List<NoteItem> list)
    {
        foreach (var n in list)
        {
            // an app we hadn't seen yet: its first banner may already be on screen; switch it off for next time
            NotificationBanners.Hide(_settings, n.AppId);
            if (_settings.DoNotDisturb) continue;
            if (n.Title.Length == 0 && n.Body.Length == 0) continue;
            _noticeQueue.Enqueue(n);
        }
        while (_noticeQueue.Count > 6) _noticeQueue.Dequeue(); // a burst: keep the latest few
        if (_mode != Mode.Notice) TryShowQueued();
        else UpdateNoticeMore();
    }

    /// <summary>Shows the next waiting notification if the notch is free (not open, not over fullscreen).</summary>
    void TryShowQueued()
    {
        if (_noticeQueue.Count == 0 || _mode is Mode.Expanded or Mode.Notice) return;
        if (_rest == Mode.Hidden) { _noticeQueue.Clear(); return; } // fullscreen video/game: leave them in the centre
        NextNotice();
    }

    void NextNotice()
    {
        // drop ones that waited too long (e.g. while the card was open)
        while (_noticeQueue.Count > 0 && DateTimeOffset.Now - _noticeQueue.Peek().Time > TimeSpan.FromMinutes(2)) _noticeQueue.Dequeue();
        if (_noticeQueue.Count == 0) { EndNotices(); return; }
        LeaveNotice(); // the previous one is going
        _notice = _noticeQueue.Dequeue();
        var n = _notice;
        _card.NoticeApp.Text = n.App;
        _card.NoticeTitle.Text = n.Title.Length > 0 ? n.Title : n.Body;
        _card.NoticeBody.Text = n.Title.Length > 0 ? n.Body : "";
        _card.NoticeBody.Visibility = _card.NoticeBody.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _card.NoticeIcon.Background = n.Logo != null ? new ImageBrush(n.Logo) { Stretch = Stretch.UniformToFill } : (Brush)FindResource("ChipOff");
        // Lagoon's own (earbuds...): an icon; no logo: the app's first letter
        _card.NoticeIcon.Child = n.Glyph != null
            ? new System.Windows.Controls.TextBlock
            {
                Text = n.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }
            : n.Logo != null || n.App.Length == 0 ? null : new System.Windows.Controls.TextBlock
            {
                Text = n.App[..1].ToUpperInvariant(), FontSize = 18, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        if (n.Glyph != null) _card.NoticeIcon.Background = new SolidColorBrush(n.GlyphColor ?? Color.FromRgb(0x0A, 0x84, 0xFF));
        _card.NoticeActionText.Text = n.ActionLabel ?? "";
        _card.NoticeAction.Visibility = n.ActionLabel != null && n.Act != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateNoticeMore();
        UpdateNoticeCall();
        _toastTimer.Stop();
        _noticeTimer.Interval = TimeSpan.FromSeconds(Math.Max(2, n.StaySeconds));
        _noticeTimer.Stop();
        _noticeTimer.Start();
        if (_mode != Mode.Notice) SetMode(Mode.Notice);
        else
        {
            // next one: a little pop so it reads as new
            Services.Motion.Stagger(_card.NoticeView);
        }
    }

    /// <summary>One of Lagoon's own pop-outs (code copied, download done...). Shares the queue with
    /// notifications, so they never overlap; skipped over fullscreen like the rest.</summary>
    void PopOut(NoteItem item)
    {
        _noticeQueue.Enqueue(item);
        if (_mode == Mode.Notice) UpdateNoticeMore(); else TryShowQueued();
    }

    void UpdateNoticeMore() => _card.NoticeMore.Text = _noticeQueue.Count > 0 ? $"+{_noticeQueue.Count}" : "now";

    /// <summary>On a call the timer must stay visible: show it inside the notification.</summary>
    void UpdateNoticeCall()
    {
        bool onCall = _privacy?.Mic != null && CallTimer.Visibility == Visibility.Visible;
        _card.NoticeCall.Visibility = onCall ? Visibility.Visible : Visibility.Collapsed;
        if (onCall) _card.NoticeCallText.Text = CallTimer.Text;
        _card.NoticeMore.Visibility = onCall ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The pop-out on screen is leaving (timed out, swiped, tapped, replaced): let it clean up.</summary>
    void LeaveNotice()
    {
        var gone = _notice?.Gone;
        if (gone == null) return;
        try { gone(); } catch { }
    }

    void EndNotices()
    {
        _noticeTimer.Stop();
        LeaveNotice();
        _notice = null;
        if (_mode == Mode.Notice) SetMode(_rest);
    }

    /// <summary>Swipe up / dismissed: on to the next one, if any.</summary>
    void DismissNotice()
    {
        _noticeTimer.Stop();
        NextNotice();
    }

    void OpenNoticeApp()
    {
        var n = _notice;
        _noticeQueue.Clear();
        EndNotices();
        if (n?.Tap != null) { try { n.Tap(); } catch (Exception ex) { Log.Write("pop-out tap: " + ex.Message); } return; }
        if (n?.Glyph != null) // earbuds pop-out: Bluetooth settings
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
            return;
        }
        if (n == null || n.AppId.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(@"shell:AppsFolder\" + n.AppId) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open notice app: " + ex.Message); }
    }

    // ------------------------------------------------------------------ do not disturb

    void ToggleDnd()
    {
        _settings.DoNotDisturb = !_settings.DoNotDisturb;
        _settings.Save();
        if (_settings.DoNotDisturb) { _noticeQueue.Clear(); if (_mode == Mode.Notice) EndNotices(); }
        UpdateDndUi();
        ShowToast("", _settings.DoNotDisturb ? "Do not disturb on" : "Do not disturb off",
                  _settings.DoNotDisturb ? Color.FromRgb(0xBF, 0x5A, 0xF2) : ThemeManager.Foreground);
    }

    /// <summary>Nothing would pop out during do not disturb or over a fullscreen app: no need to check.</summary>
    void UpdateNotesPaused()
    {
        if (_notes != null) _notes.Paused = _settings.DoNotDisturb || _rest == Mode.Hidden;
    }

    void UpdateDndUi()
    {
        UpdateNotesPaused();
        DndMenu.IsChecked = _settings.DoNotDisturb;
        DndMenu.Visibility = _settings.NotchNotifications ? Visibility.Visible : Visibility.Collapsed;
        CompactDnd.Visibility = _settings.DoNotDisturb && _settings.NotchNotifications ? Visibility.Visible : Visibility.Collapsed;
        UpdateCompact();
    }

    void Dnd_Click(object sender, RoutedEventArgs e) => ToggleDnd();

    /// <summary>Lagoon was opened again (Start menu) while already running: show it's here.</summary>
    internal void SayRunning()
    {
        KeepOnTop();
        if (_mode == Mode.Notice) EndNotices();
        ShowToast("", "Lagoon is running", Color.FromRgb(0x30, 0xD1, 0x58));
    }

    /// <summary>Settings changed the switches.</summary>
    internal async void NoticeSettingsChanged()
    {
        UpdateDndUi();
        await ApplyNoticeSettings();
    }
}
