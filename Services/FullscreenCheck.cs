using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Windows' own verdict on "a fullscreen app is running" (the same one it uses to hold notifications
/// back during a video or game). Lagoon's window-shape check alone can be fooled: Windows keeps
/// invisible screen-sized overlay windows (Click to Do, the touch keyboard host...) that look exactly
/// like a fullscreen app when they briefly take focus, which hid the notch and dock over a normal
/// maximized window. Ask this only after the shape check already says "fullscreen" (rarely).
/// </summary>
public static class FullscreenCheck
{
    public static bool WindowsAgrees()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return true; // can't tell: trust the shape check
            return state is 2 /*BUSY: fullscreen app*/ or 3 /*D3D fullscreen*/ or 4 /*presentation mode*/;
        }
        catch { return true; }
    }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
