param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if ($workspace -eq [IO.Path]::GetPathRoot($workspace).TrimEnd('\') -or
    -not (Test-Path -LiteralPath (Join-Path $workspace 'src\Voicecon\Voicecon.csproj'))) {
    throw 'Run this file from the Voicecon source directory.'
}
$relativeTargets = @(
    '.tools',
    'src\Voicecon\bin', 'src\Voicecon\obj',
    'tests\Voicecon.Checks\bin', 'tests\Voicecon.Checks\obj',
    'src\Voicecon\assets\media-runtime.zip',
    'artifacts\previews', 'artifacts\publish',
    'artifacts\publish-0.2.1', 'artifacts\publish-0.3.0',
    'artifacts\publish-0.4.0', 'artifacts\publish-0.4.1', 'artifacts\publish-0.5.0',
    'artifacts\setup-build', 'artifacts\setup-verify-0.4.0',
    'artifacts\setup-verify-0.4.1', 'artifacts\setup-verify-0.5.0',
    'artifacts\Voicecon-win-x64', 'artifacts\Voicecon-win-x64.zip',
    'artifacts\synthetic-120.h264', 'artifacts\synthetic-30.h264',
    'artifacts\synthetic-60.h264', 'artifacts\synthetic-gpu-native.h264',
    'artifacts\synthetic-gpu-scale.h264',
    'artifacts\Voicecon-0.3.0.exe', 'artifacts\Voicecon-0.4.0.exe',
    'artifacts\Voicecon-0.4.1.exe', 'artifacts\Voicecon.exe',
    'artifacts\Voicecon-Setup-0.4.0.exe', 'artifacts\Voicecon-Setup-0.4.1.exe',
    'artifacts\Voicecon-Setup-0.5.0.exe', 'artifacts\Voicecon-Setup.exe'
)
# The working 0.5 portable and its runtime are deliberately retained until installation.
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Voicecon\runtime'))
$testRuntime = Join-Path $runtimeRoot '6EF294A6605AD713'
$targets = @($relativeTargets | ForEach-Object { Join-Path $workspace $_ }) + @($testRuntime)
$protected = @((Join-Path $workspace 'artifacts\Voicecon-0.5.0.exe'))
try {
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @(
        (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Voicecon.lnk'),
        (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\Voicecon.lnk')
    )) {
        if (Test-Path -LiteralPath $link) { $protected += $shell.CreateShortcut($link).TargetPath }
    }
} catch { throw 'Cannot check Voicecon shortcuts; no files deleted.' }
$validated = @()
foreach ($candidate in $targets) {
    $absolute = [IO.Path]::GetFullPath($candidate).TrimEnd('\')
    if (-not $absolute.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::Equals($absolute, $testRuntime, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Target outside approved directories: ' + $absolute)
    }
    if (-not (Test-Path -LiteralPath $absolute)) { continue }
    $item = Get-Item -LiteralPath $absolute -Force
    $check = $item
    while ($null -ne $check) {
        if (($check.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Reparse path: ' + $check.FullName) }
        if ($check -is [IO.DirectoryInfo]) { $check = $check.Parent } else { $check = $check.Directory }
    }
    if ($item.PSIsContainer -and @(Get-ChildItem -LiteralPath $absolute -Force -Recurse -Attributes ReparsePoint).Count -gt 0) {
        throw ('Reparse entry inside: ' + $absolute)
    }
    $validated += $absolute
}
[long]$freed = 0
foreach ($absolute in $validated) {
    $running = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { } } | Where-Object { $_ })
    if (@(@($running) + @($protected) | Where-Object {
        [string]::Equals($_, $absolute, [StringComparison]::OrdinalIgnoreCase) -or
        $_.StartsWith($absolute + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count -gt 0) { Write-Host ('KEEP active or shortcut target: ' + $absolute); continue }
    $item = Get-Item -LiteralPath $absolute -Force
    [long]$bytes = $item.Length
    if ($item.PSIsContainer) { $bytes = [long](Get-ChildItem -LiteralPath $absolute -File -Force -Recurse | Measure-Object -Property Length -Sum).Sum }
    if ($Apply) {
        if ($item.PSIsContainer) { Remove-Item -LiteralPath $absolute -Recurse -Force }
        else { Remove-Item -LiteralPath $absolute -Force }
        Write-Host ('REMOVED: ' + $absolute)
    } else { Write-Host ('PREVIEW: ' + $absolute) }
    $freed += $bytes
}
Write-Host ('{0}: {1:N2} GiB. Source files, profiles, contacts and the working portable are preserved.' -f $(if ($Apply) { 'Removed' } else { 'Can remove' }), ($freed / 1GB))
if (-not $Apply) { Write-Host 'To delete the listed generated files, run: .\cleanup-local.ps1 -Apply' }
