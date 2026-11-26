<#
Builds CopyMaster's sparse package (package\AppxManifest.xml) and registers it, so that CopyMaster Paste shows in
Windows 11's compact right-click menu. setup\install.ps1 does this as part of an install; this is for changing the
package on its own.

  .\dev\package.ps1                 build package\bin\CopyMaster.msix and register it
  .\dev\package.ps1 -BuildOnly      just build it (the manifest is checked against Windows' schemas)
  .\dev\package.ps1 -Unregister     remove the package (the installed files stay; the classic Paste comes back)
  .\dev\package.ps1 -Status         say whether it's registered, and where its external location is

The package holds only the manifest and three logos. Its "external location" is the install folder (where
CopyMaster's menus are registered from, else %ProgramFiles%\CopyMaster; or -Location), which holds
CopyMasterShell.dll and CopyMaster.exe; the surrogate loads the DLL from there. So a new DLL needs no new package
(only an Explorer restart, as ever); a changed manifest does.

It's unsigned: the Publisher carries the OID that marks an unsigned package, and Windows 11 installs it with
Add-AppxPackage -AllowUnsigned. No certificate, no developer mode; it needs an elevated shell (an unsigned package
with code in it is installed for all users). Registering replaces any CopyMaster package already there. Removing a
package ends any process running in it, so this refuses while CopyMaster.exe runs from the install folder (it may
be copying).

Explorer usually picks up a new package by itself; if Paste doesn't show, restart Explorer.

The package's Paste shows in the classic menu too ("Show more options", or StartAllBack's classic menu), so
registering takes the classic handler's Paste keys out (two would show), and -Unregister puts them back
(setup\CopyMasterSetup.psm1). Registry only: no Explorer restart.
#>
param(
    [string]$Location,
    [switch]$BuildOnly,
    [switch]$Unregister,
    [switch]$Status
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'setup\CopyMasterSetup.psm1') -Force
if (-not $Location) { $Location = Get-CopyMasterInstallFolder }
if (-not $Location) { $Location = (Get-CopyMasterPlaces).DefaultFolder }

if ($Status) {
    $packages = Get-CopyMasterPackage
    if (-not $packages) { Write-Host 'CopyMaster''s package is not registered.'; return }
    foreach ($package in $packages) {
        Write-Host "$($package.PackageFullName): $($package.Status), signature $($package.SignatureKind)"
        Write-Host "  package files: $($package.InstallLocation)"
        Write-Host "  external location: $(Get-CopyMasterPackageRoot $package)"
    }
    return
}

if ($Unregister) {
    Assert-CopyMasterNotRunning $Location
    if (Get-CopyMasterPackage) { Unregister-CopyMasterPackage } else { Write-Host 'CopyMaster''s package was not registered.' }
    Restore-ClassicPaste (Join-Path $Location 'CopyMasterShell.dll')
    return
}

$msix = New-CopyMasterPackage $root
Write-Host "Built $msix."
if ($BuildOnly) { return }

foreach ($file in 'CopyMasterShell.dll', 'CopyMaster.exe') {
    if (-not (Test-Path -LiteralPath (Join-Path $Location $file))) { throw "$Location has no $file. Install first: .\setup\install.ps1" }
}
Assert-CopyMasterNotRunning $Location
Register-CopyMasterPackage $msix $Location
Remove-ClassicPaste
