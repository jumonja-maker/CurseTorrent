$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$targetRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$targetExe = Join-Path $targetRoot 'CurseTorrent.exe'
$manifestPath = Join-Path $targetRoot '.cursetorrent-install.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'No installation manifest was found. For a portable copy, delete its program folder yourself after moving any downloads elsewhere.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Schema -ne 1 -or [IO.Path]::GetFullPath($manifest.InstallDirectory).TrimEnd('\') -ne $targetRoot) { throw 'The installation manifest does not match this folder.' }
foreach ($process in @(Get-Process -Name CurseTorrent -ErrorAction SilentlyContinue)) {
    if ($process.Path -and [IO.Path]::GetFullPath($process.Path) -eq $targetExe) { throw 'Exit CurseTorrent from its tray menu before uninstalling.' }
}
$validatedFiles = [Collections.Generic.List[string]]::new()
foreach ($relative in @($manifest.Files)) {
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative)) { throw 'Invalid installation manifest path.' }
    $full = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
    if (-not $full.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The manifest contains a path outside the installation folder.' }
    $parent = Split-Path -Parent $full
    while ($parent -and $parent.Length -ge $targetRoot.Length) {
        if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installation contains a directory link. Remove that link before uninstalling.' }
        if ($parent -eq $targetRoot) { break }
        $parent = Split-Path -Parent $parent
    }
    $validatedFiles.Add($full)
}
$ownsHandlers = $false
foreach ($id in @('CurseTorrent.Torrent', 'CurseTorrent.Magnet')) {
    $key = "HKCU:\Software\Classes\$id"
    if (Test-Path -LiteralPath "$key\shell\open\command") {
        $command = (Get-Item -LiteralPath "$key\shell\open\command").GetValue('')
        if ($command -eq ('"' + $targetExe + '" "%1"')) {
            Remove-Item -LiteralPath $key -Recurse -Force
            $ownsHandlers = $true
        }
    }
}
if ($ownsHandlers) {
    Remove-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.torrent\OpenWithProgids' -Name 'CurseTorrent.Torrent' -ErrorAction SilentlyContinue
    Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name CurseTorrent -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath 'HKCU:\Software\CurseTorrent\Capabilities' -Recurse -Force -ErrorAction SilentlyContinue
}
$applicationKey = 'HKCU:\Software\Classes\Applications\CurseTorrent.exe'
if (Test-Path -LiteralPath "$applicationKey\shell\open\command") {
    $command = (Get-Item -LiteralPath "$applicationKey\shell\open\command").GetValue('')
    if ($command -eq ('"' + $targetExe + '" "%1"')) { Remove-Item -LiteralPath $applicationKey -Recurse -Force }
}
if ($manifest.RegisteredUninstaller) {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CurseTorrent'
    if (Test-Path -LiteralPath $uninstallKey) {
        $installedLocation = (Get-Item -LiteralPath $uninstallKey).GetValue('InstallLocation')
        if ($installedLocation -eq $targetRoot) { Remove-Item -LiteralPath $uninstallKey -Recurse -Force }
    }
}
$expectedShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CurseTorrent.lnk'
foreach ($shortcutPath in @($manifest.Shortcuts)) {
    if ($shortcutPath -eq $expectedShortcut -and (Test-Path -LiteralPath $shortcutPath)) {
        $shell = New-Object -ComObject WScript.Shell
        if ($shell.CreateShortcut($shortcutPath).TargetPath -eq $targetExe) { Remove-Item -LiteralPath $shortcutPath -Force }
    }
}
foreach ($full in $validatedFiles) {
    if (Test-Path -LiteralPath $full -PathType Leaf) { Remove-Item -LiteralPath $full -Force }
}
Remove-Item -LiteralPath $manifestPath -Force
# Inspect only parents of installed files; never walk the user's download folders.
$programDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($full in $validatedFiles) {
    $parent = Split-Path -Parent $full
    while ($parent -and $parent.Length -gt $targetRoot.Length) {
        [void]$programDirectories.Add($parent)
        $parent = Split-Path -Parent $parent
    }
}
foreach ($directory in @($programDirectories | Sort-Object Length -Descending)) {
    if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { Remove-Item -LiteralPath $directory -Force }
}
if (@(Get-ChildItem -LiteralPath $targetRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $targetRoot -Force }
Write-Host 'CurseTorrent was uninstalled. Downloads and saved application data were preserved.'
