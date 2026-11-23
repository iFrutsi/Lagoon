# Screenshots the notch card on each tab (media, controls...) using your real settings.
param([string]$Out = "$env:TEMP\notch-cards", [string[]]$Tabs = @("media", "controls"))
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public static class DpCard { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[DpCard]::SetProcessDPIAware() | Out-Null
$exe = "$PSScriptRoot\..\publish\Lagoon.exe"
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
& "$PSScriptRoot\close-notch.ps1"
foreach ($t in $Tabs) {
    $env:NOTCHAPP_OPENCARD = $t; $p = Start-Process $exe -PassThru; $env:NOTCHAPP_OPENCARD = $null
    Start-Sleep 5
    $bmp = New-Object System.Drawing.Bitmap 820, 330; $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($sw - 820) / 2, 0, 0, 0, $bmp.Size); $bmp.Save((Join-Path $Out "$t.png")); $g.Dispose(); $bmp.Dispose()
    & "$PSScriptRoot\close-notch.ps1"; Start-Sleep 1
}
Start-Process $exe | Out-Null
