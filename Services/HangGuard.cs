using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace NotchApp.Services;

/// <summary>
/// Freeze guard. A background thread checks every 2 s that the UI thread still answers. If it hasn't
/// for 15 s, Lagoon is frozen, so (from this thread, which isn't stuck):
///   1. note it in crash.log and save a snapshot of every thread (hang.dmp) so the cause can be found,
///   2. bring the Windows taskbar back (a frozen Lagoon would otherwise leave you with no taskbar),
///   3. start a fresh Lagoon and end the frozen one. Not again if the fresh one also freezes within
///      2 minutes, so it can never restart in a loop.
/// Costs one tiny wake-up every 2 s.
/// </summary>
public static class HangGuard
{
    static long _lastPong = Environment.TickCount64;

    public static void Start(Dispatcher ui, bool startedAfterHang)
    {
        var started = Environment.TickCount64;
        var t = new Thread(() =>
        {
            long lastLoop = Environment.TickCount64;
            while (true)
            {
                Thread.Sleep(2000);
                long now = Environment.TickCount64;
                // the PC slept (or everything was starved): that's not Lagoon freezing, start counting afresh
                if (now - lastLoop > 6000) Interlocked.Exchange(ref _lastPong, now);
                lastLoop = now;
                ui.BeginInvoke(DispatcherPriority.Send, () => Interlocked.Exchange(ref _lastPong, Environment.TickCount64));
                long silent = Environment.TickCount64 - Interlocked.Read(ref _lastPong);
                if (silent < 15000) continue;

                Note($"Lagoon stopped responding ({silent / 1000}s with no answer from the UI thread).");
                RestoreTaskbar(); // first: you're never left without a taskbar
                WriteDump();
                bool restart = !(startedAfterHang && Environment.TickCount64 - started < 120_000);
                if (restart && Environment.ProcessPath is { } exe)
                {
                    try { Process.Start(new ProcessStartInfo(exe, "--after-hang") { UseShellExecute = false }); } catch { }
                    Note("Started a fresh Lagoon.");
                }
                else Note("Froze again right after a restart: not restarting.");
                // the UI thread can't run a normal exit; end this process outright (the fresh copy is waiting for it)
                TerminateProcess(GetCurrentProcess(), 1);
            }
        }) { IsBackground = true, Name = "Lagoon freeze guard", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Thread stacks + Lagoon's own memory (enough to see where every thread is stuck), one file,
    /// overwritten each time. A process can't reliably snapshot itself, so a second copy of Lagoon,
    /// started with --dump, takes it (see <see cref="DumpOther"/>).</summary>
    static void WriteDump()
    {
        try
        {
            var path = Path.Combine(AppSettings.Dir, "hang.dmp");
            if (Environment.ProcessPath is not { } exe) return;
            using var p = Process.Start(new ProcessStartInfo(exe, $"--dump {Environment.ProcessId} \"{path}\"") { UseShellExecute = false });
            bool ok = p != null && p.WaitForExit(30000) && p.ExitCode == 0;
            Note(ok ? $"Saved a snapshot for diagnosis: {path}" : "Couldn't save a snapshot.");
        }
        catch { }
    }

    /// <summary>Runs in the helper copy (lagoon.exe --dump pid path): snapshot the frozen copy, then exit.</summary>
    public static int DumpOther(int pid, string path)
    {
        try
        {
            using var target = Process.GetProcessById(pid);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            const uint type = 0x1 /*WithDataSegs*/ | 0x4 /*WithHandleData*/ | 0x200 /*WithPrivateReadWriteMemory*/
                              | 0x1000 /*WithThreadInfo*/ | 0x20 /*WithUnloadedModules*/;
            return MiniDumpWriteDump(target.Handle, (uint)pid, fs.SafeFileHandle.DangerousGetHandle(), type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) ? 0 : 1;
        }
        catch { return 1; }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>Show the taskbar and clear Lagoon's see-through style, without touching the frozen UI thread.</summary>
    static void RestoreTaskbar()
    {
        try
        {
            var windows = new List<IntPtr>();
            var main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero) windows.Add(main);
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) windows.Add(h);
            foreach (var w in windows)
            {
                int ex = GetWindowLong(w, -20);
                if ((ex & 0x80000) != 0) SetWindowLong(w, -20, ex & ~0x80000);
                ShowWindowAsync(w, 8 /*SW_SHOWNA*/);
            }
        }
        catch { }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file, uint type, IntPtr exception, IntPtr user, IntPtr callback);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
