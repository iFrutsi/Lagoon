# Screenshots the pill and the open card for each theme (uses the app's self-test).
param([string]$Out = "$env:TEMP\notch-themes")
New-Item -ItemType Directory -Force $Out | Out-Null
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public static class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[Dpi]::SetProcessDPIAware() | Out-Null
$settings = Join-Path $env:APPDATA "NotchApp\settings.json"
$backup = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }
New-Item -ItemType Directory -Force (Split-Path $settings) | Out-Null
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width

function Shot($name, $w, $h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($sw - $w) / 2, 0, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out $name)); $g.Dispose(); $bmp.Dispose()
}

$theme = @{ Black = 0; Glass = 1; Liquid = 3 }
$list = if ($env:SHOT_THEMES) { $env:SHOT_THEMES -split ',' } else { "Black", "Glass", "Liquid" }
foreach ($t in $list) {
    "{ `"Theme`": $($theme[$t]) }" | Set-Content $settings -Encoding ascii
    $env:NOTCHAPP_SELFTEST = "1"
    $p = Start-Process "$PSScriptRoot\..\publish\Lagoon.exe" -PassThru
    $env:NOTCHAPP_SELFTEST = $null
    Start-Sleep -Milliseconds 3000; Shot "$t-line.png" 600 60
    Start-Sleep -Milliseconds 1300; Shot "$t-pill.png" 600 60
    Start-Sleep -Milliseconds 1600; Shot "$t-card.png" 820 300
    $p.WaitForExit(15000) | Out-Null
}
if ($backup) { $backup | Set-Content $settings -NoNewline } else { Remove-Item $settings }
Get-ChildItem $Out | Select-Object -ExpandProperty FullName
