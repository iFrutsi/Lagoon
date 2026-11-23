# Screenshots the dock with its calendar and notifications cards open. Uses your real settings
# (dock + hidden taskbar), closes the app gracefully each time (so the taskbar is restored),
# then starts the app normally at the end.
param([string]$Out = "$env:TEMP\notch-dockpopup")
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class DP {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static void CloseNotch() { EnumWindows((h,l)=>{ var s=new StringBuilder(64); GetWindowText(h,s,64); if (s.ToString()=="Lagoon") PostMessage(h, 0x10, IntPtr.Zero, IntPtr.Zero); return true; }, IntPtr.Zero); }
}
'@
[DP]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$exe = "$PSScriptRoot\..\publish\Lagoon.exe"
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
Get-Process Lagoon -ErrorAction SilentlyContinue | ForEach-Object { [DP]::CloseNotch(); $_.WaitForExit(8000) | Out-Null }

foreach ($mode in "calendar", "notifications") {
    $env:NOTCHAPP_DOCKPOPUP = $mode
    $p = Start-Process $exe -PassThru
    $env:NOTCHAPP_DOCKPOPUP = $null
    Start-Sleep 5
    $bmp = New-Object System.Drawing.Bitmap 760, 700; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($b.Width - 760) / 2, $b.Height - 700, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Out "$mode.png")); $g.Dispose(); $bmp.Dispose()
    [DP]::CloseNotch(); if (-not $p.WaitForExit(8000)) { "did not close gracefully" }
    Start-Sleep 1
}
"taskbar restored between runs: $([DP]::IsWindowVisible([DP]::FindWindow('Shell_TrayWnd', $null)))"
Start-Process $exe | Out-Null
"started again"
