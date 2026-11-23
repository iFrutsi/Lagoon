using System.Windows;

namespace NotchApp;

public partial class App : Application
{
    Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        // freeze guard helper: lagoon.exe --dump <pid> <file> snapshots a frozen Lagoon, then exits (no UI)
        if (e.Args.Length == 3 && e.Args[0] == "--dump" && int.TryParse(e.Args[1], out int dumpPid))
        {
            Environment.Exit(Services.HangGuard.DumpOther(dumpPid, e.Args[2]));
            return;
        }

        // installer: --quit closes a running Lagoon properly (taskbar and notification pop-ups put back),
        // --enable-startup turns on Start with Windows for this exe. Both exit without UI.
        if (e.Args.Contains("--quit"))
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(QuitName, out var quit)) { quit.Set(); quit.Dispose(); }
                // wait (up to 8 s) until it has really gone
                using var m = new Mutex(false, "Lagoon.SingleInstance");
                try { if (m.WaitOne(8000)) m.ReleaseMutex(); } catch (AbandonedMutexException) { }
            }
            catch { }
            Environment.Exit(0);
            return;
        }
        if (e.Args.Contains("--enable-startup"))
        {
            try { Services.AppSettings.StartWithWindows = true; } catch { }
            Environment.Exit(0);
            return;
        }

        // dev: NOTCHAPP_BTLIST=<file> writes the Bluetooth audio devices found to that file, then quits
        if (Environment.GetEnvironmentVariable("NOTCHAPP_BTLIST") is { } btFile)
        {
            var lines = Services.BluetoothAudio.List().Select(d =>
                $"{d.Name} | connected={d.Connected} | headset={d.IsHeadset} | endpoints={d.Parts.Count} | supportsConnect={Services.BluetoothAudio.SupportsConnect(d)}");
            System.IO.File.WriteAllLines(btFile, lines.DefaultIfEmpty("(none found)"));
            Shutdown();
            return;
        }

        // dev: NOTCHAPP_HOOKTEST=connect|disconnect edits the Claude settings file, then quits
        if (Environment.GetEnvironmentVariable("NOTCHAPP_HOOKTEST") is { } op)
        {
            if (op == "connect") Services.ClaudeAgents.Connect(); else Services.ClaudeAgents.Disconnect();
            Shutdown();
            return;
        }

        // Safety net: an unexpected error in a click or update gets logged instead of closing the app.
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrash(args.ExceptionObject as Exception);
            // if we're going down, never leave the Windows taskbar hidden
            try { NotchApp.MainWindow.RestoreTaskbar(Services.AppSettings.Load()); } catch { }
            // ...nor Windows' own notification pop-ups switched off
            try { Services.NotificationBanners.RestoreAll(Services.AppSettings.Load()); } catch { }
        };

        MigrateFromNotchApp();
        // --after-hang: started by the freeze guard of a frozen Lagoon that is ending right now,
        // so wait (up to 10 s) for it to let go instead of quitting as a second copy
        bool afterHang = e.Args.Contains("--after-hang");
        _single = new Mutex(true, "Lagoon.SingleInstance", out bool first);
        if (!first && afterHang)
        {
            try { first = _single.WaitOne(10000); }
            catch (AbandonedMutexException) { first = true; } // the frozen copy was ended: it's ours now
        }
        if (!first)
        {
            // already running (e.g. started from the Start menu): tell that copy to say hello, then quit
            try { if (EventWaitHandle.TryOpenExisting(WakeName, out var wake)) { wake.Set(); wake.Dispose(); } } catch { }
            Shutdown();
            return;
        }
        _started = true;
        Note("Lagoon started" + (afterHang ? " (after a freeze)" : ""));
        // At sign-in Windows shows its taskbar until we run: tuck it away before building any windows.
        // Only once the original taskbar settings are already saved (the dock's normal setup does the rest).
        try
        {
            var s = Services.AppSettings.Load();
            if (s.DockEnabled && s.HideTaskbar && s.TaskbarRestoreTo != null) Services.TaskbarHider.Hide();
        }
        catch { }
        // Draw on the CPU: the notch is tiny, and skipping the Direct3D device
        // saves tens of MB of RAM and the GPU read-back that layered windows need.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);
        MainWindow = new MainWindow();
        MainWindow.Show();
        // if the UI ever stops answering: bring the taskbar back, save a snapshot, restart
        Services.HangGuard.Start(Dispatcher, afterHang);
        // dev: NOTCHAPP_FREEZETEST=1 freezes the UI thread 8 s after start, to test the freeze guard
        if (Environment.GetEnvironmentVariable("NOTCHAPP_FREEZETEST") == "1")
        {
            Environment.SetEnvironmentVariable("NOTCHAPP_FREEZETEST", null); // the restarted copy mustn't freeze too
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            t.Tick += (_, _) => { t.Stop(); Thread.Sleep(Timeout.Infinite); };
            t.Start();
        }
        // Start with Windows: move an old Run-key entry to the sign-in task (off the UI thread; no-op after)
        // ...and if it points at a Lagoon.exe that's gone (moved/rebuilt elsewhere), point it at this copy
        _ = Task.Run(() => { Services.AppSettings.MigrateRunKeyToTask(); Services.AppSettings.RepairStartup(); Services.AppSettings.EnsureStartMenuShortcut(); });
        // opened again while running: a kernel wait, no thread or polling until it's signalled
        _wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        ThreadPool.RegisterWaitForSingleObject(_wake, (_, _) =>
            Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.SayRunning()), null, Timeout.Infinite, executeOnlyOnce: false);
        // the installer / uninstaller asks us to quit: the normal Quit, so everything is put back
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);
        ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    const string WakeName = "Lagoon.OpenedAgain";
    const string QuitName = "Lagoon.Quit";
    EventWaitHandle? _quit;
    EventWaitHandle? _wake;
    bool _started;

    /// <summary>One line in crash.log (starts and exits), so a Lagoon that vanished can be explained.</summary>
    internal static void Note(string text)
    {
        try
        {
            var file = System.IO.Path.Combine(Services.AppSettings.Dir, "crash.log");
            System.IO.Directory.CreateDirectory(Services.AppSettings.Dir);
            if (System.IO.File.Exists(file) && new System.IO.FileInfo(file).Length > 200_000) System.IO.File.Delete(file); // keep it small
            System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// The app used to be called NotchApp: move its data (settings, clipboard history, to-dos)
    /// to %APPDATA%\Lagoon, and switch the Start-with-Windows entry to the new name and exe.
    /// </summary>
    static void MigrateFromNotchApp()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = System.IO.Path.Combine(appData, "NotchApp");
            if (Environment.GetEnvironmentVariable("NOTCHAPP_DATADIR") == null
                && System.IO.Directory.Exists(oldDir) && !System.IO.Directory.Exists(Services.AppSettings.Dir))
                System.IO.Directory.Move(oldDir, Services.AppSettings.Dir);

            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("NotchApp") != null)
            {
                run.DeleteValue("NotchApp", throwOnMissingValue: false);
                if (Environment.ProcessPath is { } exe) run.SetValue("Lagoon", $"\"{exe}\"");
            }
        }
        catch { }
    }

    /// <summary>Appends the error to %APPDATA%\Lagoon\crash.log.</summary>
    static void WriteCrash(Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Services.AppSettings.Dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(Services.AppSettings.Dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>Windows is shutting down, restarting or signing out (not the user quitting Lagoon).</summary>
    public static bool WindowsShuttingDown { get; private set; }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Keep the taskbar hidden through the restart: if we put it back now, Windows boots with the
        // full taskbar showing until Lagoon starts again. The restore values stay saved, so a real
        // Quit (or turning the dock off) still puts everything back exactly.
        WindowsShuttingDown = true;
        Note($"Lagoon closing: Windows is {(e.ReasonSessionEnding == ReasonSessionEnding.Logoff ? "signing out" : "shutting down / restarting")}");
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (_started && !WindowsShuttingDown) Note("Lagoon quit");
        // Everything is cleaned up by now (windows closed, taskbar restored). A leftover thread
        // could keep the process alive, which let a second copy start, so make sure we end.
        // The single-instance mutex is released by Windows when the process really exits.
        new Thread(() => { Thread.Sleep(3000); Environment.Exit(0); }) { IsBackground = true }.Start();
    }
}
