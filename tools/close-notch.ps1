# Closes NotchApp gracefully (so it puts the Windows taskbar back), waits for it to exit.
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class CloseNotchApp {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static void Close() { EnumWindows((h,l)=>{ var s=new StringBuilder(64); GetWindowText(h,s,64); if (s.ToString()=="Lagoon") PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); }
}
'@
[CloseNotchApp]::Close()
Get-Process Lagoon -ErrorAction SilentlyContinue | ForEach-Object { if (-not $_.WaitForExit(8000)) { "NotchApp $($_.Id) did not exit" } }
