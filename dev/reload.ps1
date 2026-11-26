<#
Deploys CopyMaster with a fresh build of its Explorer extension and registers it.

  .\dev\reload.ps1                  build Release, deploy, register; restart Explorer if it had the old DLL
  .\dev\reload.ps1 -NoBuild         deploy what's already built
  .\dev\reload.ps1 -Configuration Debug
  .\dev\reload.ps1 -Restart         restart Explorer even if it hadn't loaded the DLL
  .\dev\reload.ps1 -Unregister      unregister (the files stay), restarting Explorer if it had the DLL

**Restarting Explorer closes every Explorer window and blanks the taskbar for a moment.** It's
needed only once Explorer has loaded the DLL (after the first right-drag that shows the menu): a
loaded DLL keeps running until Explorer exits. A first deploy needs no restart.

With CopyMaster's package registered (dev\package.ps1), the classic Paste's keys come out again after
regsvr32: the package's Paste shows in the classic menu too.

The deployed layout, in %LOCALAPPDATA%\Programs\CopyMaster: CopyMaster.exe with its files, and
CopyMasterShell.dll beside it (the DLL starts the CopyMaster.exe in its own folder). A loaded DLL
can't be overwritten, but it can be renamed: the old one is moved aside as *.old and deleted on a
later run, so the copy never has to race Explorer. Release by default: a Debug DLL inside
Explorer could stop it with a CRT assertion dialog.
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
$deploy = Join-Path $env:LOCALAPPDATA 'Programs\CopyMaster'
$dll = Join-Path $deploy 'CopyMasterShell.dll'
$regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'

# regsvr32 is a GUI program: PowerShell doesn't wait for it or set $LASTEXITCODE, so start it and wait.
function Invoke-Regsvr32([string[]]$Options) {
    $process = Start-Process $regsvr32 -ArgumentList ($Options + "`"$dll`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "regsvr32 $Options failed (exit $($process.ExitCode))." }
}

# Whether any Explorer process has a CopyMasterShell DLL from the deploy folder loaded.
function Test-ExplorerHasDll {
    foreach ($explorer in Get-Process explorer -ErrorAction SilentlyContinue) {
        try {
            if ($explorer.Modules | Where-Object { $_.FileName -like "$deploy\CopyMasterShell*" }) { return $true }
        } catch {
            return $true # can't tell: assume it does
        }
    }
    return $false
}

function Restart-Explorer {
    Write-Host 'Restarting Explorer: its windows close and the taskbar blanks for a moment.'
    $old = @(Get-Process explorer -ErrorAction SilentlyContinue)
    $old | Stop-Process -Force
    # Windows starts the shell again by itself (Winlogon's AutoRestartShell), with the user's own
    # environment rather than this script's. Wait for that.
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $new = Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $old.Id -notcontains $_.Id }
        if ($new) {
            Write-Host "Explorer is back (pid $($new[0].Id))."
            return
        }
    }
    # It didn't come back: start it through WMI, so it isn't a child of this shell (or its job).
    Write-Host 'Explorer did not restart by itself; starting it.'
    $started = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = Join-Path $env:SystemRoot 'explorer.exe'
    }
    if ($started.ReturnValue -ne 0) { throw "Couldn't start Explorer (Win32_Process.Create returned $($started.ReturnValue)). Start it from Task Manager: File, Run new task, explorer." }
}

if ($Unregister) {
    if (Test-Path $dll) {
        Invoke-Regsvr32 '/u', '/s'
        Write-Host "Unregistered $dll."
    } else {
        Write-Host "Nothing deployed at $dll."
    }
    if ($Restart -or (Test-ExplorerHasDll)) { Restart-Explorer }
    return
}

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit $LASTEXITCODE)." }
}

$exeBuild = Join-Path $root "src\CopyMaster\bin\$Configuration\net10.0-windows"
$dllBuild = Join-Path $root "src\CopyMasterShell\bin\x64\$Configuration"
foreach ($file in (Join-Path $exeBuild 'CopyMaster.exe'), (Join-Path $dllBuild 'CopyMasterShell.dll')) {
    if (-not (Test-Path $file)) { throw "$file isn't built. Run .\build.ps1 -Configuration $Configuration." }
}

# A CopyMaster.exe running from the deploy folder holds its files. Never stop it: it may be copying.
$running = Get-Process CopyMaster -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$deploy\*" }
if ($running) {
    throw "CopyMaster is running from $deploy (pid $($running.Id -join ', ')). Close it once its jobs are done, then run this again."
}

$loaded = Test-ExplorerHasDll
New-Item -ItemType Directory -Force $deploy | Out-Null
# DLLs moved aside by earlier runs; one still loaded somewhere stays until next time.
Get-ChildItem $deploy -Filter 'CopyMasterShell*.old' | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}
if (Test-Path $dll) {
    Rename-Item $dll ('CopyMasterShell.dll.{0:yyyyMMdd-HHmmss}.old' -f (Get-Date))
}

Copy-Item (Join-Path $exeBuild '*') $deploy -Recurse -Force
Copy-Item (Join-Path $dllBuild 'CopyMasterShell.dll'), (Join-Path $dllBuild 'CopyMasterShell.pdb') $deploy -Force
Write-Host "Deployed $Configuration to $deploy."

Invoke-Regsvr32 '/s'
Write-Host "Registered $dll."
# With CopyMaster's package in, its Paste serves the classic menu too: no second one (CopyMasterSetup.psm1).
Import-Module (Join-Path $PSScriptRoot 'CopyMasterSetup.psm1') -Force
if (Get-CopyMasterPackage) { Remove-ClassicPaste }

if ($Restart -or $loaded) {
    Restart-Explorer
} else {
    Write-Host 'Explorer had not loaded the old DLL, so no restart was needed: right-drag away.'
}
