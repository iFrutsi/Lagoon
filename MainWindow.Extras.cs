using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NotchApp.Services;

namespace NotchApp;

/// <summary>
/// Small notch extras, all event-driven (nothing polls):
///  - one-time codes: copy a message with a code ("Your code is 482913") and the code pops out,
///    one tap copies just the code
///  - downloads: a file finishing in Downloads pops out (tap opens it, "Show" opens the folder)
///  - Wi-Fi: "Connected to X" / "Wi-Fi lost" as a quick pill
///  - long-press the pill: a peek at battery, time left, RAM and CPU, read only at that moment
/// </summary>
public partial class MainWindow
{
    static readonly Color PopBlue = Color.FromRgb(0x0A, 0x84, 0xFF);

    void InitExtras()
    {
        _clips.TextCopied += text => Dispatcher.BeginInvoke(() => OnTextCopied(text));
        StartDownloadsWatch();
        StartWifiWatch();
        InitLongPress();
    }

    void StopExtras()
    {
        _downloads?.Dispose();
        _downloads = null;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
    }

    // ------------------------------------------------------------------ one-time codes

    // a code word near the copied text, and a 4-8 digit code (also "482 913" / "482-913")
    static readonly Regex CodeWords = new(@"\b(code|otp|passcode|verification|verify|one[- ]time|2fa|two[- ]factor|security|login|sign[- ]in|pin)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // not part of a longer number, date, time, price or % ("G-582910" and "482-913." are fine)
    static readonly Regex CodeDigits = new(@"(?<![\d/:%])(?<!\d[.,\-])(\d{3}[ \-]\d{3}|\d{4,8})(?![\d/:%])(?![.,\-]\d)", RegexOptions.CultureInvariant);

    void OnTextCopied(string text)
    {
        if (text.Length > 600 || !CodeWords.IsMatch(text)) return;
        var matches = CodeDigits.Matches(text);
        if (matches.Count != 1) return; // none, or ambiguous (several numbers): don't guess
        string code = matches[0].Value.Replace(" ", "").Replace("-", "");
        if (code.Length is < 4 or > 8) return;
        string shown = code.Length == 6 ? code[..3] + " " + code[3..] : code;
        Log.Write($"one-time code spotted ({code.Length} digits)");
        void Copy()
        {
            _clips.CopyQuiet(code);
            ShowToast("", "Code copied", AlertGreen);
        }
        PopOut(new NoteItem(0, "Verification code", shown, "Tap to copy just the code", DateTimeOffset.Now, null,
            Glyph: "", Tap: Copy, ActionLabel: "Copy", Act: Copy, GlyphColor: PopBlue));
    }

    // ------------------------------------------------------------------ downloads

    FileSystemWatcher? _downloads;
    readonly Dictionary<string, DateTime> _recentDownloads = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] PartialExt = [".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload", ".!ut"];

    void StartDownloadsWatch()
    {
        try
        {
            var dir = DownloadsFolder();
            if (dir == null || !Directory.Exists(dir)) return;
            // Windows tells us about new/renamed files; nothing is scanned. Top folder only.
            _downloads = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.FileName,
                IncludeSubdirectories = false,
                InternalBufferSize = 8192,
            };
            _downloads.Created += (_, e) => Dispatcher.BeginInvoke(() => OnDownloadFile(e.FullPath));
            _downloads.Renamed += (_, e) => Dispatcher.BeginInvoke(() => OnDownloadFile(e.FullPath)); // browsers: x.crdownload -> x.pdf
            _downloads.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Write("downloads watch: " + ex.Message); }
    }

    void OnDownloadFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith('~') || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return;
        if (PartialExt.Any(x => name.EndsWith(x, StringComparison.OrdinalIgnoreCase))) return;
        // one pop-out per file (a download can be created, then renamed, then touched)
        var now = DateTime.Now;
        foreach (var k in _recentDownloads.Where(p => now - p.Value > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToList()) _recentDownloads.Remove(k);
        if (_recentDownloads.ContainsKey(path)) return;
        _recentDownloads[path] = now;
        // let the browser finish writing, then check it's a real, finished file
        var check = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length == 0 || (fi.Attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) != 0) return;
                PopOut(new NoteItem(0, "Downloaded", name, SizeText(fi.Length), DateTimeOffset.Now, null,
                    Glyph: "", GlyphColor: AlertGreen,
                    Tap: () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }),
                    ActionLabel: "Show", Act: () => Process.Start("explorer.exe", $"/select,\"{path}\"")));
            }
            catch { }
        };
        check.Start();
    }

    static string SizeText(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" :
        bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.0} MB" :
        bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";

    static string? DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    // ------------------------------------------------------------------ Wi-Fi

    string? _wifiSsid;
    bool _wifiKnown;
    readonly DispatcherTimer _wifiCheck = new() { Interval = TimeSpan.FromSeconds(1.5) };

    void StartWifiWatch()
    {
        _wifiSsid = CurrentSsid();
        _wifiKnown = true;
        _wifiCheck.Tick += (_, _) => { _wifiCheck.Stop(); CheckWifi(); };
        // Windows raises this when addresses change (connect, disconnect, switch network); no polling
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    void OnNetworkChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(() => { _wifiCheck.Stop(); _wifiCheck.Start(); }); // settle first

    void CheckWifi()
    {
        var now = CurrentSsid();
        if (!_wifiKnown || now == _wifiSsid) { _wifiSsid = now; return; }
        var before = _wifiSsid;
        _wifiSsid = now;
        if (now != null) Alert("", $"Connected to {now}", AlertGreen);
        else if (before != null) Alert("", "Wi-Fi lost", AlertOrange);
    }

    static string? CurrentSsid()
    {
        try
        {
            var p = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (p == null || !p.IsWlanConnectionProfile) return null;
            var ssid = p.WlanConnectionProfileDetails?.GetConnectedSsid();
            return string.IsNullOrWhiteSpace(ssid) ? p.ProfileName : ssid;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ long-press peek

    readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    Point _holdAt;
    bool _heldPeek;

    void InitLongPress()
    {
        _holdTimer.Tick += async (_, _) => { _holdTimer.Stop(); _heldPeek = true; await ShowPeek(); };
        Island.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_mode is Mode.Expanded or Mode.Hidden) return;
            _heldPeek = false;
            _holdAt = e.GetPosition(Island);
            _holdTimer.Stop();
            _holdTimer.Start();
        };
        Island.PreviewMouseMove += (_, e) =>
        {
            if (_holdTimer.IsEnabled && (e.GetPosition(Island) - _holdAt).Length > 10) _holdTimer.Stop(); // a swipe, not a hold
        };
        Island.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _holdTimer.Stop();
            if (_heldPeek) { _heldPeek = false; e.Handled = true; } // a long-press doesn't also open the card
        };
    }

    /// <summary>Battery, time left, RAM and CPU, read right now (CPU: two quick samples 300 ms apart).</summary>
    async Task ShowPeek()
    {
        var parts = new List<string>();
        string glyph = "";
        if (GetSystemPowerStatus(out var ps) && ps.BatteryFlag != 128 && ps.BatteryLifePercent <= 100)
        {
            glyph = BatteryGlyph(ps.BatteryLifePercent);
            parts.Add($"{ps.BatteryLifePercent}%");
            if (ps.ACLineStatus == 1) parts.Add("charging");
            else if (ps.BatteryLifeTime > 0) parts.Add(ps.BatteryLifeTime >= 3600 ? $"{ps.BatteryLifeTime / 3600}h {ps.BatteryLifeTime % 3600 / 60}m left" : $"{ps.BatteryLifeTime / 60}m left");
        }
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref mem)) parts.Add($"RAM {mem.dwMemoryLoad}%");
        if (GetSystemTimes(out var idle1, out var kern1, out var user1))
        {
            await Task.Delay(300);
            if (GetSystemTimes(out var idle2, out var kern2, out var user2))
            {
                double total = (kern2 - kern1) + (user2 - user1), idle = idle2 - idle1;
                if (total > 0) parts.Add($"CPU {Math.Clamp((int)Math.Round((total - idle) * 100 / total), 0, 100)}%");
            }
        }
        if (parts.Count > 0) ShowToast(glyph, string.Join(" · ", parts), ThemeManager.Foreground);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }

    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
