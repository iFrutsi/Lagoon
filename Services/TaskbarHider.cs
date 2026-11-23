using System.Runtime.InteropServices;
using System.Text;

namespace NotchApp.Services;

/// <summary>
/// Fully hides the Windows taskbar while the dock is on. Auto-hide alone isn't enough (it pops
/// back at the screen edge), so we also hide the taskbar windows themselves; auto-hide stays on
/// underneath so apps get the full screen height. Restore() puts everything back exactly.
/// </summary>
public static class TaskbarHider
{
    static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) yield return h;
    }

    /// <summary>Start opens only when the taskbar is on screen: un-hide it (auto-hide keeps it tucked
    /// away); the watchdog hides it again after Start closes.</summary>
    public static void ShowForStart()
    {
        if (StartIsOpen()) return; // this press closes Start: nothing to show the taskbar for
        _startGraceUntil = DateTime.Now.AddSeconds(2); // give Start time to open before re-hiding (set first: the show hook checks it)
        foreach (var h in TaskbarWindows()) ShowWindow(h, SW_SHOWNA);
        CentreStartSoon();
    }

    static DateTime _startGraceUntil;

    /// <summary>Start is open, or closed just now (a tap on the dock takes focus away from Start, which
    /// closes it before the tap's Click arrives). Either way a Start press now means "close", not "open".</summary>
    public static bool StartOpenOrJustClosed => StartIsOpen() || DateTime.Now - _startClosedAt < TimeSpan.FromMilliseconds(600);

    static DateTime _startClosedAt;
    static bool _startFg;
    static IntPtr _fgHook;
    static readonly WinEventProc _onFg = OnForeground;

    /// <summary>Foreground changes are rare, so this hook is effectively free.</summary>
    public static void WatchStart()
    {
        if (_fgHook == IntPtr.Zero)
            _fgHook = SetWinEventHook(0x0003 /*EVENT_SYSTEM_FOREGROUND*/, 0x0003, IntPtr.Zero, _onFg, 0, 0, 0);
    }

    static void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        bool start = StartIsOpen();
        bool closed = _startFg && !start, opened = !_startFg && start;
        _startFg = start;
        // Start opened any way at all (key, dock, touch gesture): see-through taskbar under it, centred
        if (opened && _showHook != IntPtr.Zero) { SetInvisible(true); CentreStartSoon(); }
        if (!closed) return;
        _startClosedAt = DateTime.Now;
        // Start just closed: tuck the taskbar away now rather than on the next watchdog tick
        if (_showHook != IntPtr.Zero) { _startGraceUntil = default; EnforceHidden(); }
        SetInvisible(false); // after hiding, so it never flashes back in between
    }

    // ---- see-through taskbar under an open Start. The taskbar isn't normally layered (exstyle 0x88),
    //      so clearing WS_EX_LAYERED is always an exact restore. Cleared on Start close, Hide() and Restore(). ----

    static bool _invisible;

    static void SetInvisible(bool on)
    {
        if (on == _invisible) return;
        _invisible = on;
        // changing another process's window style waits for that process: never on the UI thread
        var windows = TaskbarWindows().ToArray();
        // turning it back to normal: give the (async) hide a moment to land first, or the taskbar
        // flashes up as it becomes opaque again
        lock (_styleLock) _styleQueue = _styleQueue.ContinueWith(_ =>
        {
            if (!on) Thread.Sleep(300);
            ApplyInvisible(windows, on);
        }, TaskScheduler.Default);
    }

    static readonly object _styleLock = new();
    static Task _styleQueue = Task.CompletedTask; // keeps on/off changes in order

    static void ApplyInvisible(IntPtr[] windows, bool on)
    {
        foreach (var h in windows)
        {
            int ex = GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
            if (on)
            {
                SetWindowLong(h, -20, ex | WS_EX_LAYERED);
                SetLayeredWindowAttributes(h, 0, 0, 0x2 /*LWA_ALPHA*/);
            }
            else if ((ex & WS_EX_LAYERED) != 0) SetWindowLong(h, -20, ex & ~WS_EX_LAYERED);
        }
    }

    public static bool StartIsOpen()
    {
        var fg = GetForegroundWindow();
        var cls = new StringBuilder(32);
        GetClassName(fg, cls, 32);
        if (cls.ToString() != "Windows.UI.Core.CoreWindow") return false;
        var title = new StringBuilder(16);
        GetWindowText(fg, title, 16);
        return title.ToString() is "Search" or "Start";
    }

    // ---- Start centring: with the taskbar hidden Explorer anchors Start to the left edge no matter
    //      what TaskbarAl says, so once Start's window appears we slide it to the middle ourselves.
    //      Short-lived: polls every 40ms for at most ~1.2s after a Win press, then stops. ----

    static Timer? _centreTimer;
    static int _centreTicks;

    static void CentreStartSoon()
    {
        _centreTicks = 0;
        _centreTimer ??= new Timer(_ => CentreTick());
        _centreTimer.Change(40, 40);
    }

    static void CentreTick()
    {
        if (++_centreTicks > 30) { _centreTimer?.Change(Timeout.Infinite, Timeout.Infinite); return; }
        var fg = GetForegroundWindow();
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, 64);
        if (cls.ToString() != "Windows.UI.Core.CoreWindow") return;
        var title = new StringBuilder(64);
        GetWindowText(fg, title, 64);
        if (title.ToString() is not ("Search" or "Start")) return;
        if (!GetWindowRect(fg, out var r)) return;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(fg, 2 /*NEAREST*/), ref mi)) return;
        int w = r.Right - r.Left;
        int x = mi.rcWork.Left + (mi.rcWork.Right - mi.rcWork.Left - w) / 2;
        if (Math.Abs(r.Left - x) > 2)
            SetWindowPos(fg, IntPtr.Zero, x, r.Top, 0, 0, 0x0001 /*NOSIZE*/ | 0x0004 /*NOZORDER*/ | 0x0010 /*NOACTIVATE*/);
        // NB: don't hide the taskbar while Start is open: Start then stops closing on touches outside it.
        // Make it see-through instead: it's still there for Windows, but you only see the dock.
        if (_showHook != IntPtr.Zero) SetInvisible(true);
        // keep checking until the timer runs out: Start can re-anchor itself while it animates in
    }

    // ---- instant re-hide: Explorer re-shows the taskbar now and then (edge hover, notifications).
    //      Instead of waiting for the 0.5s watchdog, Windows tells us the moment it shows.
    //      Event-driven, out-of-context, filtered to Explorer's process: no polling cost. ----

    static IntPtr _showHook;
    static uint _showPid;
    static bool _watching; // Hide() asked for the hook (it may not exist yet: Explorer still starting)
    static readonly WinEventProc _onShow = OnShow; // kept alive for the hook's lifetime

    static void WatchShows()
    {
        _watching = true;
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return; // Explorer isn't up yet (early boot): EnforceHidden retries
        GetWindowThreadProcessId(tray, out uint pid);
        if (_showHook != IntPtr.Zero && pid == _showPid) return;
        // first time, or Explorer restarted (new process): hook the new one
        if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
        _showPid = pid;
        _showHook = SetWinEventHook(0x8002 /*EVENT_OBJECT_SHOW*/, 0x8002, IntPtr.Zero, _onShow, pid, 0, 0 /*OUTOFCONTEXT*/);
        if (_showHook != IntPtr.Zero)
        {
            // a new Explorer comes up with its taskbar showing, and auto-hide may need re-applying
            AppBar.TaskbarAutoHide = true;
            foreach (var h in TaskbarWindows()) ShowWindow(h, SW_HIDE);
        }
    }

    static void StopWatchingShows()
    {
        _watching = false;
        if (_showHook == IntPtr.Zero) return;
        UnhookWinEvent(_showHook);
        _showHook = IntPtr.Zero;
        _showPid = 0;
    }

    static void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 /*OBJID_WINDOW*/ || hwnd == IntPtr.Zero) return;
        var cls = new StringBuilder(32);
        GetClassName(hwnd, cls, 32);
        if (cls.ToString() is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")) return;
        // Explorer slid the auto-hidden taskbar up on its own: the pointer hit the bottom edge, or (touch)
        // a swipe up from the bottom. Hide it, and let the dock take its place.
        bool forStart = DateTime.Now < _startGraceUntil || StartIsOpen();
        EnforceHidden();
        if (!forStart) EdgeRevealed?.Invoke();
    }

    /// <summary>The user asked for the taskbar (bottom edge / swipe up): show the dock instead.</summary>
    public static event Action? EdgeRevealed;

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // ---- tablet / tent posture: Windows swaps in its touch taskbar, ignores the auto-hide request
    //      and reserves a strip (30px here) at the bottom even though we keep the taskbar hidden.
    //      Give that strip back to apps by setting the work area to the full monitor ourselves. ----

    /// <summary>Windows turned the taskbar's auto-hide off (it does on some posture/settings changes):
    /// put it back. Asking Explorer waits for Explorer, so this runs on a worker thread, one at a time.</summary>
    public static void CheckAutoHideInBackground()
    {
        if (Interlocked.Exchange(ref _autoHideChecking, 1) == 1) return; // previous check still waiting
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (!AppBar.TaskbarAutoHide)
                {
                    Log.Write("taskbar auto-hide was turned off; re-applying");
                    AppBar.TaskbarAutoHide = true;
                    foreach (var h in TaskbarWindows()) ShowWindow(h, SW_HIDE);
                }
            }
            catch { }
            finally { Volatile.Write(ref _autoHideChecking, 0); }
        });
    }

    static int _autoHideChecking;

    public static bool InTabletPosture => GetSystemMetrics(0x2003 /*SM_CONVERTIBLESLATEMODE*/) == 0;

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);

    const int WS_EX_LAYERED = 0x80000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

    /// <summary>Explorer only applies the alignment while the taskbar is showing: show it, apply, hide.</summary>
    public static async Task ApplyCentredAsync()
    {
        _startGraceUntil = DateTime.Now.AddSeconds(3); // our own show: not the user asking for the taskbar
        foreach (var h in TaskbarWindows()) ShowWindow(h, SW_SHOWNA);
        Alignment = 0; // nudge: Explorer ignores a "change" to the value it already has
        await Task.Delay(400);
        Alignment = 1;
        await Task.Delay(1200);
        foreach (var h in TaskbarWindows()) ShowWindow(h, SW_HIDE);
    }

    public static void Hide()
    {
        AppBar.TaskbarAutoHide = true;
        foreach (var h in TaskbarWindows()) ShowWindow(h, SW_HIDE);
        _invisible = true; SetInvisible(false); // clean up if a previous run was killed with Start open
        WatchShows();
    }

    /// <summary>Show the taskbar again and put auto-hide back to how it was.</summary>
    public static void Restore(bool autoHideBefore)
    {
        StopWatchingShows();
        // quitting: put the taskbar's normal look back right now (not queued), before the process ends
        lock (_styleLock) _styleQueue.Wait(2000);
        _invisible = false;
        ApplyInvisible(TaskbarWindows().ToArray(), false);
        foreach (var h in TaskbarWindows()) ShowWindow(h, SW_SHOWNA);
        AppBar.TaskbarAutoHide = autoHideBefore;
    }

    /// <summary>Windows sometimes re-shows it (Start, Quick settings...). Hide it again once those are closed.</summary>
    public static void EnforceHidden()
    {
        if (_watching) WatchShows(); // cheap: re-hooks only if Explorer started late or restarted
        if (DateTime.Now < _startGraceUntil) return; // Start is about to open
        // don't fight the Start menu or a shell flyout that's open right now
        var fg = GetForegroundWindow();
        var cls = new StringBuilder(64);
        GetClassName(fg, cls, 64);
        // (not the taskbar itself: Windows often hands it focus when Start closes, and leaving it alone
        //  then kept the old taskbar on screen until you clicked something else)
        bool anyVisible = false;
        foreach (var h in TaskbarWindows()) anyVisible |= IsWindowVisible(h);
        if (!anyVisible) return;
        if (cls.ToString() is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
        {
            Log.Write($"taskbar showing; left alone for {cls}");
            return;
        }
        Log.Write($"taskbar showing; hiding it (in front: {cls})");
        foreach (var h in TaskbarWindows())
            if (IsWindowVisible(h)) ShowWindow(h, SW_HIDE);
    }

    // ---- taskbar alignment: Start and Search open above the Start button's position, so while
    //      the dock is on we centre it (Start/Search open in the middle, above the dock) ----

    const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>0 = left, 1 = centre (Windows 11 default when the value is missing).</summary>
    public static int Alignment
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return k?.GetValue("TaskbarAl") is int v ? v : 1;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);
            if (k == null || (k.GetValue("TaskbarAl") is int cur && cur == value)) return;
            k.SetValue("TaskbarAl", value, Microsoft.Win32.RegistryValueKind.DWord);
            // tell Explorer to pick it up now
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "TraySettings", 0x2 /*ABORTIFHUNG*/, 1000, out _);
        }
    }

    /// <summary>
    /// Windows 11 has a separate "hide the taskbar when using the device as a tablet" setting that
    /// replaces normal auto-hide in tablet/tent posture. If it's off, the taskbar reserves an empty
    /// strip there even though we hide it. 1 = hide in tablet mode.
    /// </summary>
    public static int TabletAutoHide
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return k?.GetValue("TaskbarAutoHideInTabletMode") is int v ? v : 0;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);
            if (k == null || (k.GetValue("TaskbarAutoHideInTabletMode") is int cur && cur == value)) return;
            k.SetValue("TaskbarAutoHideInTabletMode", value, Microsoft.Win32.RegistryValueKind.DWord);
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "TraySettings", 0x2, 1000, out _);
        }
    }

    /// <summary>
    /// "Optimize taskbar for touch interactions when this device is used as a tablet" (ExpandableTaskbar).
    /// When on, tablet posture swaps in a touch taskbar that ignores auto-hide and reserves a strip.
    /// 1 = on, 0 = off, -1 = value not set (Windows default). Setting -1 deletes the value.
    /// Takes effect at the next posture change.
    /// </summary>
    public static int TouchTaskbar
    {
        get
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey);
            return k?.GetValue("ExpandableTaskbar") is int v ? v : -1;
        }
        set
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AdvancedKey, writable: true);
            if (k == null) return;
            var cur = k.GetValue("ExpandableTaskbar") is int c ? c : -1;
            if (cur == value) return;
            if (value < 0) k.DeleteValue("ExpandableTaskbar", false);
            else k.SetValue("ExpandableTaskbar", value, Microsoft.Win32.RegistryValueKind.DWord);
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A /*WM_SETTINGCHANGE*/, IntPtr.Zero, "TraySettings", 0x2, 1000, out _);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    const int SW_HIDE = 0, SW_SHOWNA = 8;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    // ShowWindowAsync: the taskbar belongs to Explorer. Plain ShowWindow on another process's window
    // waits until Explorer answers, so a busy Explorer froze Lagoon with it. Async posts the request
    // and returns at once (same result, Explorer applies it as soon as it can).
    [DllImport("user32.dll", EntryPoint = "ShowWindowAsync")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
}
