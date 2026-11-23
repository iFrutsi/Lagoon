using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NotchApp.Services;

/// <summary>
/// Registers the dock as an "app bar" along the bottom, the same mechanism the real taskbar uses,
/// so maximized windows stop above it instead of sliding underneath. Also the switch that sets
/// the Windows taskbar to auto-hide while the dock is on (and puts it back afterwards).
/// </summary>
public sealed class AppBar : IDisposable
{
    readonly IntPtr _hwnd;
    readonly uint _callback;
    bool _registered;

    public AppBar(Window window)
    {
        _hwnd = new WindowInteropHelper(window).Handle;
        _callback = RegisterWindowMessage("Lagoon.AppBar");
    }

    public uint CallbackMessage => _callback;

    /// <summary>Reserve a strip of `heightPx` physical pixels at the bottom of the primary screen; returns the strip.</summary>
    public RECT Reserve(int heightPx)
    {
        var data = New();
        if (!_registered)
        {
            data.uCallbackMessage = _callback;
            var ok = SHAppBarMessage(ABM_NEW, ref data);
            Log.Write($"appbar NEW -> {ok}, cbSize {data.cbSize}");
            _registered = true;
            data = New();
        }
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        data.uEdge = ABE_BOTTOM;
        data.rc = new RECT { left = 0, right = w, top = h - heightPx, bottom = h };
        SHAppBarMessage(ABM_QUERYPOS, ref data);           // Windows may move it above the taskbar
        data.rc.top = data.rc.bottom - heightPx;
        SHAppBarMessage(ABM_SETPOS, ref data);
        Log.Write($"appbar SETPOS -> {data.rc.left},{data.rc.top},{data.rc.right},{data.rc.bottom}");
        return data.rc;
    }

    public void Release()
    {
        if (!_registered) return;
        var data = New();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _registered = false;
    }

    public void Dispose() => Release();

    APPBARDATA New() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    // ---- Windows taskbar auto-hide ----

    public static bool TaskbarAutoHide
    {
        get
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            return ((uint)SHAppBarMessage(ABM_GETSTATE, ref d) & ABS_AUTOHIDE) != 0;
        }
        set
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
            uint state = (uint)SHAppBarMessage(ABM_GETSTATE, ref d);
            d.lParam = (IntPtr)(value ? state | ABS_AUTOHIDE : state & ~ABS_AUTOHIDE);
            SHAppBarMessage(ABM_SETSTATE, ref d);
        }
    }

    // ---- Win32 ----
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const uint ABE_BOTTOM = 3, ABS_AUTOHIDE = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
}
