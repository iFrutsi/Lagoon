using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace NotchApp;

/// <summary>Shared plumbing for the two notch windows.</summary>
static class WindowUtil
{
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int SM_CXSCREEN = 0;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    /// <summary>Out of Alt+Tab, and never steals focus from the app you're using.</summary>
    public static void MakeToolWindow(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    /// <summary>Lets the window take keyboard focus (for typing a to-do), or not.</summary>
    public static void SetActivatable(Window w, bool on)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, on ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE);
    }

    /// <summary>Back on top. Windows sometimes drops "always on top" windows under ordinary ones
    /// (after sleep / sign-in, some installers and dialogs), which made the notch and dock vanish
    /// behind apps. One cheap call; no-op when already on top.</summary>
    public static void KeepOnTop(Window? w)
    {
        if (w == null || !w.IsVisible) return;
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !SlippedUnder(hwnd)) return; // still on top: leave the order alone
        SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x1 /*NOSIZE*/ | 0x2 /*NOMOVE*/ | SWP_NOACTIVATE | 0x200 /*NOOWNERZORDER*/);
    }

    /// <summary>An ordinary (not always-on-top) visible window sits above ours. Topmost windows are
    /// all at the front, so the walk up stops after a few.</summary>
    static bool SlippedUnder(IntPtr hwnd)
    {
        for (var h = GetWindow(hwnd, 3 /*GW_HWNDPREV*/); h != IntPtr.Zero; h = GetWindow(h, 3))
            if (IsWindowVisible(h) && (GetWindowLong(h, GWL_EXSTYLE) & 0x8 /*WS_EX_TOPMOST*/) == 0) return true;
        return false;
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    /// <summary>Size and centre at the top of the primary screen in one call (no sideways jump).</summary>
    public static void PlaceTopCentre(Window window, double widthDip, double heightDip)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) { window.Width = widthDip; window.Height = heightDip; return; }
        double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        int screen = GetSystemMetrics(SM_CXSCREEN);
        int w = (int)Math.Ceiling(widthDip * scale);
        int h = (int)Math.Ceiling(heightDip * scale);
        if ((screen - w) % 2 != 0) w++; // even margins: both windows share the exact same centre
        SetWindowPos(hwnd, IntPtr.Zero, (screen - w) / 2, 0, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}
