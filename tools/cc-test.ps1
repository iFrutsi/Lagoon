# Screenshots the dock's quick-settings card and its Bluetooth page (nothing is clicked).
# Closes the app gracefully between runs (taskbar restored), starts it normally at the end.
param([string]$Out = "$env:TEMP\notch-cc")
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class CC { public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static void Close() { EnumWindows((h,l)=>{ var s=new StringBuilder(64); GetWindowText(h,s,64); if (s.ToString()=="Lagoon") PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); } }
'@
[CC]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$exe = "$PSScriptRoot\..\publish\Lagoon.exe"
[CC]::Close(); Get-Process Lagoon -ErrorAction SilentlyContinue | ForEach-Object { $_.WaitForExit(8000) | Out-Null }
foreach ($mode in $(if ($env:CC_MODES) { $env:CC_MODES -split "," } else { "controls", "bluetooth" })) {
    $env:NOTCHAPP_DOCKPOPUP = $mode; $p = Start-Process $exe -PassThru; $env:NOTCHAPP_DOCKPOPUP = $null
    Start-Sleep $(if ($env:CC_WAIT) { [int]$env:CC_WAIT } else { 6 })
    $bmp = New-Object System.Drawing.Bitmap 520, 700; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(700, 330, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Out "$mode.png")); $g.Dispose(); $bmp.Dispose()
    [CC]::Close(); $p.WaitForExit(8000) | Out-Null; Start-Sleep 1
}
Start-Process $exe | Out-Null; "started again"


