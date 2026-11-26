<#
What CopyMaster's installer, uninstaller and check share (setup\install.ps1, uninstall.ps1, check.ps1), and the dev
scripts too (dev\reload.ps1, dev\package.ps1): where an install puts things, and how each one comes and goes.
Written for Windows PowerShell 5.1 as well as PowerShell 7, since 5.1 is on every Windows.

What an install puts where:
- The install folder, %ProgramFiles%\CopyMaster unless told otherwise: CopyMaster.exe's build output, with
  CopyMasterShell.dll beside it (the DLL starts the CopyMaster.exe in its own folder). Every file in it is named
  CopyMaster*, so the uninstaller deletes exactly those, then the folder if that leaves it empty: pointed at the wrong
  folder, it can't delete anything else. (Install refuses a build output holding any other file; a test checks the
  build still fits.) Until phase 5b, dev\reload.ps1 deployed to %LOCALAPPDATA%\Programs\CopyMaster, which the
  uninstaller and the check still look in.
- HKLM\Software\Classes: what the DLL's DllRegisterServer writes (regsvr32) and DllUnregisterServer removes:
  CLSID\{drag-drop} and CLSID\{paste}, the six keys below naming them, and both CLSIDs in Shell Extensions\Approved
  (Registration.cpp; a test checks these lists against it).
- On Windows 11, the sparse package (package\AppxManifest.xml), for all users, with the install folder as its
  external location. It carries the compact menu's Paste, whose CLSID lives in the package, not in HKLM. Windows adds
  %LOCALAPPDATA%\Packages\CopyMaster_gzvqk2jf034f6 and records of its own, and takes them away with the package.
- %LOCALAPPDATA%\CopyMaster: what CopyMaster.exe writes (settings.json, jobs\, errors.log), per user.
- Explorer's cache of which interfaces each shell extension has (HKCU ...\Shell Extensions\Cached, values named
  after the CLSID).

Two Pastes would show in the classic menu with the package in: the package's (the compact menu's command, which
Windows lists in the classic menu too, beside the other packaged apps') and the classic handler's. So while the
package is registered, the classic handler's three keys come out; the package's Paste serves both menus. Without the
package (Windows 10, or unregistered), regsvr32 puts them back. The classic handler's CLSID stays registered either
way, and the right-drag items never change.
#>

$script:PackageName = 'CopyMaster'
# Name, then the hash of the manifest's Publisher (a test works it out from package\AppxManifest.xml).
$script:PackageFamilyName = 'CopyMaster_gzvqk2jf034f6'

$script:Clsids = [ordered]@{
    DragDrop     = '{EA68F6FA-3C02-46D3-917E-FEE4FF327E27}' # right-drag: CopyMaster Copy here / Move here
    Paste        = '{355681EC-9621-450F-8449-6B5769A1149B}' # classic right-click: CopyMaster Paste
    PasteCommand = '{373E9CED-1AAA-4486-9EC6-DB3A03E8E315}' # the compact menu's Paste: the package registers it
}

# Under Software\Classes, as Registration.cpp writes them.
$script:DragDropKeys = @(
    'Directory\shellex\DragDropHandlers\CopyMaster'
    'Drive\shellex\DragDropHandlers\CopyMaster'
    'Folder\shellex\DragDropHandlers\CopyMaster'
)
$script:ClassicPasteKeys = @(
    'Directory\Background\shellex\ContextMenuHandlers\CopyMaster'
    'Directory\shellex\ContextMenuHandlers\CopyMaster'
    'Drive\shellex\ContextMenuHandlers\CopyMaster'
)
$script:ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved'
$script:CachedKey = 'Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Cached'

# Records Windows keeps for the package under HKCU (seen 2026-11-27 with it registered). Windows removes them with
# the package; the check lists any it leaves behind. Not CopyMaster's to delete.
$script:PackageRecords = @(
    "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$PackageFamilyName"
    "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\PolicyCache\$PackageFamilyName"
    "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\$($PackageFamilyName.ToLowerInvariant())"
    "Software\Microsoft\Windows\CurrentVersion\AppHost\IndexedDB\$PackageFamilyName"
    "Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications\$PackageFamilyName"
    "Software\Microsoft\Windows\CurrentVersion\PushNotifications\Backup\$PackageFamilyName!CopyMaster"
    "Software\Microsoft\Windows NT\CurrentVersion\HostActivityManager\CommitHistory\$PackageFamilyName!CopyMaster"
)
# Keys named after the package's full name (any version).
$script:PackageRecordParents = @(
    'Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages'
    'Software\Classes\PackagedCom\Package'
)

$script:AppxLoaded = $false

# ---- Places -------------------------------------------------------------------------------------------------------

function Get-CopyMasterPlaces {
    [pscustomobject]@{
        DefaultFolder     = Join-Path $env:ProgramFiles 'CopyMaster'
        LegacyFolder      = Join-Path $env:LOCALAPPDATA 'Programs\CopyMaster' # dev\reload.ps1's, before phase 5b
        DataFolder        = Join-Path $env:LOCALAPPDATA 'CopyMaster'
        PackageDataFolder = Join-Path $env:LOCALAPPDATA "Packages\$script:PackageFamilyName"
        PackageFamilyName = $script:PackageFamilyName
        Clsids            = $script:Clsids
        DragDropKeys      = $script:DragDropKeys
        ClassicPasteKeys  = $script:ClassicPasteKeys
    }
}

function Get-FullPath([string]$Path) {
    [IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Test-InFolder([string]$Path, [string]$Folder) {
    $Path -and $Path.StartsWith((Get-FullPath $Folder) + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Open-Key([Microsoft.Win32.RegistryKey]$Hive, [string]$Path, [bool]$Writable = $false) {
    try { $Hive.OpenSubKey($Path, $Writable) } catch { $null }
}

function Test-Key([Microsoft.Win32.RegistryKey]$Hive, [string]$Path) {
    $key = Open-Key $Hive $Path
    if ($key) { $key.Dispose(); return $true }
    $false
}

function Get-KeyValue([Microsoft.Win32.RegistryKey]$Hive, [string]$Path, [string]$Name) {
    $key = Open-Key $Hive $Path
    if (-not $key) { return $null }
    try { $key.GetValue($Name) } finally { $key.Dispose() }
}

# The folder the classic handlers are registered from (HKLM), or nothing.
function Get-CopyMasterInstallFolder {
    foreach ($clsid in $script:Clsids.DragDrop, $script:Clsids.Paste) {
        $dll = Get-KeyValue ([Microsoft.Win32.Registry]::LocalMachine) "Software\Classes\CLSID\$clsid\InprocServer32" ''
        if ($dll) { return Get-FullPath (Split-Path $dll -Parent) }
    }
}

# Every folder that holds or held CopyMaster: where it's registered from, where the package points, the default
# folder and the old dev one; those that exist, once each.
function Find-CopyMasterFolders([object[]]$Packages = @(), [string[]]$Extra = @()) {
    $places = Get-CopyMasterPlaces
    $candidates = @(Get-CopyMasterInstallFolder) + @($Packages | ForEach-Object { Get-CopyMasterPackageRoot $_ }) +
        @($places.DefaultFolder, $places.LegacyFolder) + $Extra
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $full = Get-FullPath $candidate
        if ((Test-Path -LiteralPath $full -PathType Container) -and $seen.Add($full)) { $full }
    }
}

# ---- Checks before changing anything ------------------------------------------------------------------------------

function Assert-SetupHost([switch]$NeedAdmin) {
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or -not [Environment]::Is64BitProcess) {
        throw 'CopyMaster is x64 only: run this from 64-bit PowerShell on x64 Windows.'
    }
    if ($NeedAdmin) {
        if (-not (Test-Elevated)) {
            throw 'This needs an elevated PowerShell (Run as administrator): it writes HKLM and Program Files, and the package goes in for all users.'
        }
    }
}

# CopyMaster.exe processes started from any of these folders (Explorer's, or the package's: its external location
# is the install folder).
function Get-CopyMasterProcess([string[]]$Folder) {
    Get-Process CopyMaster -ErrorAction SilentlyContinue | Where-Object {
        $path = $_.Path
        @($Folder | Where-Object { Test-InFolder $path $_ }).Count -gt 0
    }
}

# Never stop it: it may be copying. Removing the package would end one running inside it, too.
function Assert-CopyMasterNotRunning([string[]]$Folder) {
    $running = @(Get-CopyMasterProcess $Folder)
    if ($running) {
        $where = Split-Path $running[0].Path -Parent
        throw "CopyMaster is running from $where (pid $($running.Id -join ', ')). Close it once its jobs are done, then run this again."
    }
}

# Processes with a CopyMasterShell DLL from these folders loaded: Explorer, the package's dllhost surrogate, any
# program showing a file dialog. tasklist /m finds the candidates quickly; their module list says from where. A
# renamed DLL keeps the name and path it was loaded under.
function Get-DllUser([string[]]$Folder) {
    $lines = @(tasklist.exe /m CopyMasterShell.dll /fo csv /nh 2>$null | Where-Object { $_ -like '"*' })
    foreach ($row in $lines | ConvertFrom-Csv -Header Image, Id, Modules) {
        $process = Get-Process -Id $row.Id -ErrorAction SilentlyContinue
        if (-not $process) { continue }
        $from = $null
        try {
            $from = @($process.Modules | Where-Object { $module = $_.FileName; @($Folder | Where-Object { Test-InFolder $module $_ }).Count -gt 0 })
        } catch {
            $from = @('?') # can't see its modules: count it
        }
        if ($from.Count) { $process }
    }
}

function Test-DotNetDesktopRuntime {
    $shared = Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App'
    @(Get-ChildItem -LiteralPath $shared -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like '10.*' }).Count -gt 0
}

function Test-PackageSupported {
    # The manifest's MinVersion: Windows 11 (build 22000). Windows 10 keeps the classic Paste.
    [Environment]::OSVersion.Version.Build -ge 22000
}

# ---- Files ----------------------------------------------------------------------------------------------------------

# What an install holds: CopyMaster.exe's build output (all of it: every file is CopyMaster*) and the DLL with its
# symbols. A runtimeconfig.dev.json (Debug builds) would send the runtime looking in the NuGet cache: left out.
function Get-CopyMasterBuild([string]$Root, [string]$Configuration) {
    $exeBuild = Join-Path $Root "src\CopyMaster\bin\$Configuration\net10.0-windows"
    $dllBuild = Join-Path $Root "src\CopyMasterShell\bin\x64\$Configuration"
    foreach ($file in (Join-Path $exeBuild 'CopyMaster.exe'), (Join-Path $dllBuild 'CopyMasterShell.dll')) {
        if (-not (Test-Path -LiteralPath $file)) { throw "$file isn't built. Run .\build.ps1 -Configuration $Configuration." }
    }
    $folders = @(Get-ChildItem -LiteralPath $exeBuild -Directory)
    $files = @(Get-ChildItem -LiteralPath $exeBuild -File | Where-Object { $_.Name -notlike '*.runtimeconfig.dev.json' })
    $strangers = @($files | Where-Object { $_.Name -notlike 'CopyMaster*' }) + $folders
    if ($strangers) {
        throw "The build output in $exeBuild has $($strangers.Name -join ', '), which the uninstaller wouldn't remove (it deletes CopyMaster* files only). Rebuild from clean, or teach setup\CopyMasterSetup.psm1 about it."
    }
    $files + @(Get-Item -LiteralPath (Join-Path $dllBuild 'CopyMasterShell.dll'))
    $pdb = Join-Path $dllBuild 'CopyMasterShell.pdb'
    if (Test-Path -LiteralPath $pdb) { Get-Item -LiteralPath $pdb }
}

function Get-Stamp { '{0:yyyyMMdd-HHmmss}' -f (Get-Date) }

# Copies the build in. A loaded DLL can't be overwritten but can be renamed: the old one goes aside as *.old (and
# is deleted on a later run, or by the uninstaller), so the copy never has to race Explorer. Returns whether an
# older DLL was in use (by Explorer or anything else), which then needs a restart to be replaced in there.
function Copy-CopyMasterFiles([IO.FileInfo[]]$Files, [string]$Destination) {
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach ($old in Get-ChildItem -LiteralPath $Destination -Filter 'CopyMasterShell*.old' -File) {
        Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue # still loaded: next time
    }
    $dll = Join-Path $Destination 'CopyMasterShell.dll'
    $inUse = $false
    if (Test-Path -LiteralPath $dll) {
        $inUse = @(Get-DllUser $Destination).Count -gt 0
        Rename-Item -LiteralPath $dll "CopyMasterShell.dll.$(Get-Stamp).old"
    }
    foreach ($file in $Files) {
        Copy-Item -LiteralPath $file.FullName -Destination $Destination -Force
    }
    $inUse
}

# MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT): Windows deletes it when it next starts (a folder only if it's empty
# then). Needs an elevated caller. CM_TEST_NO_RESTART_DELETE=1 (the tests) only says so: each entry would sit in
# PendingFileRenameOperations until a restart, which some installers read as "a restart is pending".
function Register-DeleteAtRestart([string]$Path) {
    if ($env:CM_TEST_NO_RESTART_DELETE -eq '1') {
        Write-Host "  (test) not scheduled: $Path"
        return
    }
    if (-not ('CopyMasterSetup.Kernel' -as [type])) {
        Add-Type -Namespace CopyMasterSetup -Name Kernel -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool MoveFileEx(string existing, string replacement, int flags);
'@
    }
    if (-not [CopyMasterSetup.Kernel]::MoveFileEx($Path, $null, 4)) {
        $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw "Couldn't mark $Path for deletion at the next restart (error $code)."
    }
}

# Deletes the CopyMaster* files in Folder, then Folder if that leaves it empty. Nothing else in it is touched. A
# file in use (a DLL loaded somewhere) is renamed aside, so its name is free for a new install, and deleted at the
# next restart, as is the folder then. Returns what's left until then.
function Remove-CopyMasterFiles([string]$Folder) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container)) { return }
    $pending = @()
    # -Filter is the file system's match (short names too); -like is the rule.
    foreach ($file in Get-ChildItem -LiteralPath $Folder -Filter 'CopyMaster*' -File -Force | Where-Object { $_.Name -like 'CopyMaster*' }) {
        try {
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
        } catch {
            $aside = $file.FullName
            if ($file.Name -notlike '*.old') {
                $aside = "$($file.FullName).$(Get-Stamp).old"
                Rename-Item -LiteralPath $file.FullName $aside
            }
            Register-DeleteAtRestart $aside
            $pending += $aside
        }
    }
    $rest = @(Get-ChildItem -LiteralPath $Folder -Force)
    if ($rest.Count -eq 0) {
        Remove-Item -LiteralPath $Folder -Force
    } elseif ($pending.Count -eq $rest.Count) {
        Register-DeleteAtRestart $Folder
        $pending += $Folder
    } else {
        $others = @($rest | Where-Object { $pending -notcontains $_.FullName })
        Write-Warning "$Folder isn't CopyMaster's alone, so it stays: it also holds $($others.Name -join ', ')."
    }
    $pending
}

# Paths in PendingFileRenameOperations (deletes and renames waiting for a restart) inside these folders.
function Get-PendingDelete([string[]]$Folder) {
    $entries = Get-KeyValue ([Microsoft.Win32.Registry]::LocalMachine) 'SYSTEM\CurrentControlSet\Control\Session Manager' 'PendingFileRenameOperations'
    foreach ($entry in @($entries)) {
        if (-not $entry) { continue }
        # "\??\C:\x" (a delete is the path, then an empty string; a rename's target starts with "!")
        $path = $entry -replace '^!', '' -replace '^\\\?\?\\', ''
        foreach ($f in $Folder) {
            if ((Test-InFolder $path $f) -or ($path -ieq (Get-FullPath $f))) { $path; break }
        }
    }
}

# ---- Registration (HKLM, regsvr32) ----------------------------------------------------------------------------------

# regsvr32 is a GUI program: PowerShell neither waits for it nor sets $LASTEXITCODE, so start it and wait.
function Invoke-Regsvr32([string]$Dll, [switch]$Unregister) {
    $options = @('/s')
    if ($Unregister) { $options = @('/u', '/s') }
    $regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
    $process = Start-Process $regsvr32 -ArgumentList ($options + "`"$Dll`"") -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "regsvr32 $($options -join ' ') failed on $Dll (exit $($process.ExitCode))." }
}

# Every key and value the classic handlers' registration consists of, in both hives (HKCU too: phase 3 tried a
# per-user registration once, and COM honours one here). Present ones only, as objects {Hive, Key, Value, Path}.
function Get-RegistrationEntry {
    foreach ($hive in [Microsoft.Win32.Registry]::LocalMachine, [Microsoft.Win32.Registry]::CurrentUser) {
        $hiveName = if ($hive.Name -eq 'HKEY_LOCAL_MACHINE') { 'HKLM' } else { 'HKCU' }
        $keys = @($script:Clsids.Values | ForEach-Object { "CLSID\$_" }) + $script:DragDropKeys + $script:ClassicPasteKeys
        foreach ($key in $keys) {
            $path = "Software\Classes\$key"
            if (Test-Key $hive $path) {
                [pscustomobject]@{ Hive = $hive; Key = $path; Value = $null; Path = "${hiveName}:\$path" }
            }
        }
        $approved = Open-Key $hive $script:ApprovedKey
        if ($approved) {
            try {
                foreach ($clsid in $script:Clsids.Values) {
                    if ($approved.GetValueNames() -contains $clsid) {
                        [pscustomobject]@{ Hive = $hive; Key = $script:ApprovedKey; Value = $clsid; Path = "${hiveName}:\$($script:ApprovedKey)\$clsid" }
                    }
                }
            } finally { $approved.Dispose() }
        }
    }
}

# Deletes a key or a value by opening its parent for writing: opening HKLM\Software\Classes itself for writing is
# refused from some shells (Claude's tool shell) though regsvr32 writes under it.
function Remove-RegistryEntry([object]$Entry) {
    if ($Entry.Value) {
        $key = Open-Key $Entry.Hive $Entry.Key $true
        if ($key) { try { $key.DeleteValue($Entry.Value, $false) } finally { $key.Dispose() } }
        return
    }
    $parent = Open-Key $Entry.Hive (Split-Path $Entry.Key -Parent) $true
    if (-not $parent) { return }
    try { $parent.DeleteSubKeyTree((Split-Path $Entry.Key -Leaf), $false) } finally { $parent.Dispose() }
}

# Unregisters the classic handlers: regsvr32 /u on the registered DLL if it's still there, then whatever that left
# (or everything, if the DLL is gone), and Explorer's cached notes on our CLSIDs. Returns what it removed by hand.
function Remove-CopyMasterRegistration {
    $folder = Get-CopyMasterInstallFolder
    if ($folder) {
        $dll = Join-Path $folder 'CopyMasterShell.dll'
        if (Test-Path -LiteralPath $dll) { Invoke-Regsvr32 $dll -Unregister }
    }
    foreach ($entry in @(Get-RegistrationEntry)) {
        Remove-RegistryEntry $entry
        $entry.Path
    }
    foreach ($name in @(Get-ExplorerCacheValue)) {
        $key = Open-Key ([Microsoft.Win32.Registry]::CurrentUser) $script:CachedKey $true
        if ($key) { try { $key.DeleteValue($name, $false) } finally { $key.Dispose() } }
    }
    Send-AssociationChanged
}

# Explorer's cache: HKCU ...\Shell Extensions\Cached holds a value per (CLSID, interface) it has looked up, named
# "{CLSID} {IID} 0xFFFF". Only Explorer's notes; harmless, but named after our CLSIDs.
function Get-ExplorerCacheValue {
    $key = Open-Key ([Microsoft.Win32.Registry]::CurrentUser) $script:CachedKey
    if (-not $key) { return }
    try {
        foreach ($name in $key.GetValueNames()) {
            foreach ($clsid in $script:Clsids.Values) {
                if ($name.StartsWith($clsid, [StringComparison]::OrdinalIgnoreCase)) { $name; break }
            }
        }
    } finally { $key.Dispose() }
}

# SHCNE_ASSOCCHANGED, as DllRegisterServer sends: Explorer drops the handler lists it has cached.
function Send-AssociationChanged {
    if (-not ('CopyMasterSetup.Shell' -as [type])) {
        Add-Type -Namespace CopyMasterSetup -Name Shell -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("shell32.dll")]
public static extern void SHChangeNotify(int eventId, uint flags, System.IntPtr item1, System.IntPtr item2);
'@
    }
    [CopyMasterSetup.Shell]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
}

# Takes the classic Paste out of the right-click menus (the package's Paste stays). Says what it did.
function Remove-ClassicPaste {
    $removed = 0
    foreach ($key in $script:ClassicPasteKeys) {
        $entry = [pscustomobject]@{ Hive = [Microsoft.Win32.Registry]::LocalMachine; Key = "Software\Classes\$key"; Value = $null }
        if (Test-Key $entry.Hive $entry.Key) {
            Remove-RegistryEntry $entry
            $removed++
        }
    }
    if ($removed) {
        Send-AssociationChanged
        Write-Host 'The classic CopyMaster Paste is out of the menus: the package''s Paste shows there instead.'
    }
}

# The classic Paste, back (regsvr32 of the installed DLL writes every key again).
function Restore-ClassicPaste([string]$Dll) {
    if (-not (Test-Path -LiteralPath $Dll)) { return }
    Invoke-Regsvr32 $Dll
    Write-Host 'The classic CopyMaster Paste is back in the menus.'
}

# ---- Explorer -------------------------------------------------------------------------------------------------------

function Test-ExplorerHasDll([string[]]$Folder) {
    @(Get-DllUser $Folder | Where-Object { $_.ProcessName -eq 'explorer' }).Count -gt 0
}

# **Closes every Explorer window and blanks the taskbar for a moment.**
function Restart-Explorer {
    Write-Host 'Restarting Explorer: its windows close and the taskbar blanks for a moment.'
    $old = @(Get-Process explorer -ErrorAction SilentlyContinue)
    $old | Stop-Process -Force
    # Windows starts the shell again by itself (Winlogon's AutoRestartShell), with the user's own environment
    # rather than this script's. Wait for that.
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

# ---- The package ----------------------------------------------------------------------------------------------------

# The Appx cmdlets live in Windows PowerShell; PowerShell 7 reaches them through its compatibility layer (a Windows
# PowerShell process: a few seconds), so only when asked. That layer builds a proxy module in the global session
# state, copying it into %TEMP% as it goes, so a script run with -WhatIf makes the copy a "what if" and the import
# fails; install.ps1 and uninstall.ps1 load the cmdlets first, with WhatIf off for a moment.
function Import-Appx {
    if ($script:AppxLoaded) { return }
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
    } else {
        Import-Module Appx -ErrorAction SilentlyContinue
    }
    $script:AppxLoaded = $true
}

function Test-Elevated {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# -AllUsers needs an elevated shell; without one, this user's packages (the package is installed for every user).
function Get-CopyMasterPackage {
    Import-Appx
    if (Test-Elevated) {
        @(Get-AppxPackage -Name $script:PackageName -AllUsers -ErrorAction SilentlyContinue)
    } else {
        @(Get-AppxPackage -Name $script:PackageName -ErrorAction SilentlyContinue)
    }
}

# The package's external location (the install folder it was registered with), from Windows' per-user record of it.
function Get-CopyMasterPackageRoot([object]$Package) {
    $key = "Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages\$($Package.PackageFullName)"
    $root = Get-KeyValue ([Microsoft.Win32.Registry]::CurrentUser) $key 'PackageRootFolder'
    if ($root) { Get-FullPath $root }
}

function Get-ManifestVersion([string]$Root) {
    $manifest = [xml](Get-Content -Raw -LiteralPath (Join-Path $Root 'package\AppxManifest.xml'))
    $manifest.Package.Identity.Version
}

function Find-SdkTool([string]$Tool) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    Get-ChildItem -LiteralPath $kits -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$Tool" } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
}

# Builds package\bin\CopyMaster.msix (or Output) from the manifest and its logos, listed in a mapping file: packing
# the whole package folder took package\bin along too, the last build's msix included. /nv skips the check that
# every file the manifest names is in the package (the DLL and the exe are in the external location instead); the
# schema check still runs. Needs makeappx, from the Windows SDK.
function New-CopyMasterPackage([string]$Root, [string]$Output) {
    $source = Join-Path $Root 'package'
    if (-not $Output) { $Output = Join-Path $source 'bin\CopyMaster.msix' }
    $makeappx = Find-SdkTool 'makeappx.exe'
    if (-not $makeappx) { throw 'makeappx.exe isn''t installed (it comes with the Windows SDK), so the package can''t be built.' }
    $bin = Split-Path $Output -Parent
    New-Item -ItemType Directory -Force $bin | Out-Null
    $files = @(Get-Item -LiteralPath (Join-Path $source 'AppxManifest.xml')) + @(Get-ChildItem -LiteralPath (Join-Path $source 'Assets') -File)
    $mapping = @('[Files]') + @($files | ForEach-Object {
        '"{0}" "{1}"' -f $_.FullName, $_.FullName.Substring($source.Length + 1)
    })
    $map = Join-Path $bin 'CopyMaster.map'
    Set-Content -LiteralPath $map -Value $mapping -Encoding UTF8
    $text = & $makeappx pack /f $map /p $Output /nv /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $text | ForEach-Object { Write-Host $_ }
        throw "makeappx couldn't build the package (exit $LASTEXITCODE)."
    }
    $Output
}

# Registers the package for all users with Location (the install folder) as its external location, replacing any
# CopyMaster package there was. Unsigned: the Publisher's OID lets Windows 11 take it with -AllowUnsigned.
function Register-CopyMasterPackage([string]$Msix, [string]$Location) {
    Unregister-CopyMasterPackage
    Add-AppxPackage -Path $Msix -ExternalLocation $Location -AllowUnsigned
    $package = Get-CopyMasterPackage | Select-Object -First 1
    if (-not $package) { throw 'Add-AppxPackage said nothing went wrong, but the package is not registered.' }
    Write-Host "Registered the package $($package.PackageFullName), external location $Location."
}

# Removing a package ends every process running in it (CopyMaster.exe started from its Paste, its surrogate).
function Unregister-CopyMasterPackage {
    foreach ($package in Get-CopyMasterPackage) {
        Remove-AppxPackage -Package $package.PackageFullName -AllUsers
        Write-Host "Removed the package $($package.PackageFullName)."
    }
}

# ---- Traces ---------------------------------------------------------------------------------------------------------

function New-Trace([string]$Owner, [string]$Kind, [string]$Where, [string]$What = '') {
    [pscustomobject]@{ Owner = $Owner; Kind = $Kind; Where = $Where; What = $What }
}

function Get-ItemSummary([string]$Folder) {
    $items = @(Get-ChildItem -LiteralPath $Folder -Force -ErrorAction SilentlyContinue)
    $names = @($items | Select-Object -First 6 | ForEach-Object { if ($_.PSIsContainer) { "$($_.Name)\" } else { $_.Name } })
    if ($items.Count -gt 6) { $names += "and $($items.Count - 6) more" }
    if ($items.Count -eq 0) { 'empty' } else { $names -join ', ' }
}

# Everything CopyMaster has left on this machine, as objects {Owner, Kind, Where, What}. Owner:
#   CopyMaster  its own: installed, or left behind (the uninstaller removes these)
#   Restart     waiting for the next restart to be deleted
#   Windows     Windows' records about it (usage, the package's bookkeeping): listed, never touched
# Folder adds folders to look in (an install somewhere unusual). -Deep also searches the whole registry (HKCU,
# HKLM\SOFTWARE, HKLM\SYSTEM\CurrentControlSet) for "CopyMaster" and the CLSIDs: about a minute.
function Get-CopyMasterTrace([string[]]$Folder = @(), [switch]$Deep) {
    $places = Get-CopyMasterPlaces
    $packages = @(Get-CopyMasterPackage)
    $folders = @(Find-CopyMasterFolders -Packages $packages -Extra $Folder)
    $pending = @(Get-PendingDelete (@($folders) + $Folder + $places.DefaultFolder + $places.LegacyFolder))
    $shown = New-Object 'System.Collections.Generic.List[string]'

    foreach ($f in $folders) {
        $onlyPending = @(Get-ChildItem -LiteralPath $f -Force | Where-Object { $pending -notcontains $_.FullName }).Count -eq 0
        $owner = if ($onlyPending -and $pending -contains $f) { 'Restart' } else { 'CopyMaster' }
        New-Trace $owner 'Folder' $f (Get-ItemSummary $f)
    }
    foreach ($p in $pending) {
        New-Trace 'Restart' 'Delete at restart' $p
    }
    if (Test-Path -LiteralPath $places.DataFolder) {
        New-Trace 'CopyMaster' 'Folder' $places.DataFolder "settings and logs: $(Get-ItemSummary $places.DataFolder)"
    }

    foreach ($package in $packages) {
        $root = Get-CopyMasterPackageRoot $package
        New-Trace 'CopyMaster' 'Package' $package.PackageFullName "for all users; external location $root; manifest and logos in $($package.InstallLocation)"
    }
    if (Test-Path -LiteralPath $places.PackageDataFolder) {
        New-Trace 'CopyMaster' 'Folder' $places.PackageDataFolder 'the package''s data folder (Windows makes it)'
    }
    if ($packages.Count -eq 0) {
        # Windows deletes the package's own folder (manifest and logos) with it. WindowsApps can't be listed, but a
        # folder in it can be looked for by name: this version's.
        $publisherId = $script:PackageFamilyName.Substring($script:PackageName.Length + 1)
        $version = '1.0.0.0'
        try { $version = Get-ManifestVersion (Split-Path $PSScriptRoot -Parent) } catch { }
        $installed = Join-Path $env:ProgramFiles "WindowsApps\$($script:PackageName)_$($version)_x64__$publisherId"
        if (Test-Path -LiteralPath $installed) {
            New-Trace 'CopyMaster' 'Folder' $installed 'the package''s files, still there with the package gone'
        }
    }

    foreach ($entry in @(Get-RegistrationEntry)) {
        $what = if ($entry.Value) { 'Shell Extensions\Approved' } elseif ($entry.Key -like '*\CLSID\*') { 'class' } else { 'menu handler' }
        New-Trace 'CopyMaster' 'Registry' $entry.Path $what
        $shown.Add($entry.Path)
    }
    foreach ($name in @(Get-ExplorerCacheValue)) {
        New-Trace 'CopyMaster' 'Registry' "HKCU:\$($script:CachedKey)\$name" 'Explorer''s cache'
        $shown.Add("HKCU:\$($script:CachedKey)")
    }

    foreach ($process in Get-Process CopyMaster -ErrorAction SilentlyContinue) {
        New-Trace 'CopyMaster' 'Running' "CopyMaster.exe, pid $($process.Id)" $process.Path
    }
    foreach ($process in @(Get-DllUser (@($folders) + $Folder))) {
        New-Trace 'CopyMaster' 'Running' "$($process.ProcessName).exe, pid $($process.Id)" 'has CopyMasterShell.dll loaded'
    }

    if ($packages.Count -eq 0) {
        foreach ($key in $script:PackageRecords) {
            if (Test-Key ([Microsoft.Win32.Registry]::CurrentUser) $key) {
                New-Trace 'Windows' 'Registry' "HKCU:\$key" 'the package''s record, left by Windows'
                $shown.Add("HKCU:\$key")
            }
        }
        foreach ($parent in $script:PackageRecordParents) {
            $key = Open-Key ([Microsoft.Win32.Registry]::CurrentUser) $parent
            if (-not $key) { continue }
            try {
                foreach ($name in $key.GetSubKeyNames() | Where-Object { $_ -like 'CopyMaster_*' }) {
                    New-Trace 'Windows' 'Registry' "HKCU:\$parent\$name" 'the package''s record, left by Windows'
                    $shown.Add("HKCU:\$parent\$name")
                }
            } finally { $key.Dispose() }
        }
    }
    $usage = Open-Key ([Microsoft.Win32.Registry]::CurrentUser) 'Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched'
    if ($usage) {
        try {
            foreach ($name in $usage.GetValueNames() | Where-Object { $_ -like '*\CopyMaster.exe' }) {
                New-Trace 'Windows' 'Registry' "HKCU:\...\FeatureUsage\AppSwitched: $name" 'the taskbar''s count of switches to it'
            }
        } finally { $usage.Dispose() }
        $shown.Add('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage\AppSwitched')
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'Prefetch') -Filter 'COPYMASTER*' -ErrorAction SilentlyContinue) {
        New-Trace 'Windows' 'File' $file.FullName 'Windows'' prefetch record'
    }

    if ($Deep) { Search-RegistryTrace -Shown $shown -PackageRegistered ($packages.Count -gt 0) }
}

# reg.exe's own search, over HKCU, HKLM\SOFTWARE and HKLM\SYSTEM\CurrentControlSet, for "CopyMaster" (names and
# data, any case) and the CLSIDs. Keys already listed (and what's under them) are skipped; so is the package's
# bookkeeping while it's registered (Windows' own, expected then).
function Search-RegistryTrace([System.Collections.Generic.List[string]]$Shown, [bool]$PackageRegistered) {
    $terms = @('CopyMaster') + @($script:Clsids.Values | ForEach-Object { $_.Trim('{}') })
    $hits = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in 'HKCU', 'HKLM\SOFTWARE', 'HKLM\SYSTEM\CurrentControlSet') {
        foreach ($term in $terms) {
            $key = $null
            foreach ($line in & reg.exe query $root /f $term /s 2>$null) {
                if ($line -match '^HKEY_') {
                    $key = $line -replace '^HKEY_LOCAL_MACHINE', 'HKLM:' -replace '^HKEY_CURRENT_USER', 'HKCU:'
                    $null = $hits.Add($key)
                } elseif ($key -and $line -match '^\s{4}(.+?)\s{4}REG_\w+') {
                    $null = $hits.Add("$key  [$($Matches[1])]")
                }
            }
        }
    }
    $bookkeeping = 'AppModel|AppContainer|PackagedCom|StateRepository|\\Appx\\|CapAuthz|AppHost|PushNotifications|BackgroundAccessApplications|HostActivityManager|SecurityManager'
    foreach ($hit in $hits | Sort-Object) {
        $under = @($Shown | Where-Object {
            $hit -eq $_ -or $hit.StartsWith("$_\", [StringComparison]::OrdinalIgnoreCase) -or
                $hit.StartsWith("$_  [", [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if ($under) { continue }
        if ($PackageRegistered -and $hit -match $bookkeeping) { continue }
        New-Trace 'Windows' 'Registry (deep)' $hit 'found by searching the registry'
    }
}

# Prints traces grouped by owner. Returns nothing.
function Show-CopyMasterTrace([object[]]$Trace) {
    $groups = [ordered]@{
        CopyMaster = 'CopyMaster''s own (the uninstaller removes these):'
        Restart    = 'Deleted when Windows next restarts:'
        Windows    = 'Windows'' records of it (not CopyMaster''s; left alone):'
    }
    foreach ($owner in $groups.Keys) {
        $these = @($Trace | Where-Object { $_.Owner -eq $owner })
        if (-not $these) { continue }
        Write-Host $groups[$owner]
        foreach ($t in $these) {
            $what = if ($t.What) { "  ($($t.What))" } else { '' }
            Write-Host "  [$($t.Kind)] $($t.Where)$what"
        }
    }
    if (-not @($Trace | Where-Object { $_.Owner -ne 'Windows' })) {
        Write-Host 'Nothing of CopyMaster''s is left on this machine.'
    }
}

Export-ModuleMember -Function Get-CopyMasterPlaces, Get-CopyMasterInstallFolder, Find-CopyMasterFolders,
    Assert-SetupHost, Get-CopyMasterProcess, Assert-CopyMasterNotRunning, Get-DllUser, Test-DotNetDesktopRuntime,
    Test-PackageSupported, Get-CopyMasterBuild, Copy-CopyMasterFiles, Remove-CopyMasterFiles, Get-PendingDelete,
    Invoke-Regsvr32, Get-RegistrationEntry, Remove-CopyMasterRegistration, Get-ExplorerCacheValue,
    Remove-ClassicPaste, Restore-ClassicPaste, Test-ExplorerHasDll, Restart-Explorer, Get-CopyMasterPackage,
    Get-CopyMasterPackageRoot, Get-ManifestVersion, New-CopyMasterPackage, Register-CopyMasterPackage,
    Unregister-CopyMasterPackage, Get-CopyMasterTrace, Show-CopyMasterTrace
