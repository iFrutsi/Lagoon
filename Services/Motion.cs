using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace NotchApp.Services;

/// <summary>
/// Small shared motion helpers (from the design tips). Every animation here is short and runs only
/// when something changes, so they cost nothing at rest; effects (blur) are removed when done.
/// </summary>
public static class Motion
{
    static readonly IEasingFunction Out = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>"Stagger list rows by 30ms": rows fade and rise in one after another (first 12 only).</summary>
    public static void Stagger(Panel list, bool horizontal = false)
    {
        int i = 0;
        foreach (UIElement row in list.Children)
        {
            if (i >= 12) break;
            var begin = TimeSpan.FromMilliseconds(30 * i++);
            var shift = new TranslateTransform(horizontal ? 10 : 0, horizontal ? 0 : 6);
            row.RenderTransform = shift;
            row.Opacity = 0;
            var dur = TimeSpan.FromMilliseconds(220);
            var fade = new DoubleAnimation(1, dur) { BeginTime = begin, EasingFunction = Out };
            fade.Completed += (_, _) => { row.BeginAnimation(UIElement.OpacityProperty, null); row.Opacity = 1; };
            row.BeginAnimation(UIElement.OpacityProperty, fade);
            shift.BeginAnimation(horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                new DoubleAnimation(0, dur) { BeginTime = begin, EasingFunction = Out });
        }
    }

    /// <summary>"Pulse to confirm success": a quick swell and settle (1 -> 1.05 -> 1).</summary>
    public static void Pulse(FrameworkElement el)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = new ScaleTransform(1, 1);
        el.RenderTransform = s;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), Out));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        s.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>"Crossfade icons + blur": the outgoing icon fades and blurs (4px) while the new one
    /// fades in sharp. Both stay in the same spot (e.g. play/pause in one Grid cell).</summary>
    public static void Swap(UIElement from, UIElement to)
    {
        if (to.Visibility == Visibility.Visible && from.Visibility != Visibility.Visible) return; // already swapped
        var dur = TimeSpan.FromMilliseconds(160);
        var blur = new BlurEffect { Radius = 0 };
        from.Effect = blur;
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(4, dur));
        var outFade = new DoubleAnimation(0, dur);
        outFade.Completed += (_, _) =>
        {
            from.BeginAnimation(UIElement.OpacityProperty, null);
            from.Opacity = 1;
            from.Effect = null; // no lingering effect cost
            from.Visibility = Visibility.Collapsed;
        };
        from.BeginAnimation(UIElement.OpacityProperty, outFade);
        to.Opacity = 0;
        to.Visibility = Visibility.Visible;
        var inFade = new DoubleAnimation(1, dur) { EasingFunction = Out };
        inFade.Completed += (_, _) => { to.BeginAnimation(UIElement.OpacityProperty, null); to.Opacity = 1; };
        to.BeginAnimation(UIElement.OpacityProperty, inFade);
    }

    /// <summary>"Rubber band past bounds": at either end of a list, extra scrolling (wheel or finger)
    /// stretches the content a little with resistance and springs back on release.</summary>
    public static void RubberBand(ScrollViewer sv, bool horizontal = false)
    {
        TranslateTransform? shift = null;
        TranslateTransform Shift()
        {
            if (shift != null) return shift;
            if (sv.Content is not UIElement c) return shift = new TranslateTransform();
            shift = new TranslateTransform();
            c.RenderTransform = shift;
            return shift;
        }
        var prop = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        double offset = 0;
        void Settle()
        {
            if (offset == 0) return;
            var t = Shift();
            var a = new DoubleAnimation(offset, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut } };
            a.Completed += (_, _) => { t.BeginAnimation(prop, null); t.SetValue(prop, 0.0); };
            offset = 0;
            t.BeginAnimation(prop, a);
        }
        void Stretch(double push)
        {
            // resistance: each push moves less the further it already is (max ~40px)
            offset += push * (1 - Math.Min(Math.Abs(offset) / 40, 0.95)) * 0.35;
            var t = Shift();
            t.BeginAnimation(prop, null);
            t.SetValue(prop, offset);
        }

        // mouse wheel / touchpad at the ends
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        settle.Tick += (_, _) => { settle.Stop(); Settle(); };
        sv.PanningMode = horizontal ? PanningMode.HorizontalOnly : PanningMode.VerticalOnly; // finger scrolling
        // handledEventsToo: some lists already turn the wheel into their own scrolling
        sv.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            double pos = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
            double max = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
            bool atStart = pos <= 0 && e.Delta > 0, atEnd = pos >= max && e.Delta < 0;
            if (!atStart && !atEnd) return;
            Stretch(e.Delta > 0 ? 18 : -18);
            settle.Stop(); settle.Start();
        }), true);

        // touch: WPF's default bounces the whole window at the ends; stretch the list instead
        sv.ManipulationBoundaryFeedback += (_, e) =>
        {
            e.Handled = true;
            var v = horizontal ? e.BoundaryFeedback.Translation.X : e.BoundaryFeedback.Translation.Y;
            offset = 0;
            Stretch(v * 2.5);
        };
        sv.ManipulationCompleted += (_, _) => Settle();
    }
}
