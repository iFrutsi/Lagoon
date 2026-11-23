using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NotchApp.Services;

namespace NotchApp;

/// <summary>
/// Small card above a dock icon showing a live preview of each of the app's windows (e.g. three
/// Chrome windows). Pick one to switch to it, or close it with its ×.
/// The previews are Windows' own live thumbnails (DWM): Windows draws them straight onto this
/// window, so Lagoon holds no screenshots and they cost nothing once the card closes.
/// DWM thumbnails need a normal (non-layered) window, so this one isn't AllowsTransparency;
/// Windows 11 rounds its corners instead.
/// </summary>
public sealed class WindowPreviews : Window
{
    public DockApp App { get; }
    public event Action<IntPtr>? Picked;
    public event Action<IntPtr>? CloseRequested;

    const double Pad = 8, Gap = 6, TitleH = 30;
    readonly double _thumbW, _thumbH;
    readonly List<(IntPtr Hwnd, Border Box)> _tiles = new();
    readonly List<IntPtr> _thumbs = new();

    /// <param name="anchorX">centre of the icon, DIPs</param>
    /// <param name="bottom">bottom edge of the card, DIPs (just above the dock)</param>
    public WindowPreviews(DockApp app, double anchorX, double bottom, double screenW)
    {
        App = app;
        int n = app.Windows.Count;
        // shrink the tiles if many windows wouldn't fit on screen
        double tileW = 228;
        double fit = (screenW - 40 - 2 * Pad - (n - 1) * Gap) / n;
        if (fit < tileW) tileW = Math.Max(120, fit);
        _thumbW = tileW - 2 * 8;
        _thumbH = Math.Round(_thumbW * 0.6);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = false;
        Title = "Lagoon Previews";
        UseLayoutRounding = true;
        Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Pad) };
        for (int i = 0; i < n; i++) row.Children.Add(Tile(app.Windows[i], i > 0));
        Content = row;

        Width = 2 * Pad + n * tileW + (n - 1) * Gap;
        Height = 2 * Pad + TitleH + _thumbH + 2 * 8;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(anchorX - Width / 2, 12, screenW - Width - 12);
        Top = bottom - Height;

        SourceInitialized += (_, _) =>
        {
            WindowUtil.MakeToolWindow(this);
            var h = new WindowInteropHelper(this).Handle;
            int round = 2, dark = 1, border = 0x00403A3A; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(h, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, 4);
            DwmSetWindowAttribute(h, 20 /*DWMWA_USE_IMMERSIVE_DARK_MODE*/, ref dark, 4);
            DwmSetWindowAttribute(h, 34 /*DWMWA_BORDER_COLOR*/, ref border, 4);
        };
        ContentRendered += (_, _) => RegisterThumbnails();
    }

    FrameworkElement Tile(IntPtr hwnd, bool gapBefore)
    {
        var icon = new Image { Source = App.Icon, Width = 16, Height = 16, Margin = new Thickness(2, 0, 8, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var title = new TextBlock
        {
            Text = TitleOf(hwnd), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = (FontFamily)Application.Current.FindResource("IconFont"), FontSize = 10 },
            Style = (Style)Application.Current.FindResource("PreviewClose"),
            ToolTip = "Close window",
        };
        close.Click += (_, e) => { CloseRequested?.Invoke(hwnd); e.Handled = true; };

        var head = new DockPanel { Height = TitleH, LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(icon);
        head.Children.Add(close);
        head.Children.Add(title);

        // where the live thumbnail goes; the big icon shows through if Windows has no image (e.g. minimized)
        var big = new Image { Source = App.Icon, Width = 40, Height = 40, Opacity = 0.5 };
        var box = new Border
        {
            Width = _thumbW, Height = _thumbH, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = big,
        };

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(box);

        var hover = (Brush)Application.Current.FindResource("HoverBrush");
        var tile = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 0, 8, 8),
            Background = Brushes.Transparent, Child = stack, Cursor = Cursors.Hand,
            Margin = new Thickness(gapBefore ? Gap : 0, 0, 0, 0),
        };
        tile.MouseEnter += (_, _) => tile.Background = hover;
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, _) => Picked?.Invoke(hwnd);
        _tiles.Add((hwnd, box));
        return tile;
    }

    void RegisterThumbnails()
    {
        if (_thumbs.Count > 0) return;
        var dest = new WindowInteropHelper(this).Handle;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var (src, box) in _tiles)
        {
            if (DwmRegisterThumbnail(dest, src, out var thumb) != 0) continue;
            _thumbs.Add(thumb);

            // crop Windows 11's invisible resize borders off the source
            var crop = new RECT();
            bool cropped = false;
            if (!IsIconic(src) && GetWindowRect(src, out var wr)
                && DwmGetWindowAttribute(src, 9 /*DWMWA_EXTENDED_FRAME_BOUNDS*/, out RECT fr, Marshal.SizeOf<RECT>()) == 0)
            {
                crop = new RECT { left = fr.left - wr.left, top = fr.top - wr.top, right = fr.right - wr.left, bottom = fr.bottom - wr.top };
                cropped = crop.right > crop.left && crop.bottom > crop.top;
            }
            double sw, sh;
            if (cropped) { sw = crop.right - crop.left; sh = crop.bottom - crop.top; }
            else { DwmQueryThumbnailSourceSize(thumb, out var sz); sw = sz.cx; sh = sz.cy; }
            if (sw <= 0 || sh <= 0) continue;

            // fit the window's shape inside the box, centred
            var tl = box.TransformToAncestor(this).Transform(new Point(0, 0));
            double bw = _thumbW * s, bh = _thumbH * s;
            double k = Math.Min(bw / sw, bh / sh);
            double w = sw * k, h = sh * k;
            double x = tl.X * s + (bw - w) / 2, y = tl.Y * s + (bh - h) / 2;

            var p = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 0x1 /*RECTDESTINATION*/ | 0x4 /*OPACITY*/ | 0x8 /*VISIBLE*/ | (cropped ? 0x2u /*RECTSOURCE*/ : 0),
                rcDestination = new RECT { left = (int)Math.Round(x), top = (int)Math.Round(y), right = (int)Math.Round(x + w), bottom = (int)Math.Round(y + h) },
                rcSource = crop,
                opacity = 255,
                fVisible = 1,
            };
            DwmUpdateThumbnailProperties(thumb, ref p);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var t in _thumbs) DwmUnregisterThumbnail(t);
        _thumbs.Clear();
        base.OnClosed(e);
    }

    static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetWindowText(h, sb, 256);
        var t = sb.ToString();
        // "Page title - Google Chrome": the app name is already on the icon
        int dash = t.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && t.Length - dash < 32) t = t[..dash];
        return t.Length == 0 ? "Window" : t;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)]
    struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination, rcSource;
        public byte opacity;
        public int fVisible, fSourceClientAreaOnly;
    }

    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
