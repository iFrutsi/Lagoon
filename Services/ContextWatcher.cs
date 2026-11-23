using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace NotchApp.Services;

public enum Surroundings { Desktop, App, Fullscreen }

/// <summary>
/// Works out what is under the notch: the desktop, an app window (e.g. Chrome's tab strip),
/// or a fullscreen app. Uses Windows event hooks for instant updates on focus/minimize/move,
/// plus a very cheap poll of the foreground window's rect to catch maximize and fullscreen toggles.
/// </summary>
public sealed class ContextWatcher : IDisposable
{
    readonly IntPtr _own;
    readonly WinEventProc _proc; // kept in a field so the GC never collects the callback
    readonly IntPtr _hook;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    IntPtr _lastFg;
    RECT _lastFgRect;
    int _lastFgFrame;

    public Surroundings Current { get; private set; } = Surroundings.Desktop;
    public event Action<Surroundings>? Changed;
    /// <summary>Another window came to the front / moved (settled): a moment to make sure we're still on top.</summary>
    public event Action? WindowsMoved;

    public ContextWatcher(IntPtr ownWindow)
    {
        _own = ownWindow;
        _proc = OnWinEvent;
        // 0x03 foreground ... 0x17 minimize-end: covers focus changes, move/size end, minimize/restore.
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _debounce.Tick += (_, _) => { _debounce.Stop(); WindowsMoved?.Invoke(); Evaluate(); };
        _poll.Tick += (_, _) => PollForeground();
        _fullscreenConfirm.Tick += (_, _) => { _fullscreenConfirm.Stop(); Evaluate(); };
        _poll.Start();
        Evaluate();
    }

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return; // only whole windows
        _debounce.Stop();
        _debounce.Start();
    }

    void PollForeground()
    {
        var fg = GetForegroundWindow();
        Bounds(fg, out var r);
        // the frame too: a maximized window going fullscreen (YouTube in maximized Chrome) keeps
        // exactly the same on-screen size, only its frame goes away
        int frame = GetWindowLong(fg, -16 /*GWL_STYLE*/) & 0x00040000 /*WS_THICKFRAME*/;
        if (fg == _lastFg && r.Equals(_lastFgRect) && frame == _lastFgFrame) return;
        _lastFg = fg;
        _lastFgRect = r;
        _lastFgFrame = frame;
        Evaluate();
    }

    DateTime _fullscreenSeen = DateTime.MinValue;
    readonly DispatcherTimer _fullscreenConfirm = new() { Interval = TimeSpan.FromMilliseconds(550) };

    public void Evaluate()
    {
        var result = Compute();
        Log.Write($"surroundings: {result} (was {Current})");

        // Only hide for fullscreen once it has lasted half a second: window switches and
        // animations briefly put screen-sized windows in front, which made the notch blink out.
        if (result == Surroundings.Fullscreen && Current != Surroundings.Fullscreen)
        {
            if (_fullscreenSeen == DateTime.MinValue) _fullscreenSeen = DateTime.Now;
            if (DateTime.Now - _fullscreenSeen < TimeSpan.FromMilliseconds(500))
            {
                _fullscreenConfirm.Stop();
                _fullscreenConfirm.Start();
                return;
            }
        }
        if (result != Surroundings.Fullscreen) _fullscreenSeen = DateTime.MinValue;

        if (result == Current) return;
        Current = result;
        Changed?.Invoke(result);
    }

    /// <summary>Windows' own overlays and transient shell surfaces: never "an app under the notch".</summary>
    static bool IsShellSurface(string cls) => cls is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
        or "XamlExplorerHostIslandWindow"      // Alt+Tab, Task View, Snap layouts
        or "MultitaskingViewFrame" or "TaskSwitcherWnd" or "ForegroundStaging"
        or "Windows.UI.Core.CoreWindow"        // Start, Search, Action Center, notifications
        or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland"
        or "LockScreenControllerProxyWindow";

    Surroundings Compute()
    {
        var zone = NotchZone();
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _own && IsFullscreenOnPrimary(fg)) return Surroundings.Fullscreen;

        var result = Surroundings.Desktop;
        // EnumWindows walks top-level windows front-to-back: the first real window under the notch wins.
        EnumWindows((h, _) =>
        {
            if (h == _own || !IsWindowVisible(h) || IsIconic(h) || IsCloaked(h)) return true;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & (WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT)) != 0) return true; // overlays, tooltips
            var cls = ClassName(h);
            if (cls is "Progman" or "WorkerW") return false;               // reached the desktop
            if (IsShellSurface(cls)) return true;                          // overlays: look past them
            if (!Bounds(h, out var r)) return true;
            if (r.Left < zone.Right && r.Right > zone.Left && r.Top < zone.Bottom && r.Bottom > zone.Top)
            {
                result = Surroundings.App;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>The strip at the top centre of the primary screen where the notch lives, in physical pixels.</summary>
    RECT NotchZone()
    {
        double scale = Math.Max(1, GetDpiForWindow(_own)) / 96.0;
        int cx = GetSystemMetrics(SM_CXSCREEN) / 2;
        int half = (int)(150 * scale);
        return new RECT { Left = cx - half, Top = 0, Right = cx + half, Bottom = (int)(36 * scale) };
    }

    static bool IsFullscreenOnPrimary(IntPtr h)
    {
        if (IsShellSurface(ClassName(h)) || IsCloaked(h)) return false;
        // Maximized and tablet-filled windows cover the whole screen too (the taskbar is hidden), but
        // they keep their resizable frame; real fullscreen (video, F11, games) drops it. Don't go by
        // "maximized": Chrome maximized -> fullscreen video stays flagged maximized.
        if ((GetWindowLong(h, -16 /*GWL_STYLE*/) & 0x00040000 /*WS_THICKFRAME*/) != 0) return false;
        if (!Bounds(h, out var r)) return false;
        var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref info) || (info.dwFlags & MONITORINFOF_PRIMARY) == 0) return false;
        var m = info.rcMonitor;
        if (!(r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)) return false;
        bool agrees = FullscreenCheck.WindowsAgrees(); // screen-sized overlays look fullscreen too
        Log.Write($"fullscreen-shaped window in front: {ClassName(h)}; Windows says fullscreen: {agrees}");
        return agrees;
    }

    static bool Bounds(IntPtr h, out RECT r)
    {
        r = default;
        if (h == IntPtr.Zero) return false;
        // Visible frame, without the invisible resize borders GetWindowRect includes.
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf<RECT>()) == 0) return true;
        return GetWindowRect(h, out r);
    }

    static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose()
    {
        _poll.Stop();
        _debounce.Stop();
        _fullscreenConfirm.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    // ---- Win32 ----

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const int SM_CXSCREEN = 0;
    const uint MONITOR_DEFAULTTONEAREST = 2, MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
