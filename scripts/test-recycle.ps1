#requires -version 5.1
<#
.SYNOPSIS
  Functional test for the recycle-bin drop path.

.DESCRIPTION
  Compiles the pet shell, creates a uniquely named file and directory in the
  workspace, hands them to the same RecycleService used by drag-and-drop, and
  verifies that the originals are gone and that the Recycle Bin exposes them.
  No permanent-delete API is used anywhere in this test.

  The test intentionally leaves the recycled items in the Recycle Bin. Restore
  or empty it yourself; the test never calls a delete API to clean up.

  Run this in a normal terminal, not inside the DSH workspace sandbox. If the
  current process is restricted, the script exits 2 without creating anything.
#>
[CmdletBinding()]
param(
    [string]$WorkDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginRoot = Split-Path -Parent $scriptDir
$framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory().TrimEnd('\')
$references = @(
    (Join-Path $framework 'System.dll'),
    (Join-Path $framework 'System.Core.dll'),
    (Join-Path $framework 'System.Xml.dll'),
    (Join-Path $framework 'System.Web.Extensions.dll'),
    (Join-Path $framework 'System.Xaml.dll'),
    (Join-Path $framework 'System.Windows.Forms.dll'),
    (Join-Path $framework 'System.Drawing.dll'),
    (Join-Path $framework 'WPF\WindowsBase.dll'),
    (Join-Path $framework 'WPF\PresentationCore.dll'),
    (Join-Path $framework 'WPF\PresentationFramework.dll')
)

$type = 'DshDesktopPet.RecycleService' -as [type]
if (-not $type) {
    Add-Type -Path (Join-Path $pluginRoot 'pet\DesktopPet.cs') -ReferencedAssemblies $references -ErrorAction Stop
}

$restricted = [DshDesktopPet.RecycleService]::RestrictedReason()
if ($restricted) {
    [ordered]@{
        ok = $false
        skipped = $true
        restricted = $restricted
        message = 'Restricted process: recycle refused to avoid permanent delete. Run in a normal terminal.'
    } | ConvertTo-Json -Depth 4
    exit 2
}

if ([string]::IsNullOrWhiteSpace($WorkDir)) {
    $WorkDir = Join-Path $pluginRoot '_research\recycle-test'
}
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMddHHmmssfff'
$file = Join-Path $WorkDir ("pet-eat-$stamp.txt")
$folder = Join-Path $WorkDir ("pet-eat-dir-$stamp")
[IO.File]::WriteAllText($file, "desktop pet recycle test $stamp", [Text.UTF8Encoding]::new($false))
New-Item -ItemType Directory -Force -Path $folder | Out-Null
[IO.File]::WriteAllText((Join-Path $folder 'inside.txt'), "inside $stamp", [Text.UTF8Encoding]::new($false))

$result = [DshDesktopPet.RecycleService]::Recycle(
    [string[]]@($file, $folder),
    [string[]]@('C:\Windows')
)

$fileGone = -not (Test-Path -LiteralPath $file)
$folderGone = -not (Test-Path -LiteralPath $folder)
$binNames = @()
try {
    $shell = New-Object -ComObject Shell.Application
    $bin = $shell.Namespace(10)
    $binNames = @($bin.Items() | ForEach-Object { $_.Name })
} catch {
    $binNames = @("Recycle Bin query failed: $($_.Exception.Message)")
}
$fileInBin = $binNames -contains (Split-Path -Leaf $file)
$folderInBin = $binNames -contains (Split-Path -Leaf $folder)

$protectedProbe = [DshDesktopPet.RecycleService]::Recycle(
    [string[]]@('C:\Windows\__dsh_pet_protected_probe__'),
    [string[]]@('C:\Windows')
)
$protectedRefused = (-not $protectedProbe.Ok) -and ($protectedProbe.Moved -eq 0)

$report = [ordered]@{
    moved = $result.Moved
    failures = $result.Failures
    file = $file
    fileGone = $fileGone
    folder = $folder
    folderGone = $folderGone
    fileInRecycleBin = $fileInBin
    folderInRecycleBin = $folderInBin
    protectedRefused = $protectedRefused
    ok = ($result.Ok -and $fileGone -and $folderGone -and $fileInBin -and $folderInBin -and $protectedRefused)
}
$report | ConvertTo-Json -Depth 6
if (-not $report.ok) { exit 1 }
