using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;

namespace NotchApp.Services;

public sealed record WifiNetwork(string Ssid, int Signal, string Auth, bool Secured, bool Saved, bool Connected);

/// <summary>
/// Nearby Wi-Fi networks and connecting, through Windows' own `netsh wlan` (no extra
/// permissions). Saved networks connect directly; a new secured network gets a profile with
/// the password you type (Windows stores it, encrypted, like it does for its own panel).
/// </summary>
public static class WifiNetworks
{
    public static async Task<List<WifiNetwork>> ListAsync()
    {
        var networksText = await Netsh("wlan show networks mode=bssid");
        var current = Regex.Match(await Netsh("wlan show interfaces"), @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
        string? connected = current.Success ? current.Groups[1].Value.Trim() : null;
        var saved = Regex.Matches(await Netsh("wlan show profiles"), @"All User Profile\s*:\s*(.+)$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new List<WifiNetwork>();
        // blocks start with "SSID n : name"
        foreach (Match block in Regex.Matches(networksText, @"^SSID \d+ : (.*?)\r?\n(.*?)(?=^SSID \d+ :|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            var ssid = block.Groups[1].Value.Trim();
            if (ssid.Length == 0) continue; // hidden network
            var body = block.Groups[2].Value;
            var auth = Regex.Match(body, @"Authentication\s*:\s*(.+)").Groups[1].Value.Trim();
            int signal = Regex.Matches(body, @"Signal\s*:\s*(\d+)%").Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
            list.Add(new WifiNetwork(ssid, signal, auth, !auth.Equals("Open", StringComparison.OrdinalIgnoreCase),
                saved.Contains(ssid), ssid == connected));
        }
        return list.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Saved).ThenByDescending(n => n.Signal).ToList();
    }

    /// <summary>
    /// Ask the Wi-Fi adapter to scan now (what Windows' own panel does when it opens). Without
    /// this, Windows only has a stale list, often just the network you're on. Results arrive a
    /// few seconds later.
    /// </summary>
    public static void Scan()
    {
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return;
            try
            {
                if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return;
                int n = System.Runtime.InteropServices.Marshal.ReadInt32(list);
                for (int i = 0; i < n; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: 2 DWORDs, then 532-byte items starting with the interface GUID
                    var g = System.Runtime.InteropServices.Marshal.PtrToStructure<Guid>(list + 8 + i * 532);
                    WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                }
                WlanFreeMemory(list);
            }
            finally { WlanCloseHandle(h, IntPtr.Zero); }
        }
        catch (Exception ex) { Log.Write("wifi scan: " + ex.Message); }
    }

    [System.Runtime.InteropServices.DllImport("wlanapi.dll")] static extern int WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr h, IntPtr reserved, out IntPtr list);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr h, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr h, IntPtr reserved);
    [System.Runtime.InteropServices.DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    public static Task Connect(string ssid) => Netsh($"wlan connect name=\"{ssid}\"");
    public static Task Disconnect() => Netsh("wlan disconnect");

    /// <summary>Saves a profile for a new network (with its password), then connects.</summary>
    public static async Task ConnectNew(WifiNetwork n, string? password)
    {
        string authType, encryption;
        if (!n.Secured) { authType = "open"; encryption = "none"; }
        else if (n.Auth.Contains("WPA3", StringComparison.OrdinalIgnoreCase)) { authType = "WPA3SAE"; encryption = "AES"; }
        else { authType = "WPA2PSK"; encryption = "AES"; }
        string name = SecurityElement.Escape(n.Ssid)!;
        string key = n.Secured
            ? $"<sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(password ?? "")}</keyMaterial></sharedKey>"
            : "";
        string xml = $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{name}</name>" +
                     $"<SSIDConfig><SSID><name>{name}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>auto</connectionMode>" +
                     $"<MSM><security><authEncryption><authentication>{authType}</authentication><encryption>{encryption}</encryption><useOneX>false</useOneX></authEncryption>{key}</security></MSM></WLANProfile>";
        var file = Path.Combine(Path.GetTempPath(), $"lagoon-wifi-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(file, xml);
            await Netsh($"wlan add profile filename=\"{file}\" user=current");
        }
        finally { try { File.Delete(file); } catch { } } // the password never stays in a file
        await Connect(n.Ssid);
    }

    static async Task<string> Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output;
        }
        catch { return ""; }
    }
}
