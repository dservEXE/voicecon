param([switch]$Archive)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$sdkPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkPath)) { $sdkPath = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
[xml]$project = Get-Content -LiteralPath 'src\Voicecon\Voicecon.csproj'
$version = [string]$project.Project.PropertyGroup.Version
$destination = Join-Path $PSScriptRoot 'artifacts\publish'
& $sdkPath publish 'src\Voicecon\Voicecon.csproj' -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:RestoreLockedMode=true' -o $destination
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
$portablePath = Join-Path $PSScriptRoot 'artifacts\Voicecon.exe'
try { Copy-Item -LiteralPath (Join-Path $destination 'Voicecon.exe') -Destination $portablePath -Force }
catch [System.IO.IOException] {
    $portablePath = Join-Path $destination 'Voicecon.exe'
}
if ($Archive) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $destination 'README.md') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $destination 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $destination 'THIRD-PARTY-NOTICES.txt') -Force
    Compress-Archive -Path (Join-Path $destination '*') -DestinationPath (Join-Path $PSScriptRoot 'artifacts\Voicecon-win-x64.zip') -Force
}
Get-FileHash -LiteralPath $portablePath -Algorithm SHA256
Write-Output ('Portable EXE: ' + $portablePath)
