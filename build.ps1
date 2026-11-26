# Builds the whole solution (C# and the C++ shell DLL) with Visual Studio's MSBuild.
# `dotnet build` can't build the .vcxproj (MSB4278), so use this for everything,
# or `dotnet build`/`dotnet test` on single C# projects.
#
#   .\build.ps1                  Debug
#   .\build.ps1 -Configuration Release
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'MSBuild\Current\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'No Visual Studio with the x64 C++ tools found.' }

# -nr:false: no MSBuild nodes left running afterwards, holding files open.
& $msbuild (Join-Path $PSScriptRoot 'CopyMaster.slnx') -restore -m -nr:false -nologo -v:m `
    "-p:Configuration=$Configuration" -p:Platform=x64
exit $LASTEXITCODE
