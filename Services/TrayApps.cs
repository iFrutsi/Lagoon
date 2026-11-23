using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace NotchApp.Services;

/// <summary>An app running in the background with an icon in Windows' notification area.</summary>
public sealed class TrayApp
{
    public string Exe = "";
    public string Name = "";
    public ImageSource? Icon;
    public List<int> Pids = new();
    /// <summary>Part of Windows (e.g. Windows Security): no quitting it from here.</summary>
    public bool System;
}

/// <summary>
/// The apps behind the taskbar's "^" arrow: OneDrive, Discord, Steam...
/// Windows 11 draws its tray icons in the taskbar itself, which Lagoon keeps hidden, so we read
/// Windows' own list of tray icons (HKCU\Control Panel\NotifyIconSettings: the program and a
/// snapshot of its icon) and keep the ones whose program is running right now.
/// Only read while the dock's card is open: nothing runs at rest.
/// </summary>
public static class TrayApps
{
    public static List<TrayApp> Running()
    {
        // every running program by path (limited query rights: works for most of the user's apps)
        var running = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id <= 4) continue;
                var path = DockApps.PathOfProcess((uint)p.Id);
                if (path == null) continue;
                if (!running.TryGetValue(path, out var list)) running[path] = list = new();
                list.Add(p.Id);
            }
        }

        var me = Environment.ProcessPath ?? "";
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var apps = new List<TrayApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return apps;
            foreach (var id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("ExecutablePath") is not string raw) continue;
                var exe = ResolveKnownFolder(raw);
                if (!running.TryGetValue(exe, out var pids)) continue;
                if (exe.Equals(me, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue; // the shell itself
                if (!seen.Add(exe)) continue; // one row per app (WhatsApp registers several icons)

                apps.Add(new TrayApp
                {
                    Exe = exe,
                    Pids = pids,
                    Name = NameOf(exe, k.GetValue("InitialTooltip") as string),
                    Icon = Snapshot(k.GetValue("IconSnapshot") as byte[]),
                    System = exe.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase),
                });
            }
        }
        catch (Exception ex) { Log.Write("tray apps: " + ex.Message); }
        return apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Tray-only helpers of Windows itself: their proper name and what clicking their icon opens.</summary>
    static readonly Dictionary<string, (string Name, string Target)> Opens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SecurityHealthSystray.exe"] = ("Windows Security", "windowsdefender:"),
    };

    // ---- actions ----

    /// <summary>Bring its window forward if it has one on screen; otherwise start it again,
    /// which makes single-instance tray apps (Discord, Steam, WhatsApp...) show their window.</summary>
    public static void Open(TrayApp app)
    {
        var w = VisibleWindows(app).FirstOrDefault();        if (w != IntPtr.Zero)
        {
            if (IsIconic(w)) ShowWindow(w, 9 /*SW_RESTORE*/);
            SwitchToThisWindow(w, true);
            return;
        }
        // Windows' own tray helpers have no window and do nothing when started again: open what they stand for
        if (Opens.TryGetValue(Path.GetFileName(app.Exe), out var known))
        {
            try { Process.Start(new ProcessStartInfo(known.Target) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("tray open: " + ex.Message); }
            return;
        }
        try
        {
            var aumid = DockApps.AppIdOfPackagedExe(app.Exe); // Store apps can't be started from their exe
            var target = aumid != null ? @"shell:AppsFolder\" + aumid : app.Exe;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.Exe) ?? "" });
        }
        catch (Exception ex) { Log.Write("tray open: " + ex.Message); }
    }

    public static bool HasWindows(TrayApp app) => VisibleWindows(app).Any();

    /// <summary>Close its windows (most tray apps keep running in the background).</summary>
    public static void CloseWindows(TrayApp app)
    {
        foreach (var w in VisibleWindows(app)) PostMessage(w, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>End the app (all its processes). False if Windows wouldn't let us (e.g. it runs as admin).</summary>
    public static bool Quit(TrayApp app)
    {
        bool all = true;
        foreach (var pid in app.Pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { } // already gone
            catch { all = false; }
        }
        return all;
    }

    public static void ShowInFolder(TrayApp app)
    {
        try { Process.Start("explorer.exe", $"/select,\"{app.Exe}\""); } catch { }
    }

    static IEnumerable<IntPtr> VisibleWindows(TrayApp app)
    {
        var pids = new HashSet<uint>(app.Pids.Select(p => (uint)p));
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h) || GetWindowTextLength(h) == 0) return true;
            if ((GetWindowLong(h, -20 /*GWL_EXSTYLE*/) & 0x80 /*WS_EX_TOOLWINDOW*/) != 0) return true;
            if (GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    // ---- helpers ----

    /// <summary>"{6D809377-...}\Discord\app.exe": the GUID is a Windows known folder (Program Files...).</summary>
    static string ResolveKnownFolder(string raw)
    {
        if (raw.Length > 38 && raw[0] == '{' && raw[37] == '}' && Guid.TryParse(raw[1..37], out var g))
        {
            if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out var p) == 0)
            {
                var folder = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                return Path.Combine(folder, raw[38..].TrimStart('\\'));
            }
        }
        return raw;
    }

    static string NameOf(string exe, string? tooltip)
    {
        // Store apps (WhatsApp...): the package's own display name
        var m = System.Text.RegularExpressions.Regex.Match(exe, @"\\WindowsApps\\([^\\]+)\\", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            try
            {
                var name = new Windows.Management.Deployment.PackageManager().FindPackageForUser("", m.Groups[1].Value)?.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
        }
        if (Opens.TryGetValue(Path.GetFileName(exe), out var known)) return known.Name;
        try
        {
            var d = FileVersionInfo.GetVersionInfo(exe).FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d) && !d.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return d;
        }
        catch { }
        if (!string.IsNullOrWhiteSpace(tooltip)) return tooltip.Split('\n')[0].Trim();
        return Path.GetFileNameWithoutExtension(exe);
    }

    static ImageSource? Snapshot(byte[]? png)
    {
        if (png == null || png.Length < 8) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
