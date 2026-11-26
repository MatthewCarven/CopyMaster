<#
Deploys a fresh build of CopyMaster over its install and registers it: the dev loop for the Explorer extension.

  .\dev\reload.ps1                  build Release, deploy, register; restart Explorer if it had the old DLL
  .\dev\reload.ps1 -NoBuild         deploy what's already built
  .\dev\reload.ps1 -Configuration Debug
  .\dev\reload.ps1 -Restart         restart Explorer even if it hadn't loaded the DLL
  .\dev\reload.ps1 -Unregister      unregister (the files stay), restarting Explorer if it had the DLL

It deploys to wherever CopyMaster is installed (the folder its menus are registered from), or failing that to
%ProgramFiles%\CopyMaster, setup\install.ps1's default. Unlike the installer it never touches the package
(dev\package.ps1 does), and it restarts Explorer by itself when Explorer has the old DLL loaded.

**Restarting Explorer closes every Explorer window and blanks the taskbar for a moment.** It's needed only once
Explorer has loaded the DLL (after the first right-drag that shows the menu): a loaded DLL keeps running until
Explorer exits. A first deploy needs no restart.

With CopyMaster's package registered, the classic Paste's keys come out again after regsvr32: the package's Paste
shows in the classic menu too (setup\CopyMasterSetup.psm1).

A loaded DLL can't be overwritten, but it can be renamed: the old one is moved aside as *.old and deleted on a
later run, so the copy never has to race Explorer. Release by default: a Debug DLL inside Explorer could stop it
with a CRT assertion dialog.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Restart,
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root 'setup\CopyMasterSetup.psm1') -Force
$deploy = Get-CopyMasterInstallFolder
if (-not $deploy) { $deploy = (Get-CopyMasterPlaces).DefaultFolder }
$dll = Join-Path $deploy 'CopyMasterShell.dll'

if ($Unregister) {
    if (Test-Path -LiteralPath $dll) {
        Invoke-Regsvr32 $dll -Unregister
        Write-Host "Unregistered $dll."
    } else {
        Write-Host "Nothing deployed at $dll."
    }
    if ($Restart -or (Test-ExplorerHasDll $deploy)) { Restart-Explorer }
    return
}

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit $LASTEXITCODE)." }
}
$files = @(Get-CopyMasterBuild $root $Configuration)

# A CopyMaster.exe running from the deploy folder holds its files. Never stop it: it may be copying.
Assert-CopyMasterNotRunning $deploy

$loaded = Test-ExplorerHasDll $deploy
$null = Copy-CopyMasterFiles $files $deploy
Write-Host "Deployed $Configuration to $deploy."

Invoke-Regsvr32 $dll
Write-Host "Registered $dll."
if (Get-CopyMasterPackage) { Remove-ClassicPaste }

if ($Restart -or $loaded) {
    Restart-Explorer
} else {
    Write-Host 'Explorer had not loaded the old DLL, so no restart was needed: right-drag away.'
}
