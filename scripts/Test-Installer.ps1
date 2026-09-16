param([Parameter(Mandatory)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
# Installs and modifies the disposable runner's user profile. Do not run on a
# person's desktop: their shortcuts, application settings and registry are real.
if ($env:GITHUB_ACTIONS -ne 'true' -or -not $env:RUNNER_TEMP) {
    throw 'Run this installation test only on a disposable GitHub Actions Windows runner.'
}
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
$testRoot = Join-Path $env:RUNNER_TEMP ('EchoReplay-installer-' + [guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot 'Installed App'
$profileDir = Join-Path $testRoot 'Test Profile'
$settingsDir = Join-Path $env:LOCALAPPDATA 'EchoReplay'
$settingsFile = Join-Path $settingsDir 'settings.json'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'EchoReplay.lnk'
$runPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{36E87E05-473D-4A07-AF16-44E831245437}_is1'
function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Checked([string]$File, [string]$Arguments, [bool]$ExpectSuccess = $true) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Test process timed out: $File" }
    if ($ExpectSuccess -and $process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $File" }
    if (-not $ExpectSuccess -and $process.ExitCode -eq 0) { throw 'Installer unexpectedly accepted a running application.' }
}
function Get-Startup { (Get-ItemProperty -LiteralPath $runPath -Name EchoReplay -ErrorAction SilentlyContinue).EchoReplay }
function Get-Uninstaller {
    # Inno can choose unins001.exe after a previous uninstall. Query the
    # registration created by this install instead of assuming a file name.
    $command = (Get-ItemProperty -LiteralPath $uninstallPath -Name UninstallString).UninstallString
    $path = [IO.Path]::GetFullPath($command.Trim('"'))
    $expectedRoot = [IO.Path]::GetFullPath($installDir).TrimEnd('\') + '\'
    Assert-True ($path.StartsWith($expectedRoot, [StringComparison]::OrdinalIgnoreCase)) 'Uninstaller path escaped the test installation.'
    Assert-True (Test-Path -LiteralPath $path) "Registered uninstaller is missing: $path"
    return $path
}
Assert-True (-not (Test-Path -LiteralPath $settingsDir)) 'Runner already has EchoReplay settings.'
Assert-True (-not (Test-Path -LiteralPath $uninstallPath)) 'Runner already has EchoReplay installed.'
Assert-True (-not (Test-Path -LiteralPath $shortcut)) 'Runner already has an EchoReplay shortcut.'
Assert-True ($null -eq (Get-Startup)) 'Runner already has EchoReplay startup configured.'
New-Item -ItemType Directory -Force -Path $testRoot,$profileDir,$settingsDir | Out-Null
[IO.File]::WriteAllText($settingsFile, '{"RecordOnLaunch":false,"Minutes":3}')
[IO.File]::WriteAllText((Join-Path $profileDir 'settings.json'), '{"RecordOnLaunch":false,"CaptureMicrophone":false}')
$installArgs = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="{0}" /LOG="{1}"' -f $installDir, (Join-Path $testRoot 'install.log')
Run-Checked $InstallerPath $installArgs
$exe = Join-Path $installDir 'EchoReplay.exe'
Assert-True (Test-Path -LiteralPath $exe) 'Installed executable missing.'
Assert-True (Test-Path -LiteralPath $shortcut) 'Start menu shortcut missing.'
Assert-True (Test-Path -LiteralPath $uninstallPath) 'Windows uninstall registration missing.'
$uninstaller = Get-Uninstaller
Assert-True ($null -eq (Get-Startup)) 'Installation unexpectedly enabled login startup.'

$snapshot = Join-Path $testRoot 'installed-ui.png'
$app = Start-Process -FilePath $exe -ArgumentList ('--data-dir "{0}" --ui-snapshot "{1}"' -f $profileDir,$snapshot) -WindowStyle Hidden -PassThru
try {
    Assert-True ($app.WaitForInputIdle(15000)) 'Installed application did not initialize.'
    $actualGuard = [Threading.Mutex]::OpenExisting('Local\EchoReplay.Setup')
    $actualGuard.Dispose()
    Assert-True ($app.WaitForExit(30000)) 'Installed application did not finish its snapshot.'
    Assert-True ($app.ExitCode -eq 0) 'Installed application failed.'
} finally { if (-not $app.HasExited) { $app.Kill() } }
Assert-True (Test-Path -LiteralPath $snapshot) 'Installed WPF application did not render.'
$editorReport = Join-Path $testRoot 'editor-report.json'
Run-Checked $exe ('--data-dir "{0}" --editor-diagnose "{1}"' -f $profileDir,$editorReport)
$editorResult = Get-Content -LiteralPath $editorReport -Raw | ConvertFrom-Json
Assert-True ($editorResult.Success -eq $true) ('Installed editor diagnostic failed: ' + ($editorResult | ConvertTo-Json -Compress))
Assert-True (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($editorReport, '.png'))) 'Editor screenshot missing.'
$userAudio = Join-Path $installDir 'user-recording.wav'
[IO.File]::WriteAllText($userAudio, 'retain user data')

# A live application's named mutex must reject both updates and removal.
$guard = [Threading.Mutex]::new($false, 'Local\EchoReplay.Setup')
try {
    Run-Checked $InstallerPath $installArgs $false
    Run-Checked $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' $false
    Assert-True (Test-Path -LiteralPath $exe) 'Blocked uninstall removed the executable.'
} finally { $guard.Dispose() }

# Reinstall to the same path and keep user settings and unrelated recordings.
Run-Checked $InstallerPath $installArgs
$uninstaller = Get-Uninstaller
Assert-True ((Get-Content -LiteralPath $settingsFile -Raw) -eq '{"RecordOnLaunch":false,"Minutes":3}') 'Reinstall changed settings.'
Assert-True (Test-Path -LiteralPath $userAudio) 'Reinstall removed a user file.'
New-Item -Path $runPath -Force | Out-Null
Set-ItemProperty -LiteralPath $runPath -Name EchoReplay -Value ('"{0}" --background' -f $exe)
Run-Checked $uninstaller ('/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="{0}"' -f (Join-Path $testRoot 'uninstall.log'))
Assert-True (-not (Test-Path -LiteralPath $exe)) 'Uninstall left the executable.'
Assert-True (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall left the shortcut.'
Assert-True (-not (Test-Path -LiteralPath $uninstallPath)) 'Uninstall registration was not removed.'
Assert-True ($null -eq (Get-Startup)) 'Own startup entry was not removed.'
Assert-True (Test-Path -LiteralPath $settingsFile) 'Uninstall deleted settings.'
Assert-True ((Get-Content -LiteralPath $userAudio -Raw) -eq 'retain user data') 'Uninstall deleted a user recording.'

# An entry owned by another portable copy must survive this installation's removal.
Run-Checked $InstallerPath $installArgs
$uninstaller = Get-Uninstaller
$otherCommand = '"C:\OtherPortableCopy\EchoReplay.exe" --background'
Set-ItemProperty -LiteralPath $runPath -Name EchoReplay -Value $otherCommand
Run-Checked $uninstaller '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART'
Assert-True ((Get-Startup) -eq $otherCommand) 'Uninstall removed another copy startup entry.'
Remove-ItemProperty -LiteralPath $runPath -Name EchoReplay
Write-Output "PASS installer: install, WPF launch, running-app protection, reinstall, uninstall, user data and startup ownership. Logs: $testRoot"
