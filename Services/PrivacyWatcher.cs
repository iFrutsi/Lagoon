using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace NotchApp.Services;

public sealed record Usage(string App, DateTime Since);

/// <summary>
/// Knows which app is using the microphone, camera or screen capture right now.
/// Windows logs this per app under CapabilityAccessManager\ConsentStore (the same data behind
/// the mic/camera icons in the taskbar): LastUsedTimeStart is set and LastUsedTimeStop is 0 while in use.
/// We ask Windows to signal us when that registry branch changes, so there is no polling at all.
/// </summary>
public sealed class PrivacyWatcher : IDisposable
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;
    readonly System.Windows.Threading.DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _thread;
    volatile bool _stopping;

    public Usage? Mic { get; private set; }
    public Usage? Camera { get; private set; }
    public Usage? Screen { get; private set; }
    public bool AnyInUse => Mic != null || Camera != null || Screen != null;

    public event Action? Changed;

    // second source for the mic: open capture streams, checked every 2 s (a few microseconds)
    readonly System.Windows.Threading.DispatcherTimer _sessions = new() { Interval = TimeSpan.FromSeconds(2) };
    string? _sessionApp;
    DateTime _sessionSince;

    public PrivacyWatcher()
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); Scan(); };
        _sessions.Tick += (_, _) =>
        {
            var app = MicSessions.ActiveApp();
            if (app == _sessionApp) return;
            if (app != null && _sessionApp == null) _sessionSince = DateTime.Now;
            _sessionApp = app;
            Scan();
        };
        _sessions.Start();
        _thread = new Thread(WatchLoop) { IsBackground = true, Name = "PrivacyWatcher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Scan();
    }

    void WatchLoop()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Root);
        if (key == null) return;
        while (!_stopping)
        {
            // Async notification; re-armed after every change.
            if (RegNotifyChangeKeyValue(key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET,
                    _signal.SafeWaitHandle, true) != 0) return;
            _signal.WaitOne();
            if (_stopping) return;
            _ui.InvokeAsync(() => { _debounce.Stop(); _debounce.Start(); });
        }
    }

    void Scan()
    {
        // privacy record first; if it shows nothing, an open capture stream still counts
        var mic = Find("microphone") ?? (_sessionApp != null ? new Usage(_sessionApp, _sessionSince) : null);
        var cam = Find("webcam");
        var screen = Find("graphicsCaptureProgrammatic") ?? Find("graphicsCaptureWithoutBorder");
        if (mic == Mic && cam == Camera && screen == Screen) return;
        Mic = mic; Camera = cam; Screen = screen;
        Log.Write($"privacy: mic={mic?.App} cam={cam?.App} screen={screen?.App}");
        Changed?.Invoke();
    }

    /// <summary>The app that has been using this capability the longest, or null if none is.</summary>
    static Usage? Find(string capability)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{capability}");
            if (key == null) return null;
            Usage? best = null;
            Check(key, packaged: true, ref best);
            using var np = key.OpenSubKey("NonPackaged");
            if (np != null) Check(np, packaged: false, ref best);
            return best;
        }
        catch { return null; }
    }

    static void Check(RegistryKey parent, bool packaged, ref Usage? best)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStart") is not long start || start <= 0) continue;
            if (app.GetValue("LastUsedTimeStop") is long stop && stop != 0) continue;

            string? path = packaged ? null : name.Replace('#', '\\');
            // A crashed desktop app can leave Stop at 0 forever: only trust it if it's still running.
            if (path != null && !IsRunning(path)) continue;

            var since = DateTime.FromFileTime(start);
            if (best == null || since < best.Since)
                best = new Usage(packaged ? PackagedName(name) : DesktopName(path!), since);
        }
    }

    static bool IsRunning(string exePath)
    {
        var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath));
        bool any = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        return any;
    }

    // "5319275A.WhatsAppDesktop_cv1g1gvanyjgm" -> "WhatsApp", "Microsoft.WindowsCamera_8wek..." -> "Camera"
    static string PackagedName(string id)
    {
        var s = id.Split('_')[0];
        s = s[(s.LastIndexOf('.') + 1)..];
        if (s.StartsWith("Windows") && s.Length > 7) s = s[7..];
        if (s.EndsWith("Desktop") && s.Length > 7) s = s[..^7];
        return s;
    }

    // "C:\Program Files\Google\Chrome\Application\chrome.exe" -> "Google Chrome"
    static string DesktopName(string path)
    {
        try
        {
            var desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        _sessions.Stop();
        _stopping = true;
        _signal.Set();
    }

    const int REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool watchSubtree, int filter,
        SafeWaitHandle evt, bool asynchronous);
}
