<#
Builds CopyMaster's sparse package (package\AppxManifest.xml) and registers it, so that CopyMaster Paste
shows in Windows 11's compact right-click menu.

  .\dev\package.ps1                 build package\bin\CopyMaster.msix and register it
  .\dev\package.ps1 -BuildOnly      just build it (the manifest is checked against Windows' schemas)
  .\dev\package.ps1 -Unregister     remove the package (the deployed files stay)
  .\dev\package.ps1 -Status         say whether it's registered, and where its external location is

The package holds only the manifest and three logos. Its "external location" is the deploy folder,
%LOCALAPPDATA%\Programs\CopyMaster (or -Location), where dev\reload.ps1 puts CopyMasterShell.dll and
CopyMaster.exe; Explorer loads the DLL from there. So a new DLL needs no new package (only an Explorer
restart, as ever); a changed manifest does.

It's unsigned: the Publisher carries the OID that marks an unsigned package, and Windows 11 installs it with
Add-AppxPackage -AllowUnsigned. No certificate, no developer mode; it needs an elevated shell (an unsigned
package with code in it is installed for all users), which every shell is on Matthew's laptop. Registering
replaces any CopyMaster package already there. Removing a package ends any process running in it, so this
refuses while CopyMaster.exe runs from the deploy folder (it may be copying).

Explorer usually picks up a new package by itself; if Paste doesn't show, restart Explorer.
#>
param(
    [string]$Location = (Join-Path $env:LOCALAPPDATA 'Programs\CopyMaster'),
    [switch]$BuildOnly,
    [switch]$Unregister,
    [switch]$Status
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root 'package'
$msix = Join-Path $source 'bin\CopyMaster.msix'
$name = 'CopyMaster'

# The Appx cmdlets live in Windows PowerShell; PowerShell 7 reaches them through its compatibility layer.
Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue -ErrorAction SilentlyContinue

function Get-CopyMasterPackage {
    @(Get-AppxPackage -Name $name -AllUsers -ErrorAction SilentlyContinue)
}

function Find-SdkTool([string]$tool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$tool" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $found) { throw "$tool isn't installed (it comes with the Windows SDK)." }
    $found
}

function Assert-NotRunning {
    $running = Get-Process CopyMaster -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Location\*" }
    if ($running) {
        throw "CopyMaster is running from $Location (pid $($running.Id -join ', ')). Close it once its jobs are done, then run this again."
    }
}

function Remove-CopyMasterPackage {
    foreach ($package in Get-CopyMasterPackage) {
        Remove-AppxPackage -Package $package.PackageFullName -AllUsers
        Write-Host "Removed $($package.PackageFullName)."
    }
}

if ($Status) {
    $packages = Get-CopyMasterPackage
    if (-not $packages) { Write-Host 'CopyMaster''s package is not registered.'; return }
    foreach ($package in $packages) {
        Write-Host "$($package.PackageFullName): $($package.Status), signature $($package.SignatureKind)"
        Write-Host "  package files: $($package.InstallLocation)"
    }
    return
}

if ($Unregister) {
    Assert-NotRunning
    if (-not (Get-CopyMasterPackage)) { Write-Host 'CopyMaster''s package was not registered.'; return }
    Remove-CopyMasterPackage
    return
}

# Build. /nv skips the check that every file the manifest names is in the package: the DLL and the exe
# are in the external location instead. The schema check still runs.
New-Item -ItemType Directory -Force (Split-Path $msix) | Out-Null
$makeappx = Find-SdkTool 'makeappx.exe'
$output = & $makeappx pack /d $source /p $msix /nv /o 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    throw "makeappx couldn't build the package (exit $LASTEXITCODE)."
}
Write-Host "Built $msix."
if ($BuildOnly) { return }

foreach ($file in 'CopyMasterShell.dll', 'CopyMaster.exe') {
    if (-not (Test-Path (Join-Path $Location $file))) { throw "$Location has no $file. Deploy first: .\dev\reload.ps1" }
}
Assert-NotRunning
Remove-CopyMasterPackage
Add-AppxPackage -Path $msix -ExternalLocation $Location -AllowUnsigned
$package = Get-CopyMasterPackage | Select-Object -First 1
if (-not $package) { throw 'Add-AppxPackage said nothing went wrong, but the package is not registered.' }
Write-Host "Registered $($package.PackageFullName), external location $Location."
