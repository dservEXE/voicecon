param([switch]$Network, [switch]$Benchmark)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$sdkPath = Join-Path $PSScriptRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkPath)) { $sdkPath = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$arguments = @('run', '--project', 'tests\Voicecon.Checks\Voicecon.Checks.csproj', '-c', 'Release')
if ($Network) { $arguments += @('--', '--network') }
elseif ($Benchmark) { $arguments += @('--', '--benchmark') }
& $sdkPath @arguments
if ($LASTEXITCODE -ne 0) { throw 'Checks failed' }
