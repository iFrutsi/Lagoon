# Turns the dock on (test settings: taskbar auto-hide NOT touched), screenshots the bottom of
# the screen, and checks the screen space it reserves is given back when the app quits.
param([string]$Out = "$env:TEMP\notch-dock", [int]$Theme = 0)
New-Item -ItemType Directory -Force $Out | Out-Null
$data = Join-Path $env:TEMP "notchapp-docktest"
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $data | Out-Null
"{ `"Theme`": $Theme, `"DockEnabled`": true, `"HideTaskbar`": false }" | Set-Content (Join-Path $data "settings.json") -Encoding ascii

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public static class DpiD { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[DpiD]::SetProcessDPIAware() | Out-Null
$scr = [System.Windows.Forms.Screen]::PrimaryScreen
"work area before: $($scr.WorkingArea)"

$env:NOTCHAPP_DATADIR = $data
$p = Start-Process "$PSScriptRoot\..\publish\Lagoon.exe" -PassThru
$env:NOTCHAPP_DATADIR = $null
Start-Sleep 5
"work area with dock: $([System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea)"
$b = $scr.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, 170
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, $b.Height - 170, 0, 0, $bmp.Size)
$bmp.Save((Join-Path $Out "dock.png")); $g.Dispose(); $bmp.Dispose()

Stop-Process -Id $p.Id   # hard kill: the worst case for giving space back
Start-Sleep 3
"work area after quit: $([System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea)"
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
