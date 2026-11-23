using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotchApp.Services;

/// <param name="AppId">The app's AppUserModelID: opens it via shell:AppsFolder, and is the
/// key of its notification settings (banners) in the registry.</param>
/// <param name="Glyph">Lagoon's own pop-outs (earbuds connected...): an icon glyph instead of an app logo.</param>
/// <param name="Tap">Lagoon's own pop-outs: what a tap does (instead of opening an app).</param>
/// <param name="ActionLabel">Optional button on the right (e.g. "Copy", "Show"), running <paramref name="Act"/>.</param>
/// <param name="GlyphColor">Background of the icon tile for <paramref name="Glyph"/> (default blue).</param>
public sealed record NoteItem(uint Id, string App, string Title, string Body, DateTimeOffset Time, ImageSource? Logo,
    string AppId = "", string? Glyph = null, Action? Tap = null, string? ActionLabel = null, Action? Act = null, Color? GlyphColor = null,
    int StaySeconds = 5, Action? Gone = null); // Gone: runs when it leaves the notch (any way)

/// <summary>Reads your Windows notifications (the ones in the notification centre).</summary>
public static class NotificationsService
{
    /// <summary>null = not allowed / not available (fall back to Windows' own panel).</summary>
    public static Task<List<NoteItem>?> GetAsync() => ReadAsync(_ => true);

    /// <summary>Reads the notification centre; app logos only for the ids <paramref name="wantLogo"/> picks
    /// (fetching a logo is the slow part).</summary>
    public static async Task<List<NoteItem>?> ReadAsync(Func<uint, bool> wantLogo)
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed
                && await listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
                return null;

            var result = new List<NoteItem>();
            foreach (var n in (await listener.GetNotificationsAsync(NotificationKinds.Toast)).OrderByDescending(n => n.CreationTime))
            {
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                string title = texts?.FirstOrDefault()?.Text ?? "";
                string body = texts == null ? "" : string.Join(" ", texts.Skip(1).Select(t => t.Text));
                string app = "", appId = "";
                ImageSource? logo = null;
                try
                {
                    app = n.AppInfo.DisplayInfo.DisplayName;
                    appId = n.AppInfo.AppUserModelId ?? "";
                    if (wantLogo(n.Id))
                    {
                        using var ras = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48)).OpenReadAsync();
                        using var s = ras.AsStreamForRead();
                        var ms = new MemoryStream();
                        await s.CopyToAsync(ms);
                        ms.Position = 0;
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.DecodePixelWidth = 48; bmp.EndInit();
                        bmp.Freeze();
                        logo = bmp;
                    }
                }
                catch { }
                result.Add(new NoteItem(n.Id, app, title, body, n.CreationTime, logo, appId));
            }
            return result;
        }
        catch (Exception ex)
        {
            Log.Write("notifications: " + ex.Message);
            return null;
        }
    }

    /// <summary>Just the ids in the notification centre: the cheap "anything new?" check.</summary>
    public static async Task<List<uint>?> IdsAsync()
    {
        try
        {
            var listener = UserNotificationListener.Current;
            if (listener.GetAccessStatus() != UserNotificationListenerAccessStatus.Allowed) return null;
            return (await listener.GetNotificationsAsync(NotificationKinds.Toast)).Select(n => n.Id).ToList();
        }
        catch { return null; }
    }

    public static void Remove(uint id) { try { UserNotificationListener.Current.RemoveNotification(id); } catch { } }
    public static void ClearAll() { try { UserNotificationListener.Current.ClearNotifications(); } catch { } }
}

/// <summary>
/// Tells us when a new notification arrives, without polling: the shell signals every change
/// to its notification centre, and we then read only what is new.
/// (A minute-by-minute look is the only safety net.)
/// </summary>
public sealed class NoteWatcher : IDisposable
{
    public event Action<List<NoteItem>>? Arrived;

    readonly HashSet<uint> _seen = new();

    Timer? _debounce;
    bool _usingEvent, _reading, _again;

    public async Task<bool> StartAsync()
    {
        var all = await NotificationsService.ReadAsync(_ => false);
        if (all == null) return false; // not allowed: nothing to show
        foreach (var n in all) _seen.Add(n.Id); // what's already there isn't "new"

        _debounce = new Timer(_ => _ = CheckAsync());
        try
        {
            // only works for Store-packaged apps; Lagoon isn't one, so this normally throws
            UserNotificationListener.Current.NotificationChanged += OnChanged;
            _usingEvent = true;
            Log.Write("notes: using Windows' change event");
        }
        catch
        {
            // The shell's own "notification centre changed" signal (WNF_SHEL_NOTIFICATIONS): fires once per
            // new notification (and when some are cleared), costs nothing in between.
            _wnfCallback = OnWnf;
            int status = RtlSubscribeWnfStateChangeNotification(out _wnf, WNF_SHEL_NOTIFICATIONS, 0, _wnfCallback, IntPtr.Zero, IntPtr.Zero, 0, 0);
            if (status == 0)
            {
                Log.Write("notes: listening to the shell's notification signal");
                // safety net in case a Windows update changes that signal: one cheap look a minute
                _poll = new Timer(_ => _ = PollAsync(), null, 60_000, 60_000);
            }
            else
            {
                Log.Write($"notes: shell signal unavailable ({status:X}), checking every 4 s");
                _wnf = IntPtr.Zero;
                _poll = new Timer(_ => _ = PollAsync(), null, 4000, 4000);
            }
        }
        return true;
    }

    Timer? _poll;
    IntPtr _wnf;
    WnfCallback? _wnfCallback; // kept alive while subscribed

    int OnWnf(ulong state, uint stamp, IntPtr type, IntPtr ctx, IntPtr buffer, uint size)
    {
        if (!Paused) Poke();
        return 0;
    }

    const ulong WNF_SHEL_NOTIFICATIONS = 0x0D83063EA3BC1035;
    delegate int WnfCallback(ulong state, uint stamp, IntPtr type, IntPtr ctx, IntPtr buffer, uint size);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int RtlSubscribeWnfStateChangeNotification(out IntPtr subscription, ulong state, uint changeStamp, WnfCallback callback, IntPtr context, IntPtr typeId, uint serializationGroup, uint unknown);
    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int RtlUnsubscribeWnfStateChangeNotification(IntPtr subscription);

    /// <summary>Pause the 2 s check while nothing would be shown anyway (do not disturb, fullscreen).</summary>
    public bool Paused { get; set; }

    async Task PollAsync()
    {
        if (Paused || _reading) return;
        var ids = await NotificationsService.IdsAsync();
        if (ids != null && ids.Any(id => !_seen.Contains(id))) await CheckAsync();
    }

    void OnChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind == UserNotificationChangedKind.Added) Poke();
    }

    /// <summary>Several writes come in a burst: read once they settle.</summary>
    void Poke() => _debounce?.Change(250, Timeout.Infinite);

    async Task CheckAsync()
    {
        if (_reading) { _again = true; return; }
        _reading = true;
        try
        {
            do
            {
                _again = false;
                var fresh = await NotificationsService.ReadAsync(id => !_seen.Contains(id));
                if (fresh == null) return;
                var added = fresh.Where(n => _seen.Add(n.Id)).OrderBy(n => n.Time).ToList();
                // keep the seen-list from growing forever: only ids still in the centre matter
                _seen.IntersectWith(fresh.Select(n => n.Id));
                if (added.Count > 0) Arrived?.Invoke(added);
            } while (_again);
        }
        catch (Exception ex) { Log.Write("notes check: " + ex.Message); }
        finally { _reading = false; }
    }

    public void Dispose()
    {
        if (_usingEvent) try { UserNotificationListener.Current.NotificationChanged -= OnChanged; } catch { }
        if (_wnf != IntPtr.Zero) { RtlUnsubscribeWnfStateChangeNotification(_wnf); _wnf = IntPtr.Zero; }
        _poll?.Dispose();
        _debounce?.Dispose();
    }
}

/// <summary>
/// While notifications come through the notch, Windows' own pop-up banners are switched off per app
/// (HKCU\...\Notifications\Settings\&lt;app id&gt;\ShowBanner = 0): notifications still go to the
/// notification centre, which is where we read them. Every original value is saved first and put
/// back exactly when the feature is turned off or Lagoon quits.
/// </summary>
public static class NotificationBanners
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const int Missing = -1;

    /// <summary>Switch off the banners of every app Windows knows about.</summary>
    public static void HideAll(AppSettings s)
    {
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            if (root == null) return;
            foreach (var app in root.GetSubKeyNames()) Hide(s, app, save: false);
            s.Save();
        }
        catch (Exception ex) { Log.Write("banners: " + ex.Message); }
    }

    /// <summary>One app (e.g. one we hadn't seen before just sent its first notification).</summary>
    public static void Hide(AppSettings s, string appId, bool save = true)
    {
        if (string.IsNullOrEmpty(appId)) return;
        s.BannerRestore ??= new();
        if (s.BannerRestore.ContainsKey(appId)) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"{Key}\{appId}");
            int before = k.GetValue("ShowBanner") is int v ? v : Missing;
            s.BannerRestore[appId] = before; // remembered before we change anything
            if (save) s.Save();
            k.SetValue("ShowBanner", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (Exception ex) { Log.Write("banner " + appId + ": " + ex.Message); }
    }

    /// <summary>Put every banner setting back the way it was.</summary>
    public static void RestoreAll(AppSettings s)
    {
        if (s.BannerRestore == null) return;
        foreach (var (appId, before) in s.BannerRestore)
        {
            try
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{Key}\{appId}", writable: true);
                if (k == null) continue;
                if (before == Missing) k.DeleteValue("ShowBanner", throwOnMissingValue: false);
                else k.SetValue("ShowBanner", before, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }
        }
        s.BannerRestore = null;
        s.Save();
    }
}
