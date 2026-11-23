# Opens the card on each new tab with test data and screenshots it.
# Uses a throwaway data folder, so your real clipboard history and settings are untouched.
# Your clipboard text is saved first and put back at the end.
param([string]$Out = "$env:TEMP\notch-tabs", [int]$Theme = 0)
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = "$PSScriptRoot\..\publish\Lagoon.exe"
$data = Join-Path $env:TEMP "notchapp-testdata"
Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $data | Out-Null
@"
{ "Theme": $Theme, "WeatherCity": "London", "WorldCity": "Tokyo", "WorldZone": "Tokyo Standard Time",
  "Todos": [ { "Text": "Send the invoice", "Done": false }, { "Text": "Book the dentist", "Done": false } ] }
"@ | Set-Content (Join-Path $data "settings.json") -Encoding ascii

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System.Runtime.InteropServices; public static class DpiT { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }'
[DpiT]::SetProcessDPIAware() | Out-Null
$sw = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
function Shot($name) {
    $bmp = New-Object System.Drawing.Bitmap 820, 330
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(($sw - 820) / 2, 0, 0, 0, $bmp.Size)
    $bmp.Save((Join-Path $Out "$name.png")); $g.Dispose(); $bmp.Dispose()
}
function Run($tab) {
    $env:NOTCHAPP_DATADIR = $data; $env:NOTCHAPP_OPENCARD = $tab
    $p = Start-Process $exe -PassThru
    $env:NOTCHAPP_OPENCARD = $null; $env:NOTCHAPP_DATADIR = $null
    return $p
}

# --- clipboard: copy a few things while the app is running ---
$saved = Get-Clipboard -Raw -ErrorAction SilentlyContinue
$p = Run "media"; Start-Sleep 3
Set-Clipboard -Value "Meeting moved to 4pm - bring the Q3 numbers and the updated slides"; Start-Sleep -Milliseconds 600
Set-Clipboard -Value "https://github.com/anthropics/claude-code/issues"; Start-Sleep -Milliseconds 600
Set-Clipboard -Path "$env:WINDIR\notepad.exe", "$env:WINDIR\regedit.exe"; Start-Sleep -Milliseconds 600
Stop-Process -Id $p.Id; Start-Sleep 1
if ($saved) { Set-Clipboard -Value $saved }

$p = Run "clipboard"; Start-Sleep 4; Shot "clipboard"; Stop-Process -Id $p.Id; Start-Sleep 1
$p = Run "widgets"; Start-Sleep 6; Shot "widgets"; Stop-Process -Id $p.Id; Start-Sleep 1

# --- Claude Code: send hook events the way the real hooks would ---
$p = Run "agents"; Start-Sleep 3
function Hook($json) { $json | curl.exe -s -m 2 -X POST --data-binary "@-" "http://127.0.0.1:47321/claude" | Out-Null }
Hook '{"hook_event_name":"UserPromptSubmit","session_id":"a","cwd":"C:\\code\\storefront","prompt":"Fix tax rounding at checkout"}'
Hook '{"hook_event_name":"Notification","session_id":"a","cwd":"C:\\code\\storefront","message":"Claude needs your permission to use Bash"}'
Hook '{"hook_event_name":"UserPromptSubmit","session_id":"b","cwd":"C:\\code\\api","prompt":"Rate limits for the public API"}'
Hook '{"hook_event_name":"Stop","session_id":"b","cwd":"C:\\code\\api"}'
Hook '{"hook_event_name":"UserPromptSubmit","session_id":"c","cwd":"C:\\code\\notchapp","prompt":"Redesign the agents tab"}'
Hook '{"hook_event_name":"PreToolUse","session_id":"c","cwd":"C:\\code\\notchapp","tool_name":"Edit","tool_input":{"file_path":"C:\\code\\notchapp\\AgentsTab.cs"}}'
Start-Sleep 3; Shot "agents"; Stop-Process -Id $p.Id

Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $Out | Select-Object -ExpandProperty FullName
