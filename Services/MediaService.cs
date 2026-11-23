using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using WpfColor = System.Windows.Media.Color;
using PixelFormats = System.Windows.Media.PixelFormats;

namespace NotchApp.Services;

/// <summary>Whatever is playing in any app or browser, via Windows' media sessions.</summary>
public sealed class MediaService
{
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    readonly System.Windows.Threading.Dispatcher _ui = Application.Current.Dispatcher;
    int _version;
    TimeSpan _position;
    DateTimeOffset _positionStamp;

    public event Action? Changed;

    public bool HasSession => _session != null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public BitmapImage? Art { get; private set; }
    public WpfColor ArtColor { get; private set; } = WpfColor.FromRgb(255, 255, 255);
    public bool IsPlaying { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan Position
    {
        get
        {
            var p = _position;
            if (IsPlaying) p += DateTimeOffset.Now - _positionStamp;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }

    public async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(Attach);
            Attach();
        }
        catch { }
    }

    void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnProperties;
            _session.PlaybackInfoChanged -= OnPlayback;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = _manager?.GetCurrentSession();
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnProperties;
            _session.PlaybackInfoChanged += OnPlayback;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        ReadTimeline();
        ReadPlayback();
        _ = ReadPropertiesAsync();
    }

    void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() => _ = ReadPropertiesAsync());

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        _ui.InvokeAsync(() => { ReadPlayback(); Changed?.Invoke(); });

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        _ui.InvokeAsync(() => { ReadTimeline(); Changed?.Invoke(); });

    void ReadPlayback()
    {
        var current = Position; // freeze position at the moment the state flips
        var info = _session?.GetPlaybackInfo();
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        CanSeek = info?.Controls?.IsPlaybackPositionEnabled ?? false;
        _position = current;
        _positionStamp = DateTimeOffset.Now;
    }

    void ReadTimeline()
    {
        var t = _session?.GetTimelineProperties();
        if (t == null)
        {
            Duration = TimeSpan.Zero;
            _position = TimeSpan.Zero;
            _positionStamp = DateTimeOffset.Now;
            return;
        }
        Duration = t.EndTime - t.StartTime;
        _position = t.Position - t.StartTime;
        _positionStamp = t.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : t.LastUpdatedTime;
    }

    async Task ReadPropertiesAsync()
    {
        int version = ++_version;
        var session = _session;
        if (session == null)
        {
            Title = ""; Artist = ""; Art = null;
            Changed?.Invoke();
            return;
        }
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";

            BitmapImage? art = null;
            var color = WpfColor.FromRgb(255, 255, 255);
            if (props?.Thumbnail != null)
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                art = Decode(bytes, 224); // shown at 104px; no need to keep a 1000px+ bitmap in RAM
                color = AccentFrom(bytes);
            }
            if (version != _version) return;
            Art = art;
            ArtColor = color;
        }
        catch { }
        Changed?.Invoke();
    }

    public async void TogglePlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    public async void Seek(TimeSpan to)
    {
        if (_session == null) return;
        _position = to;
        _positionStamp = DateTimeOffset.Now;
        try { await _session.TryChangePlaybackPositionAsync(to.Ticks); } catch { }
    }

    static BitmapImage? Decode(byte[] bytes, int width)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            if (width > 0) bmp.DecodePixelWidth = width;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>A bright, saturated colour pulled from the artwork, for tinting.</summary>
    static WpfColor AccentFrom(byte[] bytes)
    {
        var white = WpfColor.FromRgb(255, 255, 255);
        var small = Decode(bytes, 16);
        if (small == null) return white;
        var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var px = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(px, stride, 0);

        double r = 0, g = 0, b = 0, weight = 0;
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            double B = px[i], G = px[i + 1], R = px[i + 2];
            double max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
            double sat = max == 0 ? 0 : (max - min) / max;
            double w = 0.05 + sat * sat * (max / 255.0);
            r += R * w; g += G * w; b += B * w; weight += w;
        }
        if (weight <= 0) return white;
        r /= weight; g /= weight; b /= weight;
        double top = Math.Max(r, Math.Max(g, b));
        if (top < 8) return white;
        double scale = Math.Max(1, 225 / top);
        return WpfColor.FromRgb((byte)Math.Min(255, r * scale), (byte)Math.Min(255, g * scale), (byte)Math.Min(255, b * scale));
    }
}
