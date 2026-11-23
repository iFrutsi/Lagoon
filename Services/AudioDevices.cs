using System.Runtime.InteropServices;

namespace NotchApp.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Speakers/headphones and microphones that are plugged in and enabled, and switching the
/// default. Listing uses Core Audio; switching uses IPolicyConfig, which Windows' own sound
/// panel uses (undocumented but stable; it's what audio switchers like EarTrumpet rely on).
/// </summary>
public static class AudioDevices
{
    const int eRender = 0, eCapture = 1, DEVICE_STATE_ACTIVE = 1, eConsole = 0;

    public static List<AudioDevice> Outputs() => List(eRender);
    public static List<AudioDevice> Inputs() => List(eCapture);

    static List<AudioDevice> List(int flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            var en = Enumerator();
            string? defaultId = null;
            if (en.GetDefaultAudioEndpoint(flow, eConsole, out var def) == 0) def.GetId(out defaultId);
            if (en.EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, out var coll) != 0) return result;
            coll.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (coll.Item(i, out var dev) != 0) continue;
                dev.GetId(out var id);
                result.Add(new AudioDevice(id, FriendlyName(dev), id == defaultId));
            }
        }
        catch (Exception ex) { Log.Write("audio list: " + ex.Message); }
        return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Makes this device the default for everything (apps, communications, system sounds).</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"))!)!;
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
            return true;
        }
        catch (Exception ex) { Log.Write("audio set default: " + ex.Message); return false; }
    }

    static IMMDeviceEnumerator Enumerator() =>
        (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;

    static string FriendlyName(IMMDevice dev)
    {
        try
        {
            if (dev.OpenPropertyStore(0, out var store) != 0) return "Audio device";
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return "Audio device";
            try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) ?? "Audio device" : "Audio device"; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return "Audio device"; }
    }

    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
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
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string id);
        [PreserveSig] int SetDeviceFormat(string id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string id, int defaultPeriod, IntPtr defaultP, IntPtr minP);
        [PreserveSig] int SetProcessingPeriod(string id, IntPtr period);
        [PreserveSig] int GetShareMode(string id, IntPtr mode);
        [PreserveSig] int SetShareMode(string id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string id, int store, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(string id, int visible);
    }
}
