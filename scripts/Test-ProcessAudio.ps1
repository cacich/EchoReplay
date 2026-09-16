param([string]$TestBinary = 'tests/EchoReplay.Tests/bin/Release/net10.0-windows/EchoReplay.Tests.exe')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$TestBinary = (Resolve-Path -LiteralPath $TestBinary).Path
$probeRoot = Join-Path $workspace ('artifacts/process-integration-' + [guid]::NewGuid().ToString('N'))
$helpers = Join-Path $probeRoot 'helpers'
New-Item -ItemType Directory -Path $helpers -Force | Out-Null
Get-ChildItem -LiteralPath ([IO.Path]::GetDirectoryName($TestBinary)) -File | Copy-Item -Destination $helpers
Copy-Item -LiteralPath $TestBinary -Destination (Join-Path $helpers 'EchoReplayVoiceProbe.exe')
Copy-Item -LiteralPath $TestBinary -Destination (Join-Path $helpers 'EchoReplayGameProbe.exe')
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
function Start-Tone([string]$Executable, [int]$Frequency, [string]$Name) {
    $arguments = '--tone {0} "{1}" "{2}"' -f $Frequency,(Join-Path $probeRoot "$Name.ready"),(Join-Path $probeRoot "$Name.stop")
    $process = Start-Process -FilePath (Join-Path $helpers $Executable) -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($process)
}
try {
    Start-Tone 'EchoReplayVoiceProbe.exe' 880 'voice'
    Start-Tone 'EchoReplayGameProbe.exe' 1320 'game'
    $readyTimeout = [Diagnostics.Stopwatch]::StartNew()
    while (-not ((Test-Path (Join-Path $probeRoot 'voice.ready')) -and (Test-Path (Join-Path $probeRoot 'game.ready')))) {
        if ($readyTimeout.Elapsed.TotalSeconds -gt 20) { throw "Tone helpers failed to start: $probeRoot" }
        Start-Sleep -Milliseconds 100
    }
    $report = Join-Path $probeRoot 'report.json'
    $controller = Start-Process -FilePath $TestBinary -ArgumentList ('--engine-capture "{0}"' -f $report) -WindowStyle Hidden -PassThru
    $ownedProcesses.Add($controller)
    $deadline = [Diagnostics.Stopwatch]::StartNew()
    $restarted = $false
    while (-not $controller.HasExited) {
        if (-not $restarted -and (Test-Path (Join-Path $probeRoot 'restart.request'))) { Start-Tone 'EchoReplayVoiceProbe.exe' 880 'voice-restarted'; $restarted = $true }
        if ($deadline.Elapsed.TotalSeconds -gt 75) { throw "Process audio diagnostic timed out: $probeRoot" }
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $report)) { throw "Diagnostic report missing: $probeRoot" }
    Get-Content -LiteralPath $report
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.Success) { throw 'Process audio integration test failed.' }
    Write-Output "PASS process audio integration: $probeRoot"
} finally {
    # Stop only helpers/controller created by this test; never existing user apps.
    foreach ($process in $ownedProcesses) { if (-not $process.HasExited) { $process.Kill(); $null = $process.WaitForExit(5000) }; $process.Dispose() }
}
