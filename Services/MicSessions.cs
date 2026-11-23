using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Which app has a microphone stream open right now, straight from Windows' audio engine
/// (active capture sessions on every microphone). A second source next to the privacy record,
/// so calls (WhatsApp, Meet in a browser, Discord...) are caught even when that record is late.
/// </summary>
public static class MicSessions
{
    const int eCapture = 1, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23, AudioSessionStateActive = 1;

    /// <summary>A friendly name of the first app capturing audio, or null if none.</summary>
    public static string? ActiveApp()
    {
        try
        {
            var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (en.EnumAudioEndpoints(eCapture, DEVICE_STATE_ACTIVE, out var coll) != 0) return null;
            coll.GetCount(out uint n);
            int self = Environment.ProcessId;
            for (uint i = 0; i < n; i++)
            {
                if (coll.Item(i, out var dev) != 0) continue;
                var iid = typeof(IAudioSessionManager2).GUID;
                if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o) != 0) continue;
                var mgr = (IAudioSessionManager2)o;
                if (mgr.GetSessionEnumerator(out var sessions) != 0) continue;
                sessions.GetCount(out int count);
                for (int s = 0; s < count; s++)
                {
                    if (sessions.GetSession(s, out var ctl) != 0) continue;
                    ctl.GetState(out int state);
                    if (state != AudioSessionStateActive) continue;
                    if (ctl is not IAudioSessionControl2 ctl2 || ctl2.GetProcessId(out uint pid) != 0 || pid == 0 || pid == self) continue;
                    if (ctl2.IsSystemSoundsSession() == 0) continue; // S_OK = system sounds
                    return FriendlyName((int)pid);
                }
            }
        }
        catch (Exception ex) { Log.Write("mic sessions: " + ex.Message); }
        return null;
    }

    static string FriendlyName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName.ToLowerInvariant();
            // browsers: call it Google Meet when a Meet tab is the window title
            if (name is "chrome" or "msedge" or "firefox" or "brave")
            {
                foreach (var w in Process.GetProcessesByName(p.ProcessName))
                    using (w) if (w.MainWindowTitle.Contains("Meet", StringComparison.OrdinalIgnoreCase)) return "Google Meet";
            }
            // apps built on WebView2 (the new WhatsApp, Teams): name the app, not "WebView2"
            if (name == "msedgewebview2")
            {
                foreach (var app in new[] { "WhatsApp", "ms-teams", "Teams" })
                    if (Process.GetProcessesByName(app).Length > 0) return app == "ms-teams" ? "Teams" : app;
                return "Call";
            }
            string? desc = null;
            try { desc = p.MainModule?.FileVersionInfo.FileDescription; } catch { }
            var label = string.IsNullOrWhiteSpace(desc) ? p.ProcessName : desc.Trim();
            // e.g. the new WhatsApp's process is "WhatsApp.Root"
            if (label.EndsWith(".Root", StringComparison.OrdinalIgnoreCase)) label = label[..^5];
            return label;
        }
        catch { return "Microphone"; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(IntPtr name);
        [PreserveSig] int SetDisplayName(string name, IntPtr ctx);
        [PreserveSig] int GetIconPath(IntPtr path);
        [PreserveSig] int SetIconPath(string path, IntPtr ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr n);
        [PreserveSig] int GetSessionIdentifier(IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
    }
}
