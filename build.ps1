param([switch]$Portable)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$sdk = if (Test-Path '.tools/dotnet/dotnet.exe') { Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe' } else { 'dotnet' }
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tools/cli'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
& $sdk run --project 'tests/EchoReplay.Tests/EchoReplay.Tests.csproj' -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
if ($Portable) {
    & $sdk publish 'src/EchoReplay/EchoReplay.csproj' -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o 'artifacts/EchoReplay-portable'
} else {
    & $sdk publish 'src/EchoReplay/EchoReplay.csproj' -c Release --self-contained false -o 'artifacts/EchoReplay'
}
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$destination = if ($Portable) { 'artifacts/EchoReplay-portable' } else { 'artifacts/EchoReplay' }
Copy-Item -LiteralPath 'README.md','VALIDATION.md','CHANGELOG.md','CONTRIBUTING.md','THIRD-PARTY-NOTICES.txt' -Destination $destination -Force
Copy-Item -LiteralPath 'docs' -Destination $destination -Recurse -Force
if ($Portable) {
    $licenses = Join-Path $destination 'licenses'
    New-Item -ItemType Directory -Force -Path $licenses | Out-Null
    $corePackage = Get-ChildItem -LiteralPath '.tools/nuget/microsoft.netcore.app.runtime.win-x64' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $desktopPackage = Get-ChildItem -LiteralPath '.tools/nuget/microsoft.windowsdesktop.app.runtime.win-x64' -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    Copy-Item -LiteralPath (Join-Path $corePackage.FullName 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-LICENSE.txt') -Force
    Copy-Item -LiteralPath (Join-Path $corePackage.FullName 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-THIRD-PARTY-NOTICES.txt') -Force
    Copy-Item -LiteralPath (Join-Path $desktopPackage.FullName 'LICENSE') -Destination (Join-Path $licenses 'windowsdesktop-LICENSE.txt') -Force
    Copy-Item -LiteralPath 'installer/Languages/INNO-LICENSE.txt' -Destination (Join-Path $licenses 'INNO-LICENSE.txt') -Force
}
