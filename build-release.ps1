param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

[xml]$project = Get-Content -LiteralPath 'src/EchoReplay/EchoReplay.csproj' -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a stable major.minor.patch version in the project file.' }
if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe')
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $InnoCompiler) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $InnoCompiler = $command.Source }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw 'Inno Setup 6.5+ is required. Install it or pass -InnoCompiler with the full path to ISCC.exe.'
}

& './build.ps1' -Portable
if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }

# Stage only known distribution files, never arbitrary existing output files.
$staging = Join-Path $PSScriptRoot ('artifacts/release-work/' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'EchoReplay'
$release = Join-Path $PSScriptRoot 'artifacts/release'
New-Item -ItemType Directory -Force -Path $payload,$release | Out-Null
Copy-Item -LiteralPath 'artifacts/EchoReplay-portable/EchoReplay.exe' -Destination $payload
Copy-Item -LiteralPath 'README.md','VALIDATION.md','CHANGELOG.md','CONTRIBUTING.md','THIRD-PARTY-NOTICES.txt' -Destination $payload
Copy-Item -LiteralPath 'docs','artifacts/EchoReplay-portable/licenses' -Destination $payload -Recurse

$compilerLog = & $InnoCompiler "/DAppVersion=$version" "/DPayloadDir=$payload" "/DReleaseDir=$release" 'installer/EchoReplay.iss' 2>&1
$compilerExit = $LASTEXITCODE
$compilerLog | Write-Output
if ($compilerExit -ne 0) { throw ('Installer compilation failed: ' + (($compilerLog | Select-Object -Last 12) -join "`n")) }
$zip = Join-Path $release 'EchoReplay-portable-win-x64.zip'
Compress-Archive -LiteralPath $payload -DestinationPath $zip -Force
$installer = Join-Path $release 'EchoReplay-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer output is missing.' }
$hashes = foreach ($file in @($installer,$zip)) {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($file)
}
[IO.File]::WriteAllLines((Join-Path $release 'SHA256SUMS.txt'), $hashes, [Text.Encoding]::ASCII)
Write-Output "EchoReplay $version release files: $release"
