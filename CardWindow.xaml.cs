using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NotchApp;

public partial class CardWindow : Window
{
    public const double W = 680, H = 300;

    // wave shape
    const double Wavelength = 18, Amplitude = 3.2, WaveMid = 10;

    public CardWindow()
    {
        InitializeComponent();
        CurrentPage = MediaPage;
        SourceInitialized += (_, _) =>
        {
            WindowUtil.MakeToolWindow(this);
            Place();
        };
        TabMedia.Checked += (_, _) => ShowPage(MediaPage);
        TabClipboard.Checked += (_, _) => ShowPage(ClipboardPage);
        TabWidgets.Checked += (_, _) => ShowPage(WidgetsPage);
        TabTimer.Checked += (_, _) => ShowPage(TimerPage);
        TabAgents.Checked += (_, _) => ShowPage(AgentsPage);
        TabControls.Checked += (_, _) => ShowPage(ControlsPage);
        WaveHost.SizeChanged += (_, _) => { BuildWave(); LayoutProgress(); };
        ProgressSlider.ValueChanged += (_, _) => LayoutProgress();
        ProgressSlider.SizeChanged += (_, _) => LayoutProgress();
    }

    public void Place() => WindowUtil.PlaceTopCentre(this, W, H);

    public bool MediaTabShown => MediaPage.Visibility == Visibility.Visible;

    public FrameworkElement CurrentPage { get; private set; } = null!;

    void ShowPage(FrameworkElement page)
    {
        foreach (var p in new FrameworkElement[] { MediaPage, ClipboardPage, WidgetsPage, TimerPage, AgentsPage, ControlsPage })
            p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
        CurrentPage = page;
        PageChanged?.Invoke();
    }

    public event Action? PageChanged;

    bool? _playing;

    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        bool first = _playing == null;
        _playing = playing;
        UIElement show = playing ? PauseGlyph : PlayGlyph, hide = playing ? PlayGlyph : PauseGlyph;
        if (first || !IsVisible) { show.Visibility = Visibility.Visible; hide.Visibility = Visibility.Collapsed; }
        else Services.Motion.Swap(hide, show); // crossfade + blur
    }

    /// <summary>Album colour: the wave, the artist name and the badge.</summary>
    public void SetAccent(Brush accent, Brush artistBrush)
    {
        WavePath.Stroke = accent;
        ArtBadge.Background = accent;
        ArtistText.Foreground = artistBrush;
    }

    // ------------------------------------------------------------------ wavy progress

    /// <summary>
    /// One static sine path, a wavelength longer than the bar. The played part is shown by
    /// clipping it; motion is just sliding it sideways by one wavelength, on repeat.
    /// </summary>
    void BuildWave()
    {
        double width = WaveHost.ActualWidth;
        if (width <= 0) return;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(0, WaveMid), false, false);
            var pts = new List<Point>();
            for (double x = 1.5; x <= width + Wavelength; x += 1.5)
                pts.Add(new Point(x, WaveMid - Amplitude * Math.Sin(x / Wavelength * 2 * Math.PI)));
            ctx.PolyLineTo(pts, true, true);
        }
        geo.Freeze();
        WavePath.Data = geo;
        WaveClipHost.Height = WaveHost.ActualHeight;
    }

    void LayoutProgress()
    {
        double width = WaveHost.ActualWidth;
        if (width <= 0) return;
        double range = ProgressSlider.Maximum - ProgressSlider.Minimum;
        double f = range > 0 ? (ProgressSlider.Value - ProgressSlider.Minimum) / range : 0;
        double x = Math.Clamp(f, 0, 1) * width;

        WaveClipHost.Width = x;
        WaveClipHost.Clip = new RectangleGeometry(new Rect(0, -4, Math.Max(0, x - 2), WaveHost.ActualHeight + 8));
        WaveThumb.Margin = new Thickness(Math.Max(0, x - 1.75), 0, 0, 0);
        WaveRest.Margin = new Thickness(x + 4, 0, 0, 0);
        WaveRest.Width = Math.Max(0, width - x - 4);
    }

    bool _waveRunning;

    /// <summary>Only animates while the card is open on the media tab and music plays.</summary>
    public void SetWaveRunning(bool run)
    {
        if (run == _waveRunning) return;
        _waveRunning = run;
        if (run)
        {
            var anim = new DoubleAnimation(0, -Wavelength, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(anim, 30);
            WaveShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        else
        {
            WaveShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
