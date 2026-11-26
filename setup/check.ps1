<#
Lists every trace of CopyMaster on this machine, and changes nothing: install folders, the menus' registration
(HKLM, and HKCU in case), the package and its folders, your settings and logs, files waiting to be deleted at the
next restart, CopyMaster.exe running and processes with CopyMasterShell.dll loaded, Explorer's cached notes on its
CLSIDs; and Windows' own records of it (the taskbar's usage counts, prefetch, the package's bookkeeping left after
it went), which are listed but aren't CopyMaster's to remove.

  .\setup\check.ps1                  the places CopyMaster and Windows are known to use (a few seconds)
  .\setup\check.ps1 -Deep            also search the registry (HKCU, HKLM\SOFTWARE, HKLM\SYSTEM\CurrentControlSet)
                                     for "CopyMaster" and its CLSIDs (a minute or two)
  .\setup\check.ps1 -Path <folder>   look in that folder too (an install somewhere unusual)

Exit code 0 when nothing of CopyMaster's own is left (Windows' records, and deletes waiting for a restart, aside).
From an elevated PowerShell it sees the package for all users and Windows' prefetch folder.
#>
param(
    [switch]$Deep,
    [string[]]$Path = @()
)
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'CopyMasterSetup.psm1') -Force
$traces = @(Get-CopyMasterTrace -Folder $Path -Deep:$Deep)
Show-CopyMasterTrace $traces
if (@($traces | Where-Object { $_.Owner -eq 'CopyMaster' }).Count) { exit 1 }
exit 0
