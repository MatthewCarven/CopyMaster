# End-to-end check of the real CopyMaster.exe, driven by UI Automation. Build first.
# The window shows on screen for about 15 seconds; don't type while it runs.
#   1. Hand-over: a second launch, a --job file and a bare launch go to the first instance.
#   2. A conflict answered in its dialog (Overwrite), from the job's own thread.
#   3. Quitting with a job still running: asked, "Cancel and quit", no temp file left.
# Your settings.json is put aside for the run and put back afterwards.
#
#   .\dev\window-e2e.ps1 [-Configuration Release]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

$exe = Join-Path $PSScriptRoot "..\src\CopyMaster\bin\$Configuration\net10.0-windows\CopyMaster.exe"
if (-not (Test-Path $exe)) { throw "Build first: $exe isn't there." }
if (Get-Process CopyMaster -ErrorAction SilentlyContinue) { throw 'Quit the running CopyMaster first.' }

$appData = Join-Path $env:LOCALAPPDATA 'CopyMaster'
$settings = Join-Path $appData 'settings.json'
$jobs = Join-Path $appData 'jobs'
$appDataExisted = Test-Path $appData
$jobsExisted = Test-Path $jobs
$savedSettings = if (Test-Path $settings) { Get-Content -Raw $settings } else { $null }

$work = Join-Path $env:TEMP "CopyMasterE2E\$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { "  ok    $what" } else { "  FAIL  $what"; $script:failures++ }
}

$root = [Windows.Automation.AutomationElement]::RootElement
function Find-Window([string]$prefix, [int]$seconds = 10) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $windows = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Window)
    while ((Get-Date) -lt $deadline) {
        foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Descendants, $windows)) {
            if ($w.Current.Name.StartsWith($prefix)) { return $w }
        }
        Start-Sleep -Milliseconds 200
    }
    throw "No window whose title starts '$prefix'."
}
function Click([Windows.Automation.AutomationElement]$window, [string]$button) {
    $b = $window.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::NameProperty, $button)))
    if (-not $b) { throw "No '$button' button." }
    $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Start-CopyMaster([string[]]$arguments, [switch]$Wait) {
    $quoted = $arguments | ForEach-Object { if ($_ -match ' ') { "`"$_`"" } else { $_ } }
    if ($quoted) { Start-Process $exe -ArgumentList $quoted -PassThru -Wait:$Wait } else { Start-Process $exe -PassThru -Wait:$Wait }
}
function Wait-For([scriptblock]$condition, [int]$seconds = 20) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline -and -not (& $condition)) { Start-Sleep -Milliseconds 200 }
    & $condition
}

try {
    New-Item -ItemType Directory -Force "$work\a", "$work\b", "$work\c", "$work\to-a", "$work\to-b", "$work\to-c",
        "$work\conflict", "$work\slow" | Out-Null
    $bytes = [byte[]]::new(3MB); (New-Object Random 1).NextBytes($bytes)
    foreach ($n in 'a', 'b', 'c') { [IO.File]::WriteAllBytes("$work\$n\$n.bin", $bytes) }
    $hash = (Get-FileHash "$work\a\a.bin").Hash
    Set-Content "$work\a\same.txt" 'new contents'
    Set-Content "$work\conflict\same.txt" 'old contents'
    $big = [IO.File]::Create("$work\a\big.bin"); $big.SetLength(64MB); $big.Close()

    # Ask on conflicts, and 2 MB/s so the slow job is still running when we quit.
    New-Item -ItemType Directory -Force $appData | Out-Null
    '{ "SpeedLimitMBps": 2, "Verify": true, "Conflicts": "Ask" }' | Set-Content $settings

    '1. Hand-over'
    $app = Start-CopyMaster 'copy', '--dest', "$work\to-a", "$work\a\a.bin"
    Start-Sleep -Milliseconds 1500
    $second = Start-CopyMaster 'copy', '--dest', "$work\to-b", "$work\b\b.bin" -Wait
    Check ($second.ExitCode -eq 0) "a second launch hands over and exits 0 (got $($second.ExitCode))"
    New-Item -ItemType Directory -Force $jobs | Out-Null
    $jobFile = Join-Path $jobs "e2e-$([guid]::NewGuid().ToString('N')).cmjob"
    [IO.File]::WriteAllText($jobFile, "CopyMaster job 1`nop=move`ndest=$work\to-c`nsrc=$work\c\c.bin`n", [Text.UTF8Encoding]::new($false))
    $third = Start-CopyMaster '--job', $jobFile -Wait
    Check ($third.ExitCode -eq 0 -and -not (Test-Path $jobFile)) 'a --job launch hands over, and its job file is deleted'
    $bare = Start-CopyMaster -Wait
    Check ($bare.ExitCode -eq 0) 'a bare launch just shows the window'
    Check (@(Get-Process CopyMaster).Count -eq 1) 'one CopyMaster process'
    $copied = Wait-For { (Test-Path "$work\to-a\a.bin") -and (Test-Path "$work\to-b\b.bin") -and (Test-Path "$work\to-c\c.bin") }
    Check $copied 'all three jobs ran'
    Start-Sleep -Milliseconds 300
    Check ((Get-FileHash "$work\to-b\b.bin").Hash -eq $hash -and (Get-FileHash "$work\to-c\c.bin").Hash -eq $hash) 'the copies match'
    Check (-not (Test-Path "$work\c\c.bin")) 'the move removed its source'

    '2. A conflict'
    Start-CopyMaster 'copy', '--dest', "$work\conflict", "$work\a\same.txt" -Wait | Out-Null
    $dialog = Find-Window 'File already there'
    Click $dialog 'Overwrite'
    Check (Wait-For { (Get-Content "$work\conflict\same.txt" -Raw).Trim() -eq 'new contents' } 10) 'Overwrite in the dialog replaced the file'

    '3. Quitting mid-copy'
    Start-CopyMaster 'copy', '--dest', "$work\slow", "$work\a\big.bin" -Wait | Out-Null
    Check (Wait-For { @(Get-ChildItem "$work\slow" -Force -Filter '.~cm*.tmp').Count -eq 1 } 10) 'the slow copy is under way (one temp file)'
    [void]$app.CloseMainWindow()
    $ask = Find-Window 'CopyMaster' 5
    Click $ask 'Cancel and quit'
    Check ($app.WaitForExit(15000) -and $app.ExitCode -eq 0) 'Cancel and quit: exits 0'
    Check (@(Get-ChildItem "$work\slow" -Force).Count -eq 0) 'no temp file left behind'
}
finally {
    Get-Process CopyMaster -ErrorAction SilentlyContinue | Stop-Process
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    if ($savedSettings -ne $null) { Set-Content $settings $savedSettings -NoNewline } else { Remove-Item $settings -ErrorAction SilentlyContinue }
    if (-not $jobsExisted) { Remove-Item -Recurse -Force $jobs -ErrorAction SilentlyContinue }
    if (-not $appDataExisted -and (Test-Path $appData) -and -not (Get-ChildItem $appData -Force)) { Remove-Item $appData }
}
if ($failures -gt 0) { "$failures check(s) failed."; exit 1 } else { 'All checks passed.' }
