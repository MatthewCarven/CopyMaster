# Makes (or removes) two small virtual disks for the engine's integration tests:
#   big   256 MB  a second volume, for real moves between volumes
#   small  32 MB  a volume to fill up, for disk-full handling
# Each is mounted on an empty folder rather than a drive letter, so nothing pops up in
# Explorer. Needs admin rights (diskpart). Works on Windows Home (no Hyper-V needed).
#
#   .\dev\test-volumes.ps1            make and mount them; prints the variables to set
#   .\dev\test-volumes.ps1 -Remove    unmount and delete them
param(
    [switch]$Remove,
    [string]$Root = (Join-Path $env:TEMP 'CopyMasterTestVolumes')
)
$ErrorActionPreference = 'Stop'

$disks = @(
    @{ Name = 'big';   SizeMB = 256; Label = 'CMTEST-BIG' },
    @{ Name = 'small'; SizeMB = 32;  Label = 'CMTEST-SMALL' }
)

function Invoke-Diskpart([string[]]$Lines) {
    $script = Join-Path $Root 'diskpart.txt'
    Set-Content -Path $script -Value $Lines -Encoding ascii
    $output = & diskpart.exe /s $script 2>&1
    $code = $LASTEXITCODE
    Remove-Item $script -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "diskpart failed ($code):`n$($output -join "`n")" }
}

if ($Remove) {
    foreach ($disk in $disks) {
        $file = Join-Path $Root "$($disk.Name).vhdx"
        if (Test-Path $file) {
            Invoke-Diskpart @("select vdisk file=`"$file`"", 'detach vdisk')
            Remove-Item $file
        }
        $mount = Join-Path $Root "mnt-$($disk.Name)"
        if (Test-Path $mount) { Remove-Item $mount -Recurse -Force }
    }
    if ((Test-Path $Root) -and -not (Get-ChildItem $Root -Force)) { Remove-Item $Root }
    'Removed.'
    return
}

New-Item -ItemType Directory -Force $Root | Out-Null
foreach ($disk in $disks) {
    $file = Join-Path $Root "$($disk.Name).vhdx"
    $mount = Join-Path $Root "mnt-$($disk.Name)"
    if (Test-Path $file) { throw "$file already exists; run with -Remove first." }
    New-Item -ItemType Directory -Force $mount | Out-Null
    Invoke-Diskpart @(
        "create vdisk file=`"$file`" maximum=$($disk.SizeMB) type=expandable",
        "select vdisk file=`"$file`"",
        'attach vdisk',
        'convert mbr',
        'create partition primary',
        "format fs=ntfs quick label=$($disk.Label)",
        "assign mount=`"$mount`""
    )
}

"Set these for the integration tests:"
"`$env:CM_TEST_VOLUME = '$(Join-Path $Root 'mnt-big')'"
"`$env:CM_TEST_SMALL_VOLUME = '$(Join-Path $Root 'mnt-small')'"
