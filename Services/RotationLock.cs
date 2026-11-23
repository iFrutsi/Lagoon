using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Screen auto-rotation lock for 2-in-1s. Reading uses the documented GetAutoRotationState.
/// Changing it has no documented API; like Windows' own toggle and other rotation-lock tools we
/// call user32's SetAutoRotation (export ordinal 2507). If that ever isn't there, we return false
/// and the caller opens Display settings instead.
/// </summary>
public static class RotationLock
{
    const int AR_DISABLED = 0x1, AR_NOSENSOR = 0x10, AR_NOT_SUPPORTED = 0x20;

    public static bool Supported => GetAutoRotationState(out int s) && (s & (AR_NOSENSOR | AR_NOT_SUPPORTED)) == 0;

    /// <summary>true = rotation is locked by the user.</summary>
    public static bool Locked => GetAutoRotationState(out int s) && (s & AR_DISABLED) != 0;

    public static bool TrySetLocked(bool locked)
    {
        try
        {
            SetAutoRotation(!locked);
            return Locked == locked;
        }
        catch (Exception ex)
        {
            Log.Write("rotation: " + ex.Message);
            return false;
        }
    }

    [DllImport("user32.dll")] static extern bool GetAutoRotationState(out int state);
    [DllImport("user32.dll", EntryPoint = "#2507")] static extern bool SetAutoRotation(bool enable);
}
