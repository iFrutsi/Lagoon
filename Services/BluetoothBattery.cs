using System.Runtime.InteropServices;

namespace NotchApp.Services;

/// <summary>
/// Battery level of connected Bluetooth headphones, the same number Windows shows in
/// Settings > Bluetooth. Windows puts it on the headset's hands-free device node as
/// DEVPKEY_Bluetooth_Battery. Only present Bluetooth (BTHENUM) devices are checked, a few
/// dozen at most; called only while the Widgets tab is open.
/// </summary>
public static class BluetoothBattery
{
    public readonly record struct Level(string Name, int Percent);

    public static Level? Read()
    {
        var all = ReadAll();
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>Every connected Bluetooth headset that reports its battery (usually one).</summary>
    public static List<Level> ReadAll()
    {
        var all = new List<Level>();
        try
        {
            const uint flags = 0x1 /*FILTER_ENUMERATOR*/ | 0x100 /*FILTER_PRESENT*/;
            if (CM_Get_Device_ID_List_Size(out uint len, "BTHENUM", flags) != 0 || len == 0) return all;
            var buf = new char[len];
            if (CM_Get_Device_ID_List("BTHENUM", buf, len, flags) != 0) return all;

            foreach (var id in new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (CM_Locate_DevNode(out uint node, id, 0) != 0) continue;
                if (ReadUInt8(node, BatteryKey) is not byte pct) continue;
                var name = ReadString(node, FriendlyNameKey) ?? "Headphones";
                // the node is the headset's hands-free profile: "Space Travel 2 Ultra Hands-Free AG" -> "Space Travel 2 Ultra"
                foreach (var suffix in new[] { " Hands-Free AG", " Hands-Free", " Stereo", " Avrcp Transport" })
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
                if (all.Any(l => l.Name == name)) continue;
                all.Add(new Level(name, Math.Clamp((int)pct, 0, 100)));
            }
        }
        catch { }
        return all;
    }

    static byte? ReadUInt8(uint node, DEVPROPKEY key)
    {
        var data = new byte[4];
        uint size = (uint)data.Length;
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, data, ref size, 0) != 0) return null;
        return type == 0x3 /*DEVPROP_TYPE_BYTE*/ && size >= 1 ? data[0] : null;
    }

    static string? ReadString(uint node, DEVPROPKEY key)
    {
        var data = new byte[512];
        uint size = (uint)data.Length;
        if (CM_Get_DevNode_PropertyW(node, ref key, out uint type, data, ref size, 0) != 0 || type != 0x12 /*STRING*/) return null;
        return System.Text.Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    static readonly DEVPROPKEY BatteryKey = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
    static readonly DEVPROPKEY FriendlyNameKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_Size(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List(string filter, char[] buffer, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNode(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_PropertyW(uint node, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);
}
