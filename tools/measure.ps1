# Samples NotchApp's memory and CPU, the way Task Manager reports them.
# Usage: powershell -File tools\measure.ps1 [-Seconds 30]
param([int]$Seconds = 30)

$p = Get-Process Lagoon -ErrorAction Stop | Select-Object -First 1
$cores = [Environment]::ProcessorCount
$cpu0 = $p.TotalProcessorTime
$t0 = Get-Date
$priv = @()
for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep 1
    $c = Get-Counter "\Process(Lagoon)\Working Set - Private" -ErrorAction SilentlyContinue
    if ($c) { $priv += $c.CounterSamples[0].CookedValue / 1MB }
}
$p.Refresh()
$cpuPct = ($p.TotalProcessorTime - $cpu0).TotalMilliseconds / ((Get-Date) - $t0).TotalMilliseconds / $cores * 100

[pscustomobject]@{
    'Task Manager memory (MB)' = [math]::Round(($priv | Measure-Object -Average).Average, 1)
    'Working set (MB)'         = [math]::Round($p.WorkingSet64 / 1MB, 1)
    'Private bytes (MB)'       = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    'CPU (% of total)'         = [math]::Round($cpuPct, 2)
    'Threads'                  = $p.Threads.Count
} | Format-List
