using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchApp;

/// <summary>
/// Ruler-style time picker for the Timer tab. The needle stays in the middle and the ruler slides
/// under it: drag with a finger or the mouse, or scroll. One tick per minute, a taller tick every
/// 5 minutes, labels every 15. The stretch from 0 up to the needle is tinted in the accent colour.
/// While a timer runs it just follows the time left (not draggable).
/// Drawn in one pass (no child elements per tick), and only when something changes.
/// </summary>
public sealed class RulerPicker : FrameworkElement
{
    double _value = 25;              // minutes, at the needle
    double _dragStartX, _dragStartValue;
    bool _dragging;

    public double Max { get; set; } = 180;
    public double PixelsPerMinute { get; set; } = 9;
    public Color Accent { get; set; } = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// <summary>While dragging/scrolling: the minutes under the needle (unsnapped), for a live preview.</summary>
    public event Action<double>? Previewing;
    /// <summary>The user let go (or scrolled): a whole number of minutes, at least 1.</summary>
    public event Action<int>? Picked;

    public bool Interactive { get; set; } = true;
    public bool IsDragging => _dragging;

    public double Value
    {
        get => _value;
        set { var v = Math.Clamp(value, 0, Max); if (Math.Abs(v - _value) < 0.001) return; _value = v; InvalidateVisual(); }
    }

    public RulerPicker()
    {
        Cursor = Cursors.SizeWE;
        Focusable = false;
        ClipToBounds = true;
    }

    // ------------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive) return;
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartValue = _value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_dragging) return;
        // drag left = more time (the ruler moves left under the needle), like a real ruler
        Value = _dragStartValue - (e.GetPosition(this).X - _dragStartX) / PixelsPerMinute;
        Previewing?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Commit();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_dragging) { _dragging = false; Commit(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Interactive) return;
        Value = Math.Round(_value) + (e.Delta > 0 ? 1 : -1);
        Commit();
        e.Handled = true;
    }

    void Commit()
    {
        int minutes = (int)Math.Clamp(Math.Round(_value), 1, Max);
        Value = minutes; // snap
        Picked?.Invoke(minutes);
    }

    // ------------------------------------------------------------------ drawing

    static readonly Typeface LabelFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double mid = w / 2, ppm = PixelsPerMinute;
        double stripTop = 2, stripH = h - 20;              // tick strip; labels underneath
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // strip background, rounded like the rest of Lagoon
        var strip = new Rect(0, stripTop, w, stripH);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), null, strip, 10, 10);

        // accent tint from 0 up to the needle
        double zeroX = mid - _value * ppm;
        double tintLeft = Math.Max(0, zeroX), tintRight = mid;
        if (tintRight > tintLeft)
        {
            var tint = new Rect(tintLeft, stripTop, tintRight - tintLeft, stripH);
            dc.PushClip(new RectangleGeometry(strip, 10, 10));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x38, Accent.R, Accent.G, Accent.B)), null, tint);
            if (zeroX >= 0) // the start edge, like the bordered blocks in a scheduler
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xB0, Accent.R, Accent.G, Accent.B)), null, new Rect(zeroX, stripTop, 1.5, stripH));
            dc.Pop();
        }

        // ticks: only the visible minutes are drawn
        var minor = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), 1);
        var major = new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1.2);
        minor.Freeze(); major.Freeze();
        var labelBrush = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        int first = Math.Max(0, (int)Math.Floor(_value - mid / ppm) - 1);
        int last = Math.Min((int)Max, (int)Math.Ceiling(_value + mid / ppm) + 1);
        double cy = stripTop + stripH / 2;
        int labelEvery = ppm * 5 >= 36 ? 5 : 15; // every 5 minutes when there's room
        for (int m = first; m <= last; m++)
        {
            double x = Math.Round(mid + (m - _value) * ppm) + 0.5;
            bool five = m % 5 == 0, fifteen = m % 15 == 0;
            double len = fifteen ? stripH * 0.62 : five ? stripH * 0.44 : stripH * 0.24;
            dc.DrawLine(five ? major : minor, new Point(x, cy - len / 2), new Point(x, cy + len / 2));
            if (m % labelEvery == 0)
            {
                string text = m >= 60 && m % 60 == 0 ? $"{m / 60}h" : m >= 60 ? $"{m / 60}h{m % 60:00}" : m.ToString(CultureInfo.InvariantCulture);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, labelBrush, dpi);
                dc.DrawText(ft, new Point(x - ft.Width / 2, stripTop + stripH + 3));
            }
        }

        // (the soft fade at both ends is an OpacityMask in the XAML, so it works on every theme)

        // the needle: accent line through the strip with a dot at the bottom (like the image)
        var needle = new SolidColorBrush(Accent);
        dc.DrawRectangle(needle, null, new Rect(mid - 1, 0, 2, stripTop + stripH + 1));
        dc.DrawEllipse(needle, null, new Point(mid, stripTop + stripH + 1), 4, 4);
    }
}
