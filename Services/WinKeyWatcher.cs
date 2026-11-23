using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Start only opens when the real taskbar exists on screen, so while we keep it hidden the
/// Windows key would do nothing. This watches for the Windows key (and only that key) and
/// un-hides the taskbar just before Start opens; auto-hide keeps it tucked away and the dock's
/// watchdog hides it again once Start has closed. Only active while the taskbar is hidden.
/// </summary>
public sealed class WinKeyWatcher : IDisposable
{
    readonly HookThread.HookProc _proc;  // kept alive while hooked
    static readonly Action _showForStart = TaskbarHider.ShowForStart;
    IntPtr _hook;
    bool _winDown;

    public WinKeyWatcher() => _proc = Hook;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = HookThread.SetHook(WH_KEYBOARD_LL, _proc);
    }

    public void Stop()
    {
        HookThread.Unhook(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Runs for EVERY key press in Windows, on the hook thread: only look at the key code and
    /// return at once. Showing the taskbar (a call into Explorer) happens after we've returned.</summary>
    IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode; nothing else is read
            if (vk is VK_LWIN or VK_RWIN)
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                // once per press, not for every auto-repeat while the key is held
                if (down && !_winDown) HookThread.Post(_showForStart);
                _winDown = down;
            }
        }
        return HookThread.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Stop();

    const int WH_KEYBOARD_LL = 13, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    static readonly IntPtr WM_KEYDOWN = (IntPtr)0x0100, WM_SYSKEYDOWN = (IntPtr)0x0104;
}
