$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$expected = '35B004C78016BD7A141F3C4AF10A49E1218C1AB65D6A62A78573679365A4C0AD'
$source = Join-Path $PSScriptRoot '.tools\ffmpeg-lgpl-win64.zip'
New-Item -ItemType Directory -Force '.tools' | Out-Null
if (-not (Test-Path -LiteralPath $source)) {
    $local = Join-Path $PSScriptRoot '.tools\ffmpeg-download\ffmpeg.zip'
    if ((Test-Path -LiteralPath $local) -and (Get-FileHash -LiteralPath $local -Algorithm SHA256).Hash -eq $expected) { Copy-Item -LiteralPath $local -Destination $source }
    else {
        try { Invoke-WebRequest 'https://github.com/akimicj/voicecon/releases/download/media-runtime-v1/ffmpeg-lgpl-win64.zip' -OutFile $source -Headers @{'User-Agent'='Voicecon-Build'} }
        catch { Invoke-WebRequest 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/assets/701810522' -OutFile $source -Headers @{'User-Agent'='Voicecon-Build';Accept='application/octet-stream'} }
    }
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected) { throw 'FFmpeg checksum differs from the reviewed version. Do not publish this build.' }
$unpacked = Join-Path $PSScriptRoot '.tools\ffmpeg-unpacked'
Expand-Archive -LiteralPath $source -DestinationPath $unpacked -Force
$binary = (Get-ChildItem -LiteralPath $unpacked -Filter ffmpeg.exe -Recurse | Select-Object -First 1).Directory.FullName
if (-not $binary) { throw 'FFmpeg executable not found.' }
$assets = Join-Path $PSScriptRoot 'src\Voicecon\assets'; New-Item -ItemType Directory -Force $assets | Out-Null
$temporary = Join-Path $assets 'media-runtime.zip.tmp'; $archivePath = Join-Path $assets 'media-runtime.zip'
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Create($temporary)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$true)
    try {
        $files = @((Get-ChildItem -LiteralPath $binary -File | Where-Object { $_.Extension -eq '.dll' -or $_.Name -eq 'ffmpeg.exe' })) + @(Get-ChildItem media -File)
        foreach ($file in ($files | Sort-Object Name)) {
            $entry = $zip.CreateEntry($file.Name,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $input = $file.OpenRead(); $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose();$input.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
[IO.File]::Move($temporary,$archivePath,$true)
Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
