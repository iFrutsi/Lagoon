# Runs the app's self-test (cycles line -> pill -> card -> ... -> hidden) and samples the
# screen pixel at the notch's centre. Prints every time the pixel changes between
# "notch" (black / indicator colour) and "gone", next to the mode the app was in.
param([string]$Exe = "$PSScriptRoot\..\publish\Lagoon.exe")

Add-Type @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic; using System.Diagnostics;
public static class Px {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
  [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
  public static List<string> Sample(int ms) {
    var log = new List<string>(); var sw = Stopwatch.StartNew(); int x = GetSystemMetrics(0) / 2; string last = null; int n = 0;
    while (sw.ElapsedMilliseconds < ms) {
      IntPtr dc = GetDC(IntPtr.Zero); uint c = GetPixel(dc, x, 2); ReleaseDC(IntPtr.Zero, dc); n++;
      string s = (c & 0xFFFFFF) == 0 ? "notch" : "gone #" + (c & 0xFFFFFF).ToString("X6");
      if (s != last) { log.Add(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); last = s; }
    }
    log.Add("samples: " + n); return log; } }
'@
[Px]::SetProcessDPIAware() | Out-Null
Remove-Item "$env:TEMP\Lagoon.log" -ErrorAction SilentlyContinue
$env:NOTCHAPP_LOG = "1"; $env:NOTCHAPP_SELFTEST = "1"
Start-Process $Exe
$px = [Px]::Sample(15000)
$env:NOTCHAPP_SELFTEST = $null
$modes = Get-Content "$env:TEMP\Lagoon.log" | Where-Object { $_ -match "selftest" }
($px + $modes) | Sort-Object
