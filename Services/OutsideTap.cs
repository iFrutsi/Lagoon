using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace NotchApp.Services;

/// <summary>
/// "Tap anywhere else to close": while something is open, watch for a press (finger, pen or
/// mouse; touch taps arrive as mouse presses) outside the given areas. Only installed while
/// something is open, then removed, so it costs nothing otherwise.
/// </summary>
public sealed class OutsideTap : IDisposable
{
    readonly Func<IEnumerable<Rect>> _areas;   // physical screen pixels
    readonly Action _onOutside;
    readonly HookThread.HookProc _proc;        // kept alive while hooked
    readonly Action<POINT> _check;
    IntPtr _hook;

    public OutsideTap(Func<IEnumerable<Rect>> areas, Action onOutside)
    {
        _areas = areas;
        _onOutside = onOutside;
        _proc = Hook;
        _check = Check;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = HookThread.SetHook(WH_MOUSE_LL, _proc);
    }

    public void Stop()
    {
        HookThread.Unhook(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Runs for every mouse event in Windows: just note the point and return at once.
    /// (WindowFromPoint can ask the window under the pointer a question, which waits on that app.)</summary>
    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN))
        {
            var pt = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt;
            HookThread.Post(() => _check(pt));
        }
        return HookThread.CallNextHookEx(_hook, code, wParam, lParam);
    }

    void Check(POINT pt)
    {
        // "inside" = on any of our own visible windows (card, dock, menus, Settings...).
        // See-through parts of our windows let taps through, so they count as outside.
        GetWindowThreadProcessId(GetAncestor(WindowFromPoint(pt), 2 /*GA_ROOT*/), out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        // the extra areas are WPF rectangles: check those (and act) on the UI thread
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var r in _areas()) if (r.Contains(pt.x, pt.y)) return;
            _onOutside();
        });
    }

    public void Dispose() => Stop();

    const int WH_MOUSE_LL = 14;
    static readonly IntPtr WM_LBUTTONDOWN = (IntPtr)0x0201, WM_RBUTTONDOWN = (IntPtr)0x0204;
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    /// <summary>A WPF element's on-screen rectangle in physical pixels.</summary>
    public static Rect ScreenRect(FrameworkElement e)
    {
        if (!e.IsVisible) return Rect.Empty;
        var tl = e.PointToScreen(new Point(0, 0));
        var br = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new Rect(tl, br);
    }
}
