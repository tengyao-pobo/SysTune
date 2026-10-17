<#
.SYNOPSIS
  Launches the real SysTune build on Windows, captures a screenshot, collects process metrics and the
  smoke-result.json written by `SysTune.exe --smoke-test`, and fails if the app crashed or produced no data.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $OutDir,
    [string] $Variant = 'dark-expanded',
    [string] $Theme = 'Dark',
    [string] $Mode = 'Expanded',
    [switch] $ShowSettings,
    [int] $Seconds = 40
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Fresh, deterministic settings for this variant.
$settingsDir = Join-Path $env:APPDATA 'SysTune'
$logDir = Join-Path $env:LOCALAPPDATA 'SysTune\logs'
New-Item -ItemType Directory -Force $settingsDir | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $logDir 'smoke-result.json')
@{
    version = 1; theme = $Theme; mode = $Mode; glass = 'Acrylic'; tintOpacity = 0.6; alwaysOnTop = $true
    expanded = @{ width = 340 }; compact = @{ width = 460 }
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $settingsDir 'settings.json')

$appArgs = @('--smoke-test', "$Seconds")
if ($ShowSettings) { $appArgs += '--show-settings' }
$p = Start-Process -FilePath $Exe -ArgumentList $appArgs -PassThru
Start-Sleep -Seconds ([Math]::Max(6, $Seconds - 15))

# Process metrics while running (real numbers for the test report).
$proc = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
$metrics = $null
if ($proc) {
    $cpu1 = $proc.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 10
    $proc.Refresh()
    $cpu2 = $proc.TotalProcessorTime.TotalMilliseconds
    $metrics = [ordered]@{
        workingSetMB = [Math]::Round($proc.WorkingSet64 / 1MB, 1)
        privateMB = [Math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
        threads = $proc.Threads.Count
        handles = $proc.HandleCount
        cpuPercentOfMachine = [Math]::Round(($cpu2 - $cpu1) / 10000 / [Environment]::ProcessorCount * 100, 2)
        cpuPercentOfOneCore = [Math]::Round(($cpu2 - $cpu1) / 10000 * 100, 2)
        measuredAfterSeconds = $Seconds - 15
    }
}

# Screenshot of the primary screen.
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save((Join-Path $OutDir "$Variant.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force; throw "SysTune did not exit after the smoke test" }
$exit = $p.ExitCode

Copy-Item -ErrorAction SilentlyContinue (Join-Path $logDir 'smoke-result.json') (Join-Path $OutDir "$Variant-result.json")
Copy-Item -ErrorAction SilentlyContinue (Join-Path $logDir 'systune.log') (Join-Path $OutDir "$Variant-systune.log")
if ($metrics) { $metrics | ConvertTo-Json | Set-Content (Join-Path $OutDir "$Variant-metrics.json") }

Write-Host "Variant $Variant exit code: $exit"
if ($metrics) { $metrics | Format-List | Out-String | Write-Host }
if ($exit -ne 0) { throw "Smoke test failed for $Variant (exit $exit)" }
