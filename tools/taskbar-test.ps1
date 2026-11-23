# Uses your real settings (dock + hide taskbar). Checks the taskbar is fully hidden and no
# screen space is reserved, screenshots the bottom, then quits the app GRACEFULLY and checks the
# taskbar is back. Finally starts the app again.
param([string]$Out = "$env:TEMP\notch-taskbar")
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class TB {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(int a, int b, ref R r, int c);
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static string WorkArea() { var r = new R(); SystemParametersInfo(0x30, 0, ref r, 0); return r.Ri + "x" + r.B; }
  public static bool TaskbarVisible() { return IsWindowVisible(FindWindow("Shell_TrayWnd", null)); }
  public static void CloseNotch() { EnumWindows((h,l)=>{ var s=new StringBuilder(64); GetWindowText(h,s,64); if (s.ToString()=="Lagoon") PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); }
}
'@
[TB]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$exe = "$PSScriptRoot\..\publish\Lagoon.exe"

$p = Start-Process $exe -PassThru; Start-Sleep 5
"running: taskbar visible=$([TB]::TaskbarVisible())  work area=$([TB]::WorkArea())"
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, 140; $g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, $b.Height - 140, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Out "bottom.png")); $g.Dispose(); $bmp.Dispose()

[TB]::CloseNotch()
if (-not $p.WaitForExit(8000)) { "did not close gracefully" }
Start-Sleep 1
"after quit: taskbar visible=$([TB]::TaskbarVisible())  work area=$([TB]::WorkArea())"

Start-Process $exe | Out-Null
"started again"
