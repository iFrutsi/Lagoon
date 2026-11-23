using System.Runtime.InteropServices;

namespace NotchApp.Services;

public sealed class BtAudioDevice
{
    public string Name = "";
    public bool Connected;
    public bool IsHeadset;          // headphones/earbuds vs speaker
    internal List<object> Parts = new(); // KS filters to send connect/disconnect to
}

/// <summary>
/// Paired Bluetooth audio devices (headphones, earbuds, speakers) and connect/disconnect for them.
///
/// Windows has no public "connect this paired device" API. Like Windows' own panel (and tools such
/// as ToothTray), we ask the Bluetooth audio driver directly: each Bluetooth audio endpoint leads,
/// through its device topology, to a kernel-streaming filter that accepts a one-shot
/// "reconnect" or "disconnect" request.
/// </summary>
public static class BluetoothAudio
{
    public static List<BtAudioDevice> List()
    {
        var byName = new Dictionary<string, BtAudioDevice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Created by class id and used only through the interface: the enumerator is one shared
            // COM object, and AudioService already wraps it in its own class (a second wrapper class
            // made .NET fail with "Unable to cast MMDeviceEnumeratorCom to MMDeviceEnumeratorCom").
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (enumerator.EnumAudioEndpoints(eAll, DEVICE_STATEMASK_ALL, out var coll) != 0) return new();
            coll.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    if (coll.Item(i, out var dev) != 0) continue;
                    dev.GetState(out int state);
                    if ((state & (DEVICE_STATE_ACTIVE | DEVICE_STATE_UNPLUGGED)) == 0) continue; // skip disabled / not present

                    var iid = typeof(IDeviceTopology).GUID;
                    int hr = dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var topoObj);
                    if (hr != 0) { Log.Write($"bt {i}: topology hr={hr:X}"); continue; }
                    var topo = (IDeviceTopology)topoObj;
                    hr = topo.GetConnector(0, out var connector);
                    if (hr != 0) { Log.Write($"bt {i}: connector hr={hr:X}"); continue; }
                    hr = connector.GetDeviceIdConnectedTo(out var connectedId);
                    Log.Write($"bt {i}: state={state} connectedTo hr={hr:X} id={connectedId}");
                    if (hr != 0 || connectedId == null) continue;
                    if (!IsBluetooth(connectedId)) continue;
                    hr = connector.GetConnectedTo(out var other);
                    if (hr != 0 || other is not IPart part) { Log.Write($"bt {i}: connectedTo hr={hr:X} part={other is IPart}"); continue; }
                    // the control lives on the Bluetooth filter *device*: part -> its topology -> device id -> device
                    hr = part.GetTopologyObject(out var filterTopo);
                    if (hr != 0) { Log.Write($"bt {i}: filter topology hr={hr:X}"); continue; }
                    hr = filterTopo.GetDeviceId(out var filterId);
                    if (hr != 0) { Log.Write($"bt {i}: filter id hr={hr:X}"); continue; }
                    hr = enumerator.GetDevice(filterId, out var filterDev);
                    if (hr != 0) { Log.Write($"bt {i}: filter device hr={hr:X}"); continue; }
                    var ksIid = typeof(IKsControl).GUID;
                    hr = filterDev.Activate(ref ksIid, CLSCTX_ALL, IntPtr.Zero, out var ks);
                    if (hr != 0) { Log.Write($"bt {i}: ks hr={hr:X}"); continue; }

                    var (name, headset) = DeviceName(dev);
                    if (!byName.TryGetValue(name, out var d)) byName[name] = d = new BtAudioDevice { Name = name };
                    d.Connected |= (state & DEVICE_STATE_ACTIVE) != 0;
                    d.IsHeadset |= headset;
                    d.Parts.Add(ks);
                }
                catch (Exception ex) { Log.Write($"bt {i}: {ex.GetType().Name} {ex.Message}"); }
            }
        }
        catch (Exception ex) { Log.Write("bt list: " + ex.Message); }
        return byName.Values.OrderByDescending(d => d.Connected).ThenBy(d => d.Name).ToList();
    }

    /// <summary>
    /// Bluetooth audio filters: BTHENUM (stereo) / BTHHFENUM (hands-free) with Windows' own driver,
    /// or "intcbttopo" when the PC uses Intel Smart Sound's Bluetooth audio offload.
    /// </summary>
    static bool IsBluetooth(string id) =>
        id.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
        || id.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase)
        || id.Contains("intcbttopo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the driver whether it accepts connect/disconnect, without doing either.</summary>
    public static bool SupportsConnect(BtAudioDevice d)
    {
        foreach (var o in d.Parts)
        {
            try
            {
                var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = KSPROPERTY_ONESHOT_RECONNECT, Flags = KSPROPERTY_TYPE_BASICSUPPORT };
                int hr = ((IKsControl)o).KsProperty(ref prop, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                Log.Write($"bt support {d.Name}: hr={hr:X}");
                if (hr == 0 || hr == unchecked((int)0x8007007A) /* buffer too small = supported */) return true;
            }
            catch { }
        }
        return false;
    }

    public static Task Connect(BtAudioDevice d) => Send(d, KSPROPERTY_ONESHOT_RECONNECT);
    public static Task Disconnect(BtAudioDevice d) => Send(d, KSPROPERTY_ONESHOT_DISCONNECT);

    /// <summary>
    /// Runs on the calling (UI) thread on purpose: these audio COM objects can't be used from a
    /// background thread (the cast fails there). The request itself returns quickly; the radio
    /// link then changes over the next few seconds.
    /// </summary>
    static Task Send(BtAudioDevice d, uint id)
    {
        foreach (var o in d.Parts)
        {
            try
            {
                var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = id, Flags = KSPROPERTY_TYPE_GET };
                int hr = ((IKsControl)o).KsProperty(ref prop, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                Log.Write($"bt {(id == KSPROPERTY_ONESHOT_RECONNECT ? "connect" : "disconnect")} {d.Name}: hr={hr:X}");
            }
            catch (Exception ex) { Log.Write("bt send: " + ex.Message); }
        }
        return Task.CompletedTask;
    }

    /// <summary>"Headphones (WH-1000XM4 Stereo)" -> "WH-1000XM4"; both endpoints of one device share it.</summary>
    static (string name, bool headset) DeviceName(IMMDevice dev)
    {
        string friendly = "";
        try
        {
            if (dev.OpenPropertyStore(0, out var store) == 0)
            {
                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out var pv) == 0 && pv.vt == 31 /*VT_LPWSTR*/)
                    friendly = Marshal.PtrToStringUni(pv.p) ?? "";
                PropVariantClear(ref pv);
            }
        }
        catch { }
        bool headset = friendly.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase)
                    || friendly.StartsWith("Headset", StringComparison.OrdinalIgnoreCase)
                    || friendly.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);
        int a = friendly.IndexOf('('), b = friendly.LastIndexOf(')');
        var name = a >= 0 && b > a ? friendly[(a + 1)..b] : friendly;
        foreach (var suffix in new[] { " Hands-Free AG Audio", " Hands-Free", " Stereo" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return (name.Trim().Length > 0 ? name.Trim() : "Bluetooth device", headset);
    }

    // ---- COM ----
    const int eAll = 2, DEVICE_STATEMASK_ALL = 0xF, DEVICE_STATE_ACTIVE = 1, DEVICE_STATE_UNPLUGGED = 8, CLSCTX_ALL = 23;
    const uint KSPROPERTY_ONESHOT_RECONNECT = 0, KSPROPERTY_ONESHOT_DISCONNECT = 1, KSPROPERTY_TYPE_GET = 1, KSPROPERTY_TYPE_BASICSUPPORT = 0x200;
    static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    static readonly PROPERTYKEY PKEY_Device_FriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [StructLayout(LayoutKind.Sequential)] struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Sequential)] struct PROPVARIANT { public ushort vt, r1, r2, r3; public IntPtr p; public IntPtr p2; }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
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
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        [PreserveSig] int GetConnectorType(out int type); // COM only cares about the order
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPart
    {
        [PreserveSig] int GetName(IntPtr name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId(IntPtr id);
        [PreserveSig] int GetPartType(out int type);
        [PreserveSig] int GetSubType(out Guid subtype);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr iface);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
        [PreserveSig] int Activate(int clsContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, int propertyLength, IntPtr data, int dataLength, out int bytesReturned);
    }
}
