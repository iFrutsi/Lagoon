using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NotchApp.Services;

// Older saved values (2 = White, 3 = Liquid glass, both removed) fall back to Black.
public enum Theme { Black = 0, Glass = 1 }

/// <summary>Saved to %APPDATA%\Lagoon\settings.json.</summary>
public sealed class AppSettings
{
    public Theme Theme { get; set; } = Theme.Black;
    public string WeatherCity { get; set; } = "";
    public string WorldCity { get; set; } = "London";
    public string WorldZone { get; set; } = "GMT Standard Time";
    public List<TodoItem> Todos { get; set; } = new();

    /// <summary>New notifications slide out of the notch (Windows' own banners are switched off meanwhile).</summary>
    /// Off for a new install: it changes Windows' notification settings, so people switch it on themselves.
    public bool NotchNotifications { get; set; }
    /// <summary>Do not disturb: notifications go quietly to the notification centre, no pop-outs.</summary>
    public bool DoNotDisturb { get; set; }
    /// <summary>Each app's banner setting before we switched it off (-1 = wasn't set). null = we haven't.</summary>
    public Dictionary<string, int>? BannerRestore { get; set; }

    /// <summary>Countdown timer length (Timer tab presets / Controls tab button).</summary>
    public int TimerMinutes { get; set; } = 25;

    public bool DockEnabled { get; set; }
    /// <summary>Set the Windows taskbar to auto-hide while the dock is on.</summary>
    public bool HideTaskbar { get; set; }
    /// <summary>The taskbar's own auto-hide setting before we changed it (null = we haven't).</summary>
    public bool? TaskbarRestoreTo { get; set; }
    /// <summary>The taskbar alignment before we centred it (null = we haven't).</summary>
    public int? TaskbarAlignRestoreTo { get; set; }
    /// <summary>"Hide the taskbar in tablet mode" before we turned it on (null = we haven't).</summary>
    public int? TabletAutoHideRestoreTo { get; set; }
    /// <summary>"Optimize taskbar for touch interactions" before we turned it off (null = we haven't, -1 = it was unset).</summary>
    public int? TouchTaskbarRestoreTo { get; set; }
    /// <summary>Battery saver's "turn on at" charge levels before the Control Center tile switched it on (null = untouched).</summary>
    public int? SaverThresholdRestoreTo { get; set; }
    public int? SaverThresholdRestoreToAC { get; set; }

    public sealed class TodoItem
    {
        public string Text { get; set; } = "";
        public bool Done { get; set; }
    }

    /// <summary>Cities offered for the world clock (Windows time zone ids).</summary>
    public static readonly (string City, string Zone)[] WorldCities =
    [
        ("London", "GMT Standard Time"), ("New York", "Eastern Standard Time"), ("San Francisco", "Pacific Standard Time"),
        ("Chicago", "Central Standard Time"), ("Paris", "Romance Standard Time"), ("Berlin", "W. Europe Standard Time"),
        ("Dubai", "Arabian Standard Time"), ("Mumbai", "India Standard Time"), ("Singapore", "Singapore Standard Time"),
        ("Tokyo", "Tokyo Standard Time"), ("Sydney", "AUS Eastern Standard Time"), ("UTC", "UTC"),
    ];

    /// <summary>%APPDATA%\NotchApp, or NOTCHAPP_DATADIR (used by tests so they never touch your real data).</summary>
    public static readonly string Dir = Environment.GetEnvironmentVariable("NOTCHAPP_DATADIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lagoon");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load() => SafeJson.Read<AppSettings>(FilePath) ?? new AppSettings();

    public void Save() => SafeJson.Write(FilePath, this);

    // "Start with Windows": a per-user Task Scheduler task that runs the moment you sign in. The Run key
    // works too, but Windows holds Run apps back for several seconds after sign-in, and during that time
    // the old taskbar shows. The Run key is only a fallback if the task can't be created.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Lagoon";
    const string TaskName = "Lagoon";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string || Schtasks($"/Query /TN \"{TaskName}\"");
        }
        set
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (value && Environment.ProcessPath is { } exe)
            {
                if (CreateLogonTask(exe)) key?.DeleteValue(RunName, throwOnMissingValue: false);
                else key?.SetValue(RunName, $"\"{exe}\"");
            }
            else
            {
                Schtasks($"/Delete /TN \"{TaskName}\" /F");
                key?.DeleteValue(RunName, throwOnMissingValue: false);
            }
        }
    }

    /// <summary>Older builds used the Run key: move that to the sign-in task (once; no-op otherwise).</summary>
    public static void MigrateRunKeyToTask()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(RunName) is string) StartWithWindows = true;
    }

    static bool CreateLogonTask(string exe)
    {
        var user = System.Security.SecurityElement.Escape($"{Environment.UserDomainName}\\{Environment.UserName}");
        var path = System.Security.SecurityElement.Escape(exe);
        var dir = System.Security.SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        // no delay, runs on battery, never stopped for running long, normal priority (tasks default to low).
        // Also on every unlock: if Lagoon is still running the task is too, so IgnoreNew skips it (costs
        // nothing); if Lagoon was closed (signed out, ended, crashed) unlocking brings it back.
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Lagoon when you sign in.</Description></RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
                <SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange><UserId>{user}</UserId></SessionStateChangeTrigger>
              </Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
              </Settings>
              <Actions Context="Author"><Exec><Command>"{path}"</Command><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
            </Task>
            """;
        var file = Path.Combine(Path.GetTempPath(), "Lagoon-task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            return Schtasks($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>
    /// Start with Windows is on but points at a Lagoon.exe that no longer exists (the build folder moved,
    /// the app was moved or reinstalled...): point it at this copy instead, so sign-in still starts Lagoon.
    /// Leaves a working entry alone, even if it's a different copy. Runs once per start, off the UI thread.
    /// </summary>
    public static void RepairStartup()
    {
        try
        {
            if (Environment.ProcessPath is not { } self) return;
            string? target = null;
            if (Schtasks($"/Query /TN \"{TaskName}\" /XML", out var xml) && xml.Contains("<Command>"))
            {
                int a = xml.IndexOf("<Command>") + "<Command>".Length, b = xml.IndexOf("</Command>", a);
                if (b > a) target = System.Net.WebUtility.HtmlDecode(xml[a..b]).Trim().Trim('"');
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(RunName) is string run) target = run.Trim().Trim('"');
            }
            if (target == null) return; // off
            if (File.Exists(target))
            {
                // an older task without the "on unlock" trigger: add it (same exe)
                if (xml.Contains("<LogonTrigger>") && !xml.Contains("SessionStateChangeTrigger")) CreateLogonTask(target);
                return;
            }
            Log.Write($"start with Windows pointed at a missing file ({target}); now {self}");
            StartWithWindows = true; // re-registers with this exe
        }
        catch { }
    }

    /// <summary>A "Lagoon" entry in the Start menu (search "Lagoon" to start it again), pointing at the
    /// installed copy. Made once; left alone while it points at a file that exists.</summary>
    public static void EnsureStartMenuShortcut()
    {
        try
        {
            if (Environment.ProcessPath is not { } self) return;
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Lagoon", "Lagoon.exe");
            var exe = File.Exists(installed) ? installed : self;
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Lagoon.lnk");
            if (File.Exists(lnk) && ShortcutTarget(lnk) is { } t && File.Exists(t)) return;
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.Description = "Lagoon: notch and dock";
            sc.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
        catch (Exception ex) { Log.Write("start menu shortcut: " + ex.Message); }
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(lnk);
            string target = sc.TargetPath;
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(sc);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            return target;
        }
        catch { return null; }
    }

    static bool Schtasks(string args) => Schtasks(args, out _);

    static bool Schtasks(string args, out string output)
    {
        output = "";
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return false;
            output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// Settings files that survive a crash, a kill or a power cut mid-save:
///  - Write: to a temp file, flushed to disk, then swapped in; the previous version is kept as .bak.
///  - Read: the file, else the .bak. A file that can't be read is kept aside (.broken) instead of
///    being silently replaced with defaults on the next save.
/// </summary>
static class SafeJson
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static T? Read<T>(string path) where T : class
    {
        lock (Gate)
        {
            foreach (var p in new[] { path, path + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var value = JsonSerializer.Deserialize<T>(File.ReadAllText(p));
                    if (value == null) throw new JsonException("empty");
                    if (p != path)
                    {
                        Log.Write($"settings: {Path.GetFileName(path)} unreadable, using the backup");
                        // repair: the backup becomes the file again (the broken one was copied aside below)
                        try { File.Delete(path); } catch { }
                        Write(path, value);
                    }
                    return value;
                }
                catch (Exception ex)
                {
                    Log.Write($"settings: can't read {Path.GetFileName(p)}: {ex.Message}");
                    if (p == path) try { File.Copy(p, path + ".broken", overwrite: true); } catch { }
                }
            }
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Pretty);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes);
                    fs.Flush(flushToDisk: true);
                }
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Write($"settings: save {Path.GetFileName(path)} failed: {ex.Message}"); }
        }
    }
}
