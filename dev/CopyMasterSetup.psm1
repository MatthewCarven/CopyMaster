# What dev\reload.ps1 and dev\package.ps1 (and later the installer) share about how CopyMaster is registered.
#
# Two Pastes would show in the classic menu with the package in: the package's (the compact menu's command,
# which Windows lists in the classic menu too, beside the other packaged apps') and the classic handler's
# (Registration.cpp, kPasteKeys). So while the package is registered, the classic handler's three keys come
# out; the package's Paste serves both menus. Without the package (Windows 10, or unregistered), regsvr32
# puts them back. The classic handler's CLSID stays registered either way, and the right-drag items never
# change.

$script:PackageName = 'CopyMaster'

# Under HKLM\Software\Classes, as Registration.cpp writes them.
$script:ClassicPasteKeys = @(
    'Directory\Background\shellex\ContextMenuHandlers\CopyMaster'
    'Directory\shellex\ContextMenuHandlers\CopyMaster'
    'Drive\shellex\ContextMenuHandlers\CopyMaster'
)

# The Appx cmdlets live in Windows PowerShell; PowerShell 7 reaches them through its compatibility layer.
if ($PSVersionTable.PSEdition -eq 'Core') {
    Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
}

function Get-CopyMasterPackage {
    @(Get-AppxPackage -Name $script:PackageName -AllUsers -ErrorAction SilentlyContinue)
}

# Takes the classic Paste out of the right-click menus (the package's Paste stays). Says what it did.
function Remove-ClassicPaste {
    $removed = 0
    foreach ($key in $script:ClassicPasteKeys) {
        # Each key's own parent (...\ContextMenuHandlers), opened for writing: asking that of
        # HKLM\Software\Classes itself is refused.
        $parentPath = 'Software\Classes\' + (Split-Path $key -Parent)
        $parent = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($parentPath, $true)
        if (-not $parent) { continue }
        try {
            $leaf = Split-Path $key -Leaf
            if ($parent.OpenSubKey($leaf)) {
                $parent.DeleteSubKeyTree($leaf)
                $removed++
            }
        } finally {
            $parent.Dispose()
        }
    }
    if ($removed) {
        Send-AssociationChanged
        Write-Host 'The classic CopyMaster Paste is out of the menus: the package''s Paste shows there instead.'
    }
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

# The classic Paste, back (regsvr32 of the deployed DLL writes every key again).
function Restore-ClassicPaste([string]$Dll) {
    if (-not (Test-Path $Dll)) { return }
    $regsvr32 = Join-Path $env:SystemRoot 'System32\regsvr32.exe'
    $process = Start-Process $regsvr32 -ArgumentList '/s', "`"$Dll`"" -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "regsvr32 failed (exit $($process.ExitCode))." }
    Write-Host 'The classic CopyMaster Paste is back in the menus.'
}

Export-ModuleMember -Function Get-CopyMasterPackage, Remove-ClassicPaste, Restore-ClassicPaste
