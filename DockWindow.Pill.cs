using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NotchApp;

/// <summary>
/// The home pill adapts to what's behind it, like the iPhone's: white over dark screens, black
/// over light ones, fading between the two. Cheap by design:
///  - only while the pill is showing;
///  - one tiny screen copy (a 128x16 strip just above the pill, ~8 KB) at most once a second,
///    plus right away when the pill appears or the window in front changes;
///  - hysteresis, so a half-light background doesn't make it flicker.
/// </summary>
public partial class DockWindow
{
    static readonly Color PillWhite = Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), PillWhiteEdge = Color.FromArgb(0x66, 0x00, 0x00, 0x00);
    static readonly Color PillBlack = Color.FromArgb(0xD9, 0x00, 0x00, 0x00), PillBlackEdge = Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF);
    // brightness 0..1: turn dark above Light, back to white below Dark (the gap is the hysteresis)
    // (in-between greys keep the white pill)
    const double Light = 0.68, Dark = 0.50;

    readonly SolidColorBrush _pillFill = new(PillWhite), _pillEdge = new(PillWhiteEdge);
    readonly DispatcherTimer _pillTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _pillDark;
    IntPtr _pillFg;

    void InitPill()
    {
        HandlePill.Background = _pillFill;
        HandlePill.BorderBrush = _pillEdge;
        _pillTimer.Tick += (_, _) => AdaptPill();
        // only runs while the pill is on screen
        Handle.IsVisibleChanged += (_, _) =>
        {
            if (Handle.IsVisible) { AdaptPill(); _pillTimer.Start(); }
            else _pillTimer.Stop();
        };
    }

    /// <summary>The window in front changed (called from the dock's checks): look again now.</summary>
    void PillMaybeChanged()
    {
        if (!Handle.IsVisible) return;
        _pillTimer.Stop();
        AdaptPill();
        _pillTimer.Start();
    }

    void AdaptPill()
    {
        if (SampleBehindPill() is not double lum) return;
        bool dark = _pillDark ? lum > Dark : lum > Light;
        if (dark == _pillDark) return;
        _pillDark = dark;
        var d = TimeSpan.FromMilliseconds(260);
        _pillFill.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(dark ? PillBlack : PillWhite, d));
        _pillEdge.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(dark ? PillBlackEdge : PillWhiteEdge, d));
    }

    /// <summary>Average brightness of the screen just above the pill (what's behind it, without our own windows).</summary>
    double? SampleBehindPill()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var centre = Handle.PointToScreen(new Point(Handle.ActualWidth / 2, Handle.ActualHeight)); // physical px
        const int w = 128, h = 16;
        int x = (int)centre.X - w / 2, y = (int)(centre.Y - 3 * s) - h; // the strip right behind/above the pill

        IntPtr screen = GetDC(IntPtr.Zero), mem = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            bmp = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero) return null;
            old = SelectObject(mem, bmp);
            // plain SRCCOPY (no CAPTUREBLT): layered windows like ours are left out, so we see what's behind
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, 0x00CC0020)) return null;
            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            double sum = 0;
            for (int i = 0; i < px.Length; i += 4)
                sum += 0.0722 * px[i] + 0.7152 * px[i + 1] + 0.2126 * px[i + 2]; // B, G, R
            return sum / (w * h) / 255.0;
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
