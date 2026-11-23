using System.Windows;
using System.Windows.Media;

namespace NotchApp.Services;

/// <summary>Swaps the app-wide colour resources; every control picks them up live.</summary>
public static class ThemeManager
{
    public static Theme Current { get; private set; } = Theme.Black;
    public static event Action? Changed;

    /// <summary>Fill of the notch itself.</summary>
    public static Brush Island { get; private set; } = Brushes.Black;
    /// <summary>Hairline edge (glass); null = none.</summary>
    public static Brush? Edge { get; private set; }
    public static Color Foreground { get; private set; } = Colors.White;
    /// <summary>The thin line over apps. Glass gets a soft light tint so it's still findable.</summary>
    public static Brush Line => Current == Theme.Glass ? GlassLine : Island;
    static readonly Brush GlassLine = Frozen(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static void Apply(Theme theme)
    {
        var r = Application.Current.Resources;
        if (theme == Theme.Glass)
        {
            // Liquid glass, drawn by the app (no Windows blur): a see-through dark tint that is a
            // touch denser at the top, where it meets the screen edge, and clearer towards the
            // curve; plus a hairline highlight that fades in along the bottom edge. WPF anti-aliases
            // all of it, so the rounded corners stay perfectly smooth.
            Island = Gradient((0, 0xD6, 0x10, 0x10, 0x13), (0.55, 0xC2, 0x14, 0x14, 0x18), (1, 0xB0, 0x1A, 0x1A, 0x20));
            Edge = Gradient((0, 0x00, 0xFF, 0xFF, 0xFF), (0.6, 0x1A, 0xFF, 0xFF, 0xFF), (1, 0x5C, 0xFF, 0xFF, 0xFF));
            Set(r, "Subtle", 0xB3, 0xFF, 0xFF, 0xFF);
            Set(r, "Card", 0x24, 0xFF, 0xFF, 0xFF);
            Set(r, "CardHover", 0x38, 0xFF, 0xFF, 0xFF);
            Set(r, "ChipOff", 0x26, 0xFF, 0xFF, 0xFF);
            Set(r, "SliderRestBrush", 0x40, 0xFF, 0xFF, 0xFF);
            Set(r, "HoverBrush", 0x26, 0xFF, 0xFF, 0xFF);
            Set(r, "PressBrush", 0x40, 0xFF, 0xFF, 0xFF);
            Set(r, "PlaceholderBrush", 0x80, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            theme = Theme.Black;
            Island = Brushes.Black;
            Edge = null;
            Set(r, "Subtle", 0xFF, 0x8E, 0x8E, 0x93);
            Set(r, "Card", 0xFF, 0x1C, 0x1C, 0x1E);
            Set(r, "CardHover", 0xFF, 0x2C, 0x2C, 0x2E);
            Set(r, "ChipOff", 0xFF, 0x26, 0x26, 0x28);
            Set(r, "SliderRestBrush", 0xFF, 0x3A, 0x3A, 0x3C);
            Set(r, "HoverBrush", 0x1F, 0xFF, 0xFF, 0xFF);
            Set(r, "PressBrush", 0x38, 0xFF, 0xFF, 0xFF);
            Set(r, "PlaceholderBrush", 0xFF, 0x48, 0x48, 0x4A);
        }
        Current = theme;
        Changed?.Invoke();
    }

    static LinearGradientBrush Gradient(params (double offset, byte a, byte r, byte g, byte b)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach (var s in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb(s.a, s.r, s.g, s.b), s.offset));
        brush.Freeze();
        return brush;
    }

    static void Set(ResourceDictionary res, string key, byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        res[key] = brush;
    }
}
