using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NotchApp;

/// <summary>
/// A thick rounded slider like Control Center's. Vertical: fills from the bottom. Horizontal
/// (macOS style): fills from the left. Drag, click or scroll to change it. The icon sits at the
/// start and turns dark once the white fill covers it.
/// </summary>
public sealed class CapsuleSlider : Grid
{
    readonly Border _back = new();
    readonly Border _fill = new();
    readonly TextBlock _icon = new() { FontSize = 15, IsHitTestVisible = false };
    double _value;
    bool _horizontal;

    public event Action<double>? ValueChanged; // 0..1, raised only for user changes

    public CapsuleSlider()
    {
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        _back.SetResourceReference(Border.BackgroundProperty, "ChipOff");
        _fill.Background = Brushes.White;
        _icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        Children.Add(_back);
        Children.Add(_fill);
        Children.Add(_icon);
        Horizontal = false;
        SizeChanged += (_, _) => Layout();
    }

    public string Glyph { set => _icon.Text = value; }

    public bool Horizontal
    {
        get => _horizontal;
        set
        {
            _horizontal = value;
            _fill.HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            _fill.VerticalAlignment = value ? VerticalAlignment.Stretch : VerticalAlignment.Bottom;
            _icon.HorizontalAlignment = value ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            _icon.VerticalAlignment = value ? VerticalAlignment.Center : VerticalAlignment.Bottom;
            _icon.Margin = value ? new Thickness(9, 0, 0, 0) : new Thickness(0, 0, 0, 16);
            _icon.FontSize = value ? 13 : 22;
            Layout();
        }
    }

    /// <summary>0..1</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Layout(); }
    }

    void Layout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = (_horizontal ? h : w) / 2;
        Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r); // rounded ends for the fill too
        _fill.CornerRadius = new CornerRadius(r);                 // and the moving end of the fill is round, not cut
        bool covered;
        if (_horizontal)
        {
            // never thinner than the rounded end, so a low value still reads as a pill
            _fill.Width = Math.Max(h, w * _value);
            _fill.Height = double.NaN;
            covered = w * _value > 9 + _icon.FontSize;
        }
        else
        {
            _fill.Height = h * _value;
            _fill.Width = double.NaN;
            covered = h * _value > 16 + _icon.FontSize * 0.6;
        }
        _icon.Foreground = covered ? new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)) : Brushes.White;
    }

    void SetFromPoint(Point p)
    {
        Value = _horizontal ? p.X / Math.Max(1, ActualWidth) : 1 - p.Y / Math.Max(1, ActualHeight);
        ValueChanged?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        Value += e.Delta > 0 ? 0.05 : -0.05;
        ValueChanged?.Invoke(_value);
        e.Handled = true;
    }
}
