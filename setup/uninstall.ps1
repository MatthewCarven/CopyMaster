<#
Uninstalls CopyMaster and leaves nothing behind:
- the package (Windows 11), for all users, and its data folder %LOCALAPPDATA%\Packages\CopyMaster_gzvqk2jf034f6;
- the menus' registration in HKLM (regsvr32 /u, then anything left of it), and Explorer's cached notes on it;
- the files: every CopyMaster* file in each folder CopyMaster is found in (where it's registered from, where the
  package points, %ProgramFiles%\CopyMaster, and dev\reload.ps1's old %LOCALAPPDATA%\Programs\CopyMaster), then the
  folder if that empties it (nothing else in a folder is touched);
- your settings and logs, %LOCALAPPDATA%\CopyMaster, if you say so.
Then it lists whatever is left; .\setup\check.ps1 does the same any time.

  .\setup\uninstall.ps1                    asks about your settings, and about restarting Explorer if it must
  .\setup\uninstall.ps1 -RemoveSettings    or -KeepSettings: don't ask
  .\setup\uninstall.ps1 -RestartExplorer   or -NoRestart: don't ask
  .\setup\uninstall.ps1 -WhatIf            say what it would do, and do nothing
  .\setup\uninstall.ps1 -Path <folder>     that folder too (an install somewhere unusual)
  .\setup\uninstall.ps1 -FilesOnly -Path <folder>
                                           only the CopyMaster* files there (for testing)

A DLL that Explorer (or any program showing a file dialog) has loaded can't be deleted, only renamed. So when
Explorer has CopyMasterShell.dll loaded, it asks whether to restart Explorer, which closes every Explorer window, so
the DLL can go at once. Otherwise, or if something else still holds it, the DLL is renamed aside and deleted when
Windows next restarts, as is its folder.

Refuses while CopyMaster.exe runs from an install folder: it may be copying, and it's never stopped. (Removing the
package would end one running inside it.) Needs an elevated PowerShell.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Path,
    [switch]$RemoveSettings,
    [switch]$KeepSettings,
    [switch]$RestartExplorer,
    [switch]$NoRestart,
    [switch]$FilesOnly
)
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'CopyMasterSetup.psm1') -Force
$places = Get-CopyMasterPlaces

if ($RemoveSettings -and $KeepSettings) { throw 'Say -RemoveSettings or -KeepSettings, not both.' }
if ($RestartExplorer -and $NoRestart) { throw 'Say -RestartExplorer or -NoRestart, not both.' }
if ($FilesOnly -and -not $Path) { throw '-FilesOnly removes a test install: say where with -Path.' }
Assert-SetupHost -NeedAdmin:(-not $FilesOnly)

function Ask([string]$Question, [string]$Switches) {
    try {
        $answer = Read-Host "$Question [y/N]"
    } catch {
        throw "$Question This shell can't ask (it isn't interactive): run again with $Switches."
    }
    $answer -match '^\s*y(es)?\s*$'
}

$extra = @()
if ($Path) { $extra = @([IO.Path]::GetFullPath($Path).TrimEnd('\')) }
$packages = @()
if ($FilesOnly) {
    $folders = @($extra | Where-Object { Test-Path -LiteralPath $_ -PathType Container })
} else {
    # The Appx cmdlets, loaded with WhatIf off (CopyMasterSetup.psm1, Import-Appx): reading packages changes nothing.
    $whatIf = $WhatIfPreference
    $WhatIfPreference = $false
    $packages = @(Get-CopyMasterPackage)
    $WhatIfPreference = $whatIf
    $folders = @(Find-CopyMasterFolders -Packages $packages -Extra $extra)
}

Assert-CopyMasterNotRunning $folders

# Both questions first, so nothing is asked half-way through.
$removeData = $false
$dataThere = (-not $FilesOnly) -and (Test-Path -LiteralPath $places.DataFolder)
if ($dataThere) {
    if ($RemoveSettings) {
        $removeData = $true
    } elseif ($WhatIfPreference -and -not $KeepSettings) {
        Write-Host "What if: it would ask whether to delete your settings and logs, $($places.DataFolder) (-RemoveSettings or -KeepSettings answers)."
    } elseif (-not $KeepSettings) {
        $removeData = Ask "Delete your CopyMaster settings and logs too ($($places.DataFolder))?" '-RemoveSettings or -KeepSettings'
    }
}

$restart = $false
$explorerHasDll = (-not $FilesOnly) -and $folders.Count -and (Test-ExplorerHasDll $folders)
if ($explorerHasDll) {
    if ($RestartExplorer) {
        $restart = $true
    } elseif ($WhatIfPreference -and -not $NoRestart) {
        Write-Host 'What if: Explorer has CopyMasterShell.dll loaded; it would ask whether to restart Explorer (-RestartExplorer or -NoRestart answers).'
    } elseif (-not $NoRestart) {
        $restart = Ask ('Explorer has CopyMasterShell.dll loaded, so the DLL can''t be deleted until Explorer restarts. ' +
            'Restart Explorer now? It closes every Explorer window. (No: the DLL is deleted when Windows next restarts.)') '-RestartExplorer or -NoRestart'
    }
}

if (-not $FilesOnly) {
    # The package first: removing it ends its surrogate, which has the DLL loaded too.
    if ($packages -and $PSCmdlet.ShouldProcess(($packages.PackageFullName -join ', '), 'Remove the package for all users')) {
        Unregister-CopyMasterPackage
        if (Get-CopyMasterPackage) { throw 'Remove-AppxPackage said nothing went wrong, but the package is still registered.' }
    }
    # Windows deletes the package's data folder with it (for this user); if it didn't, it's an orphan.
    if ((Test-Path -LiteralPath $places.PackageDataFolder) -and -not ($WhatIfPreference -and $packages) -and
        $PSCmdlet.ShouldProcess($places.PackageDataFolder, 'Delete the package''s data folder, left behind')) {
        Remove-Item -LiteralPath $places.PackageDataFolder -Recurse -Force
    }

    $entries = @(Get-RegistrationEntry)
    $cached = @(Get-ExplorerCacheValue)
    if (($entries.Count -or $cached.Count) -and
        $PSCmdlet.ShouldProcess("$($entries.Count) registry entries and $($cached.Count) of Explorer's cache values", 'Unregister the menus (regsvr32 /u, then whatever it left)')) {
        $byHand = @(Remove-CopyMasterRegistration)
        foreach ($entry in $byHand) { Write-Host "  removed by hand: $entry" }
        Write-Host 'Unregistered the menus.'
    }
}

if ($restart -and $PSCmdlet.ShouldProcess('Explorer', 'Restart, so it lets go of CopyMasterShell.dll (closes its windows)')) {
    Restart-Explorer
}

$holders = @()
if ($folders.Count) { $holders = @(Get-DllUser $folders) }
$pending = @()
foreach ($folder in $folders) {
    if ($PSCmdlet.ShouldProcess($folder, 'Delete the CopyMaster* files in it, then the folder if that empties it')) {
        $pending += @(Remove-CopyMasterFiles $folder)
        Write-Host "Removed CopyMaster from $folder."
    }
}
if ($pending) {
    $by = ''
    if ($holders) { $by = " (it's loaded in $(($holders | ForEach-Object { "$($_.ProcessName) $($_.Id)" }) -join ', '))" }
    Write-Host "In use$by, so deleted when Windows next restarts:"
    $pending | ForEach-Object { Write-Host "  $_" }
}

if ($removeData -and $PSCmdlet.ShouldProcess($places.DataFolder, 'Delete your settings, jobs and errors.log')) {
    Remove-Item -LiteralPath $places.DataFolder -Recurse -Force
    Write-Host "Deleted $($places.DataFolder)."
}

if ($WhatIfPreference) { return }
Write-Host ''
if ($FilesOnly) {
    # A test install: only its folder matters, not the real one.
    if (Test-Path -LiteralPath $extra[0]) {
        $left = @(Get-PendingDelete $extra)
        if ($left.Count -and @(Get-ChildItem -LiteralPath $extra[0] -Force | Where-Object { $left -notcontains $_.FullName }).Count -eq 0) {
            Write-Host "Only what waits for a restart is left in $($extra[0])."
            exit 0
        }
        Write-Host "$($extra[0]) is still there: $((Get-ChildItem -LiteralPath $extra[0] -Force).Name -join ', ')"
        exit 1
    }
    Write-Host "$($extra[0]) is gone."
    exit 0
}
$traces = @(Get-CopyMasterTrace -Folder $extra)
if ($dataThere -and -not $removeData) {
    $traces = @($traces | Where-Object { $_.Where -ne $places.DataFolder })
    Write-Host "Kept your settings and logs in $($places.DataFolder)."
}
Show-CopyMasterTrace $traces
if (@($traces | Where-Object { $_.Owner -eq 'CopyMaster' }).Count) { exit 1 }
