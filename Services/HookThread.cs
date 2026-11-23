using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// One small background thread that owns Lagoon's low-level keyboard/mouse hooks.
/// Every key press and click in Windows waits for a low-level hook to return, so the hooks must
/// never sit on the UI thread (busy for a moment = the whole keyboard stalls) and must never make
/// blocking calls themselves. Callbacks only note what happened and <see cref="Post"/> the real
/// work to a worker thread; this thread does nothing but answer the hooks.
/// </summary>
static class HookThread
{
    static Thread? _thread;
    static uint _tid;
    static readonly ConcurrentQueue<Action> _work = new();
    static readonly object _lock = new();

    static void Ensure()
    {
        lock (_lock)
        {
            if (_thread != null) return;
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "Lagoon input hooks", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
            ready.Wait();
        }
    }

    static void Loop(ManualResetEventSlim ready)
    {
        _tid = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // creates this thread's message queue
        ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_RUN)
            {
                while (_work.TryDequeue(out var a))
                    try { a(); } catch (Exception ex) { Log.Write("hook work failed: " + ex.Message); }
            }
            else
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
    }

    /// <summary>Runs <paramref name="a"/> soon on a worker thread, never on the hook thread: if it waits
    /// (e.g. on a busy Explorer), the hooks keep answering and input keeps flowing.</summary>
    public static void Post(Action a) => ThreadPool.UnsafeQueueUserWorkItem(static s =>
    {
        try { ((Action)s!)(); } catch (Exception ex) { Log.Write("hook work failed: " + ex.Message); }
    }, a);

    static void RunOnHookThread(Action a)
    {
        Ensure();
        _work.Enqueue(a);
        PostThreadMessage(_tid, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Installs a low-level hook on the hook thread (they belong to the thread that sets them).</summary>
    public static IntPtr SetHook(int id, HookProc proc)
    {
        var done = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnHookThread(() =>
        {
            var mod = GetModuleHandle(null); // our exe
            done.SetResult(SetWindowsHookEx(id, proc, mod, 0));
        });
        return done.Task.Wait(2000) ? done.Task.Result : IntPtr.Zero;
    }

    public static void Unhook(IntPtr hook)
    {
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook); // allowed from any thread
    }

    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    const uint WM_RUN = 0x8000 + 1; // WM_APP + 1

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
}
