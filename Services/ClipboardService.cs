using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NotchApp.Services;

public enum ClipKind { Text, Link, Image, Files }

public sealed class ClipItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public string[]? Files { get; set; }
    public int ImageW { get; set; }
    public int ImageH { get; set; }
    public string? Source { get; set; }
    public DateTime Time { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }
}

/// <summary>
/// Clipboard history. Windows tells us when the clipboard changes (no polling). History is
/// saved encrypted for your Windows account (DPAPI) and never leaves the PC. Copies that
/// apps mark as private (password managers do) are skipped.
/// </summary>
public sealed class ClipboardService
{
    const int MaxUnpinned = 60;
    static readonly string Dir = Path.Combine(AppSettings.Dir, "clips");
    static readonly string IndexPath = Path.Combine(Dir, "history.bin");

    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    string? _selfCopyId; // ignore the change we cause when you click an item

    public List<ClipItem> Items { get; } = new();
    public event Action? Changed;
    /// <summary>New text was copied (not by Lagoon itself, not flagged secret by the app).</summary>
    public event Action<string>? TextCopied;

    public ClipboardService()
    {
        _debounce.Tick += (_, _) => { _debounce.Stop(); Read(); };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        Load();
    }

    public void Attach(IntPtr hwnd) => AddClipboardFormatListener(hwnd);
    public void Detach(IntPtr hwnd) => RemoveClipboardFormatListener(hwnd);

    /// <summary>Call on WM_CLIPBOARDUPDATE.</summary>
    public void OnClipboardUpdate() { Log.Write("clipboard changed"); _debounce.Stop(); _debounce.Start(); }

    void Read()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            // Apps flag secrets so clipboard managers skip them.
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")
                || data.GetDataPresent("Clipboard Viewer Ignore")
                || IsZeroFlag(data, "CanIncludeInClipboardHistory"))
                return;
            if (_selfCopyId != null) { _selfCopyId = null; return; }

            var item = new ClipItem { Source = OwnerName() };
            if (Clipboard.ContainsFileDropList())
            {
                var list = Clipboard.GetFileDropList();
                item.Kind = ClipKind.Files;
                item.Files = list.Cast<string>().ToArray();
                if (item.Files.Length == 0) return;
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Clipboard.GetImage();
                if (img == null) return;
                item.Kind = ClipKind.Image;
                item.ImageW = img.PixelWidth; item.ImageH = img.PixelHeight;
                SaveImage(item.Id, img);
            }
            else if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                if (text.Length > 20000) text = text[..20000];
                bool link = Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
                item.Kind = link ? ClipKind.Link : ClipKind.Text;
                item.Text = link ? text.Trim() : text;
            }
            else return;

            // Same thing copied again: just move it to the front.
            var dup = Items.FirstOrDefault(i => i.Kind == item.Kind && i.Text == item.Text
                && (i.Files == null ? item.Files == null : item.Files != null && i.Files.SequenceEqual(item.Files))
                && item.Kind != ClipKind.Image);
            if (dup != null)
            {
                Items.Remove(dup);
                dup.Time = DateTime.Now;
                Items.Insert(0, dup);
            }
            else
            {
                Items.Insert(0, item);
                Trim();
            }
            Log.Write($"clipboard: {item.Kind} from {item.Source}");
            Changed?.Invoke();
            if (item.Kind == ClipKind.Text && item.Text != null) TextCopied?.Invoke(item.Text);
            _saveTimer.Stop(); _saveTimer.Start();
        }
        catch (Exception ex) { Log.Write("clipboard error: " + ex.Message); }
    }

    static bool IsZeroFlag(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format)) return false;
            if (data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var b = ms.ToArray();
                return BitConverter.ToInt32(b, 0) == 0;
            }
        }
        catch { }
        return false;
    }

    void Trim()
    {
        var extra = Items.Where(i => !i.Pinned).Skip(MaxUnpinned).ToList();
        foreach (var e in extra) { Items.Remove(e); DeleteImage(e); }
    }

    /// <summary>Copy text without adding it to the history (e.g. a one-time code).</summary>
    public void CopyQuiet(string text)
    {
        try { _selfCopyId = "quiet"; Clipboard.SetText(text); }
        catch { _selfCopyId = null; }
    }

    /// <summary>Put an item back on the clipboard.</summary>
    public void CopyBack(ClipItem item)
    {
        try
        {
            _selfCopyId = item.Id;
            switch (item.Kind)
            {
                case ClipKind.Files:
                    var sc = new StringCollection();
                    sc.AddRange(item.Files!);
                    Clipboard.SetFileDropList(sc);
                    break;
                case ClipKind.Image:
                    var img = LoadImage(item, 0);
                    if (img != null) Clipboard.SetImage(img);
                    break;
                default:
                    Clipboard.SetText(item.Text ?? "");
                    break;
            }
        }
        catch { _selfCopyId = null; }
    }

    public void TogglePin(ClipItem item) { item.Pinned = !item.Pinned; Changed?.Invoke(); Save(); }

    public void Remove(ClipItem item) { Items.Remove(item); DeleteImage(item); Changed?.Invoke(); Save(); }

    /// <summary>Clears history; pinned snippets stay.</summary>
    public void Clear()
    {
        foreach (var i in Items.Where(i => !i.Pinned).ToList()) { Items.Remove(i); DeleteImage(i); }
        Changed?.Invoke();
        Save();
    }

    // ---- images: stored encrypted, decoded small on demand ----

    static string ImagePath(string id) => Path.Combine(Dir, id + ".img");

    static void SaveImage(string id, BitmapSource img)
    {
        Directory.CreateDirectory(Dir);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        File.WriteAllBytes(ImagePath(id), ProtectedData.Protect(ms.ToArray(), null, DataProtectionScope.CurrentUser));
    }

    /// <summary>decodeWidth 0 = full size (for copying back); otherwise a small thumbnail.</summary>
    public static BitmapSource? LoadImage(ClipItem item, int decodeWidth)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(ImagePath(item.Id)), null, DataProtectionScope.CurrentUser);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(bytes);
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    static void DeleteImage(ClipItem i)
    {
        if (i.Kind != ClipKind.Image) return;
        try { File.Delete(ImagePath(i.Id)); } catch { }
    }

    // ---- history file, encrypted for this Windows account ----

    void Load()
    {
        try
        {
            if (!File.Exists(IndexPath)) return;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(IndexPath), null, DataProtectionScope.CurrentUser);
            var list = JsonSerializer.Deserialize<List<ClipItem>>(json);
            if (list != null) Items.AddRange(list);
        }
        catch { }
    }

    void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.SerializeToUtf8Bytes(Items);
            File.WriteAllBytes(IndexPath, ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser));
        }
        catch { }
    }

    // ---- which app copied it ----

    static string? OwnerName()
    {
        try
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return null;
            GetWindowThreadProcessId(owner, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
