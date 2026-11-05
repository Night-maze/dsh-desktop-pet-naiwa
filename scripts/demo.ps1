#requires -version 5.1
<#
.SYNOPSIS
  Run the pet UI without DSH, for visual and gesture testing.

.DESCRIPTION
  Generates a per-run demo config with absolute asset paths, then launches the
  same DesktopPet.ps1 used by the plugin.  The demo labels its stats as
  "演示" so it can never be mistaken for real billing data.
#>
[CmdletBinding()]
param(
    [string]$DataDir = '',
    [double]$Scale = 1.0
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginRoot = Split-Path -Parent $scriptDir
if ([string]::IsNullOrWhiteSpace($DataDir)) {
    $DataDir = Join-Path $pluginRoot '_research\pet-runtime'
}
$DataDir = [IO.Path]::GetFullPath($DataDir)
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null

$config = [ordered]@{
    spriteDir = Join-Path $pluginRoot 'pet\assets\sprites'
    soundDir = Join-Path $pluginRoot 'pet\assets\sounds'
    scale = $Scale
    volume = 0.7
    topmost = $true
    shadow = $true
    avoidFullscreen = $true
    statsDisplay = 'always'
    longPressMs = 450
    dragStartPx = 10
    edgeThreshold = 24
    edgeVisibleRatio = 0.38
    edgeHideDelayMs = 750
    edgeSlideMs = 180
    bubbleMs = 3800
    debug = $true
}
$configPath = Join-Path $DataDir 'demo-config.json'
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $pluginRoot 'pet\DesktopPet.ps1') `
    -Demo -ConfigPath $configPath -DataDir $DataDir
