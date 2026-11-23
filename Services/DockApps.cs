using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace NotchApp.Services;

public sealed class DockApp
{
    public string Key = "";            // exe path (lower-case) or shortcut path
    public string Name = "";
    public string? Exe;
    public string? Shortcut;           // pinned .lnk
    public string? AppId;              // Store apps: launched via shell:AppsFolder\<id>
    public ImageSource? Icon;
    public List<IntPtr> Windows = new();
    public bool Pinned;
    public bool Active;
    public bool Running => Windows.Count > 0;
}

/// <summary>
/// Pinned taskbar shortcuts + open app windows (the same windows Alt+Tab shows), grouped
/// per app. Windows tells us when windows open/close/minimize/get focus; we rebuild then,
/// debounced, and only report a change when the list actually changed.
/// </summary>
public sealed class DockApps : IDisposable
{
    static readonly string PinnedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    readonly WinEventProc _proc;
    readonly List<IntPtr> _hooks = new();
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    readonly uint _ownPid = (uint)Environment.ProcessId;
    string _signature = "";

    public List<DockApp> Apps { get; private set; } = new();
    public event Action? Changed;

    public DockApps()
    {
        _proc = OnEvent;
        // focus, minimize/restore, and windows being shown/hidden/destroyed
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _hooks.Add(SetWinEventHook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        Refresh();
    }

    void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0) return;                 // whole windows only
        if (evt != EVENT_SYSTEM_FOREGROUND && evt != EVENT_OBJECT_DESTROY && GetParent(hwnd) != IntPtr.Zero) return;
        if (!_debounce.IsEnabled) _debounce.Start();
    }

    public void Refresh()
    {
        var apps = new List<DockApp>();
        var byKey = new Dictionary<string, DockApp>(StringComparer.OrdinalIgnoreCase);

        // pinned apps: Lagoon's own list (first filled from the Windows taskbar's pins)
        if (_store.Pins == null) { _store.Pins = PinsFromTaskbar(); _store.Save(); }
        foreach (var p in _store.Pins)
        {
            if (byKey.ContainsKey(p.Key) || (p.AppId != null && byKey.ContainsKey(p.AppId))) continue;
            var lnk = p.Shortcut != null && File.Exists(p.Shortcut) ? p.Shortcut : null;
            // an uninstalled app's pin quietly drops out (it stays in the list in case it comes back)
            if (p.AppId == null && lnk == null && !(p.Exe != null && File.Exists(p.Exe))) continue;
            var app = new DockApp { Key = p.Key, Pinned = true, Shortcut = lnk, Exe = p.Exe, AppId = p.AppId, Name = p.Name };
            app.Icon = IconFor(app.Exe ?? lnk ?? p.Key, p.AppId);
            byKey[p.Key] = app; apps.Add(app);
            if (p.AppId != null) byKey[p.AppId] = app; // so its open windows find this pin
            if (p.Exe != null) byKey[p.Exe] = app;
        }

        // open windows
        var fg = GetForegroundWindow();
        EnumWindows((h, _) =>
        {
            if (!IsAppWindow(h)) return true;
            var exe = ExeOf(h);
            if (exe == null) return true;
            // Store apps that don't report an app id on their window (e.g. the new WhatsApp):
            // look it up from the package their exe runs from.
            var aumid = AppIdOfWindow(h) ?? AppIdOfPackagedExe(exe);
            if (!(aumid != null && byKey.TryGetValue(aumid, out var app)) && !byKey.TryGetValue(exe, out app))
            {
                // Store apps (WindowsApps / frame host) are identified by their app id
                bool store = aumid != null && (exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                                               || exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase));
                var key = store ? aumid! : exe;
                app = new DockApp { Key = key, Exe = exe, AppId = store ? aumid : null, Name = DisplayName(exe, h) };
                app.Icon = IconFor(exe, store ? aumid : null);
                byKey[key] = app; apps.Add(app);
                if (aumid != null) byKey[aumid] = app;
                byKey[exe] = app;
            }
            app.Windows.Add(h);
            if (h == fg) app.Active = true;
            return true;
        }, IntPtr.Zero);

        // your order (dragged icons); apps you never moved keep their place at the end
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _store.Order.Count; i++) rank.TryAdd(_store.Order[i], i);
        apps = apps.OrderBy(a => rank.TryGetValue(a.Key, out var r) ? r : int.MaxValue).ToList(); // stable

        var sig = string.Join("|", apps.Select(a => $"{a.Key}:{a.Windows.Count}:{a.Active}:{a.Pinned}"));
        Apps = apps;
        if (sig == _signature) return;
        _signature = sig;
        Changed?.Invoke();
    }

    // ---- pins and order (saved in %APPDATA%\Lagoon\dock.json, separate from settings.json) ----

    readonly DockStore _store = DockStore.Load();

    /// <summary>The Windows taskbar's pins, in the order they were pinned: the dock's starting set.</summary>
    static List<DockStore.Pin> PinsFromTaskbar()
    {
        var pins = new List<DockStore.Pin>();
        try
        {
            foreach (var lnk in Directory.EnumerateFiles(PinnedDir, "*.lnk").OrderBy(f => File.GetCreationTime(f)))
            {
                var target = ResolveShortcut(lnk);
                // File Explorer's pin points at a shell location, not explorer.exe: map it so the
                // pin and the open Explorer windows are one icon, like on the real taskbar.
                if (string.IsNullOrEmpty(target) && Path.GetFileNameWithoutExtension(lnk).Contains("Explorer", StringComparison.OrdinalIgnoreCase))
                    target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                var key = !string.IsNullOrEmpty(target) && File.Exists(target) ? target : lnk;
                var aumid = AppIdOfFile(lnk); // Store-app pins have an app id instead of a target
                if (pins.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) || (aumid != null && p.AppId == aumid))) continue;
                pins.Add(new DockStore.Pin { Key = key, Name = Path.GetFileNameWithoutExtension(lnk), Shortcut = lnk, Exe = key == lnk ? null : target, AppId = aumid });
            }
        }
        catch { }
        return pins;
    }

    public void Pin(DockApp app)
    {
        var pins = _store.Pins ??= new();
        if (pins.Any(p => p.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase))) return;
        pins.Add(new DockStore.Pin { Key = app.Key, Name = app.Name, Exe = app.Exe, AppId = app.AppId, Shortcut = app.Shortcut });
        SaveOrder(Apps.Select(a => a.Key).ToList()); // stays exactly where it is now
        Refresh();
    }

    public void Unpin(DockApp app)
    {
        _store.Pins?.RemoveAll(p => p.Key.Equals(app.Key, StringComparison.OrdinalIgnoreCase));
        _store.Save();
        Refresh();
    }

    /// <summary>Move an icon to a new place in the dock (drag and drop).</summary>
    public void Move(string key, int to)
    {
        var keys = Apps.Select(a => a.Key).ToList();
        int from = keys.FindIndex(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to == from) return;
        keys.RemoveAt(from);
        keys.Insert(Math.Clamp(to, 0, keys.Count), key);
        SaveOrder(keys);
        Refresh();
    }

    void SaveOrder(List<string> keys)
    {
        // remember the places of apps that aren't open right now too (they come back where they were)
        foreach (var k in _store.Order)
            if (keys.Count < 100 && !keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
        _store.Order = keys;
        _store.Save();
    }

    // ---- actions ----

    /// <summary>Open it, bring it forward, or minimize it if it's already in front (like the taskbar).</summary>
    public void Activate(DockApp app)
    {
        if (!app.Running) { Launch(app); return; }
        var fg = GetForegroundWindow();
        int i = app.Windows.IndexOf(fg);
        if (i >= 0 && app.Windows.Count == 1) { ShowWindow(fg, SW_MINIMIZE); return; }
        var next = app.Windows[(i + 1) % app.Windows.Count]; // several windows: cycle through them
        if (IsIconic(next)) ShowWindow(next, SW_RESTORE);
        SwitchToThisWindow(next, true);
    }

    /// <summary>Bring one particular window to the front (picked from the window previews).</summary>
    public void ActivateWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SwitchToThisWindow(hwnd, true);
    }

    public void CloseWindow(IntPtr hwnd) => PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void Launch(DockApp app)
    {
        try
        {
            var target = app.Shortcut ?? (app.AppId != null ? @"shell:AppsFolder\" + app.AppId : app.Exe);
            if (target != null) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { }
    }

    public void CloseAll(DockApp app)
    {
        foreach (var h in app.Windows) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ---- which windows count as apps (roughly what Alt+Tab shows) ----

    bool IsAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return false;
        if (GetWindowTextLength(h) == 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == _ownPid) return false;
        var cls = new StringBuilder(64);
        GetClassName(h, cls, 64);
        return cls.ToString() is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    static string? ExeOf(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        var exe = PathOfProcess(pid);
        // Store apps live inside ApplicationFrameHost: use the real app's process instead.
        if (exe != null && exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            string? inner = null;
            EnumChildWindows(h, (c, _) =>
            {
                GetWindowThreadProcessId(c, out uint cp);
                if (cp != pid) { inner = PathOfProcess(cp); return false; }
                return true;
            }, IntPtr.Zero);
            exe = inner ?? exe;
        }
        return exe;
    }

    internal static string? PathOfProcess(uint pid)
    {
        var p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(p); }
    }

    static string DisplayName(string exe, IntPtr h)
    {
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch { }
        var t = new StringBuilder(256);
        GetWindowText(h, t, 256);
        return t.Length > 0 ? t.ToString() : Path.GetFileNameWithoutExtension(exe);
    }

    // ---- icons: a 48px icon from the exe (or the shortcut), cached ----

    /// <summary>
    /// The app's icon the way File Explorer gets it (works for normal exes, shortcuts without the
    /// arrow, and Store apps via their app id). Falls back to the older exe/shortcut extraction.
    /// </summary>
    ImageSource? IconFor(string path, string? appId)
    {
        var cacheKey = appId ?? path;
        if (_icons.TryGetValue(cacheKey, out var cached)) return cached;
        var img = (appId != null ? ShellIcon(@"shell:AppsFolder\" + appId) : null) ?? ShellIcon(path) ?? LegacyIcon(path);
        _icons[cacheKey] = img;
        return img;
    }

    static ImageSource? ShellIcon(string parsingName)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var obj) != 0) return null;
            var factory = (IShellItemImageFactory)obj;
            if (factory.GetImage(new SIZE { cx = 96, cy = 96 }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            return FromHBitmapWithAlpha(hbmp);
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }

    /// <summary>The shell hands back a 32-bit bitmap with real transparency; WPF's own helper drops it.</summary>
    static BitmapSource? FromHBitmapWithAlpha(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out BITMAP bm) == 0 || bm.bmWidth <= 0) return null;
        int w = bm.bmWidth, h = bm.bmHeight;
        var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var px = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbmp, 0, (uint)h, px, ref bmi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // old-style icons can come back with no alpha at all (all 0): treat those as opaque
        bool anyAlpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        src.Freeze();
        return src;
    }

    // ---- app ids (Store apps, and how Windows groups windows on the taskbar) ----

    static string? AppIdOfWindow(IntPtr h)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(h, ref iid, out var obj) != 0) return null;
            return ReadAppId((IPropertyStore)obj);
        }
        catch { return null; }
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _packagedIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"C:\Program Files\WindowsApps\5319275A.WhatsAppDesktop_2.x_x64__cv1g...\WhatsApp.Root.exe"
    /// -> the package's app id ("5319275A.WhatsAppDesktop_cv1g...!App"), via Windows' package manager.</summary>
    internal static string? AppIdOfPackagedExe(string exe)
    {
        if (_packagedIds.TryGetValue(exe, out var cached)) return cached;
        string? id = null;
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(exe, @"\\WindowsApps\\([^\\]+)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var pkg = new Windows.Management.Deployment.PackageManager().FindPackageForUser("", m.Groups[1].Value);
                id = pkg?.GetAppListEntries().FirstOrDefault()?.AppUserModelId;
            }
        }
        catch (Exception ex) { Log.Write("package id: " + ex.Message); }
        _packagedIds[exe] = id;
        return id;
    }

    static string? AppIdOfFile(string path)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out var obj) != 0) return null;
            return ReadAppId((IPropertyStore)obj);
        }
        catch { return null; }
    }

    static string? ReadAppId(IPropertyStore store)
    {
        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }

    static ImageSource? LegacyIcon(string path)
    {
        ImageSource? img = null;
        try
        {
            var handles = new IntPtr[1];
            var ids = new int[1];
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && PrivateExtractIcons(path, 0, 96, 96, handles, ids, 1, 0) > 0 && handles[0] != IntPtr.Zero)
            {
                img = Imaging.CreateBitmapSourceFromHIcon(handles[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                DestroyIcon(handles[0]);
            }
            else
            {
                // From the system image list: the plain icon, without the shortcut-arrow overlay.
                var info = new SHFILEINFO();
                var list = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_LARGEICON);
                var h = list != IntPtr.Zero ? ImageList_GetIcon(list, info.iIcon, ILD_TRANSPARENT) : IntPtr.Zero;
                if (h != IntPtr.Zero)
                {
                    img = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    DestroyIcon(h);
                }
            }
            img?.Freeze();
        }
        catch { }
        return img;
    }

    static string? ResolveShortcut(string lnk)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            Marshal.ReleaseComObject(link);
            return sb.ToString();
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _debounce.Stop();
        foreach (var h in _hooks) UnhookWinEvent(h);
    }

    // ---- Win32 ----
    const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_SYSTEM_MINIMIZESTART = 0x16, EVENT_SYSTEM_MINIMIZEEND = 0x17;
    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_NAMECHANGE = 0x800C;
    const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    const uint GW_OWNER = 4, PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, WM_CLOSE = 0x10;
    const int SW_MINIMIZE = 6, SW_RESTORE = 9, DWMWA_CLOAKED = 14;
    const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_LARGEICON = 0, ILD_TRANSPARENT = 1;
    [DllImport("comctl32.dll")] static extern IntPtr ImageList_GetIcon(IntPtr list, int index, uint flags);

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    const uint SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr hbitmap);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
    }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr p, uint flags, StringBuilder name, ref int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
}

/// <summary>The dock's own pins and icon order: %APPDATA%\Lagoon\dock.json.
/// (Its own file, so it never races the main settings file.)</summary>
public sealed class DockStore
{
    /// <summary>null until first filled from the Windows taskbar's pins.</summary>
    public List<Pin>? Pins { get; set; }
    /// <summary>App keys in the order you arranged them.</summary>
    public List<string> Order { get; set; } = new();

    public sealed class Pin
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Exe { get; set; }
        public string? AppId { get; set; }
        public string? Shortcut { get; set; }
    }

    static string FilePath => Path.Combine(AppSettings.Dir, "dock.json");

    public static DockStore Load() => SafeJson.Read<DockStore>(FilePath) ?? new DockStore();

    public void Save() => SafeJson.Write(FilePath, this);
}
