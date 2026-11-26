<#
Installs CopyMaster from this repo: builds Release, puts it in %ProgramFiles%\CopyMaster, registers the right-drag
and right-click menus (regsvr32: HKLM), and on Windows 11 registers the package that puts Paste in the compact menu.
Run it again to update an install in place.

  .\setup\install.ps1                      build Release, install, register
  .\setup\install.ps1 -NoBuild             install what's already built
  .\setup\install.ps1 -Path <folder>       install somewhere else
  .\setup\install.ps1 -NoPackage           no package: the classic Paste only (as on Windows 10)
  .\setup\install.ps1 -RestartExplorer     restart Explorer if it runs an older CopyMasterShell.dll
  .\setup\install.ps1 -WhatIf              say what it would do, and do nothing
  .\setup\install.ps1 -FilesOnly -Path <folder>
                                           only lay the files out: no registration, no package (for testing)

Needs an elevated PowerShell (HKLM, Program Files, a package for all users), x64 Windows, and the .NET 10 Desktop
Runtime for CopyMaster.exe. Building needs Visual Studio 2026 (build.ps1); the package needs makeappx from the
Windows SDK.

It refuses while CopyMaster.exe runs from the install folder (it may be copying; never stopped), and when CopyMaster
is installed in another folder (uninstall that first: .\setup\uninstall.ps1).

The package is unsigned (its Publisher carries the OID Windows 11 accepts with -AllowUnsigned), so no certificate is
made or trusted. Its Paste shows in the classic menu too, so with it in, the classic handler's Paste keys come out
(two would show). Updating it ends anything running inside it, which is why CopyMaster mustn't be running.

A first install needs no Explorer restart. An update replaces CopyMasterShell.dll, which Explorer keeps using until
it restarts (the old one is renamed aside meanwhile); -RestartExplorer does that, and closes every Explorer window.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Path,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$NoPackage,
    [switch]$RestartExplorer,
    [switch]$FilesOnly
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'CopyMasterSetup.psm1') -Force
$places = Get-CopyMasterPlaces

if ($FilesOnly -and -not $Path) { throw '-FilesOnly lays the files out somewhere for a test: say where with -Path.' }
Assert-SetupHost -NeedAdmin:(-not $FilesOnly)
if (-not $Path) { $Path = $places.DefaultFolder }
$Path = [IO.Path]::GetFullPath($Path).TrimEnd('\')

if (-not $FilesOnly) {
    $registered = Get-CopyMasterInstallFolder
    if ($registered -and $registered -ne $Path) {
        throw "CopyMaster is installed in $registered. Uninstall it first (.\setup\uninstall.ps1), or update it there (-Path `"$registered`")."
    }
    # The Appx cmdlets, loaded with WhatIf off (CopyMasterSetup.psm1, Import-Appx): reading packages changes nothing.
    $whatIf = $WhatIfPreference
    $WhatIfPreference = $false
    $null = Get-CopyMasterPackage
    $WhatIfPreference = $whatIf
}
if (-not (Test-DotNetDesktopRuntime)) {
    Write-Warning 'The .NET 10 Desktop Runtime isn''t installed, so CopyMaster.exe won''t start until it is (https://dotnet.microsoft.com/download/dotnet/10.0).'
}

if (-not $NoBuild -and $PSCmdlet.ShouldProcess($root, "Build $Configuration")) {
    & (Join-Path $root 'build.ps1') -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit $LASTEXITCODE)." }
}
$files = @(Get-CopyMasterBuild $root $Configuration)

Assert-CopyMasterNotRunning $Path

$restartNeeded = $false
if ($PSCmdlet.ShouldProcess($Path, "Copy CopyMaster's $($files.Count) files ($Configuration)")) {
    $restartNeeded = Copy-CopyMasterFiles $files $Path
    Write-Host "Copied $Configuration to $Path."
}
if ($FilesOnly) { return }

$dll = Join-Path $Path 'CopyMasterShell.dll'
if ($PSCmdlet.ShouldProcess($dll, 'Register the right-drag and right-click menus (regsvr32, HKLM)')) {
    Invoke-Regsvr32 $dll
    Write-Host "Registered $dll."
}

if (-not (Test-PackageSupported)) {
    Write-Host 'Windows 10: no compact menu, so no package; Paste is in the classic menu.'
} elseif ($NoPackage) {
    if (Get-CopyMasterPackage) {
        Write-Warning 'The package from an earlier install is still registered (-NoPackage leaves it alone); .\dev\package.ps1 -Unregister takes it out.'
    }
} else {
    $version = Get-ManifestVersion $root
    $current = Get-CopyMasterPackage | Select-Object -First 1
    if ($current -and $current.Version -eq $version -and (Get-CopyMasterPackageRoot $current) -eq $Path) {
        Write-Host "The package $($current.PackageFullName) is registered already, from $Path."
    } elseif ($PSCmdlet.ShouldProcess("CopyMaster $version, external location $Path", 'Build and register the package for all users (unsigned)')) {
        $msix = New-CopyMasterPackage $root
        Register-CopyMasterPackage $msix $Path
    }
    # With the package in, its Paste serves the classic menu too: no second one.
    if ($PSCmdlet.ShouldProcess('the classic CopyMaster Paste (3 keys in HKLM)', 'Take out of the menus')) {
        if (Get-CopyMasterPackage) { Remove-ClassicPaste }
    }
}

if ($restartNeeded) {
    if ($RestartExplorer) {
        if ($PSCmdlet.ShouldProcess('Explorer', 'Restart (closes its windows)')) { Restart-Explorer }
    } else {
        Write-Host 'Explorer is still using the older CopyMasterShell.dll; it takes up the new one when it restarts (-RestartExplorer, or sign out and in).'
    }
}

if (-not $WhatIfPreference) {
    Write-Host ''
    Show-CopyMasterTrace @(Get-CopyMasterTrace -Folder $Path | Where-Object { $_.Owner -ne 'Windows' })
}
