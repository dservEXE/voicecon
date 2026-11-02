$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
[xml]$project = Get-Content -LiteralPath 'src\Voicecon\Voicecon.csproj'
$version = [string]$project.Project.PropertyGroup.Version
$payload = Join-Path $PSScriptRoot 'artifacts\publish\Voicecon.exe'
if (-not (Test-Path -LiteralPath $payload)) { throw 'Run build.ps1 first.' }
$output = Join-Path $PSScriptRoot 'artifacts\setup-build'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$versionSource = Join-Path $output 'BuildInfo.cs'
[IO.File]::WriteAllText($versionSource, '[assembly: System.Reflection.AssemblyVersion("' + $version + '.0")] internal static class BuildInfo { public const string Version = "' + $version + '"; }')
$manifest = Join-Path $output 'setup.manifest'
[xml]$xml = Get-Content installer\setup.manifest
$xml.assembly.assemblyIdentity.version = $version + '.0'; $xml.Save($manifest)
$uninstall = Join-Path $output 'Uninstall.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll ('/win32manifest:' + $manifest) ('/win32icon:' + (Join-Path $PSScriptRoot 'src\Voicecon\assets\voicecon.ico')) ('/out:' + $uninstall) 'installer\Uninstall.cs' $versionSource
if ($LASTEXITCODE -ne 0) { throw 'Uninstaller compilation failed.' }
$hash = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
$hashSource = Join-Path $output 'PayloadHash.cs'
[IO.File]::WriteAllText($hashSource, 'internal static class PayloadHash { public const string Value = "' + $hash + '"; }')
$installer = Join-Path $PSScriptRoot 'artifacts\Voicecon-Setup.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll ('/win32manifest:' + $manifest) ('/win32icon:' + (Join-Path $PSScriptRoot 'src\Voicecon\assets\voicecon.ico')) ('/out:' + $installer) ('/resource:' + $payload + ',Voicecon.Payload') ('/resource:' + $uninstall + ',Voicecon.Uninstall') 'installer\Setup.cs' 'installer\UpdateInstall.cs' $hashSource $versionSource
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Get-FileHash -LiteralPath $installer -Algorithm SHA256
