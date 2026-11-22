param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CurseTorrent'),
    [switch]$RegisterHandlers,
    [switch]$NoShortcuts,
    [switch]$NoUninstallRegistration
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows 11 x64 is required.' }
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$targetRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$sourceExe = Join-Path $sourceRoot 'CurseTorrent.exe'
$targetExe = Join-Path $targetRoot 'CurseTorrent.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw 'Run install.cmd from an extracted CurseTorrent release package.' }
if ($sourceRoot -eq $targetRoot) { throw 'Choose an installation folder different from the extracted package folder.' }
if ($targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The installation folder cannot be inside the package folder.' }
if ($sourceRoot.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The installation folder cannot contain the package folder.' }
if ($targetRoot.Length -le [IO.Path]::GetPathRoot($targetRoot).Length) { throw 'A drive root cannot be an installation folder.' }
foreach ($protected in @($env:WINDIR, $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if ($protected -and $targetRoot -eq [IO.Path]::GetFullPath($protected).TrimEnd('\')) { throw 'Choose a dedicated CurseTorrent folder.' }
}
foreach ($process in @(Get-Process -Name CurseTorrent -ErrorAction SilentlyContinue)) {
    if ($process.Path -and [IO.Path]::GetFullPath($process.Path) -eq $targetExe) { throw 'Exit CurseTorrent from its tray menu before installing an update.' }
}
$previousFiles = @()
$previousShortcuts = @()
$previousUninstaller = $false
if (Test-Path -LiteralPath $targetRoot) {
    $targetItem = Get-Item -LiteralPath $targetRoot
    if (-not $targetItem.PSIsContainer -or ($targetItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'The installation folder must be a regular directory.' }
    if (@(Get-ChildItem -LiteralPath $targetRoot -Force).Count -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $targetRoot '.cursetorrent-install.json'))) { throw 'The folder is not empty and was not created by this installer. Choose another folder.' }
    $oldManifestPath = Join-Path $targetRoot '.cursetorrent-install.json'
    if (Test-Path -LiteralPath $oldManifestPath) {
        $oldManifest = Get-Content -LiteralPath $oldManifestPath -Raw | ConvertFrom-Json
        if ($oldManifest.Schema -ne 1 -or [IO.Path]::GetFullPath($oldManifest.InstallDirectory).TrimEnd('\') -ne $targetRoot) { throw 'The existing installation manifest does not match this folder.' }
        $previousFiles = @($oldManifest.Files)
        $previousShortcuts = @($oldManifest.Shortcuts)
        $previousUninstaller = [bool]$oldManifest.RegisteredUninstaller
    }
}
New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
$relativeFiles = [Collections.Generic.List[string]]::new()
foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force)) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symbolic links are not supported in an installation package.' }
    $relative = $file.FullName.Substring($sourceRoot.Length + 1)
    if ($relative -eq '.cursetorrent-install.json') { continue }
    $destination = Join-Path $targetRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    $relativeFiles.Add($relative)
}
$shortcuts = [Collections.Generic.List[string]]::new()
if (-not $NoShortcuts) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'CurseTorrent.lnk'
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe
    $shortcut.WorkingDirectory = $targetRoot
    $shortcut.IconLocation = "$targetExe,0"
    $shortcut.Description = 'CurseTorrent - BitTorrent client'
    $shortcut.Save()
    $shortcuts.Add($shortcutPath)
}
if ($RegisterHandlers) {
    $classes = 'HKCU:\Software\Classes'
    foreach ($id in @('CurseTorrent.Torrent', 'CurseTorrent.Magnet')) {
        New-Item -Path "$classes\$id\shell\open\command" -Force | Out-Null
        Set-Item -LiteralPath "$classes\$id\shell\open\command" -Value ('"' + $targetExe + '" "%1"')
        New-Item -Path "$classes\$id\DefaultIcon" -Force | Out-Null
        Set-Item -LiteralPath "$classes\$id\DefaultIcon" -Value ('"' + $targetExe + '",0')
    }
    Set-Item -LiteralPath "$classes\CurseTorrent.Torrent" -Value 'BitTorrent file'
    Set-Item -LiteralPath "$classes\CurseTorrent.Magnet" -Value 'Magnet link'
    New-ItemProperty -LiteralPath "$classes\CurseTorrent.Magnet" -Name 'URL Protocol' -Value '' -PropertyType String -Force | Out-Null
    New-Item -Path "$classes\.torrent\OpenWithProgids" -Force | Out-Null
    New-ItemProperty -LiteralPath "$classes\.torrent\OpenWithProgids" -Name 'CurseTorrent.Torrent' -Value '' -PropertyType String -Force | Out-Null
    New-Item -Path "$classes\Applications\CurseTorrent.exe\shell\open\command" -Force | Out-Null
    Set-Item -LiteralPath "$classes\Applications\CurseTorrent.exe\shell\open\command" -Value ('"' + $targetExe + '" "%1"')
    New-ItemProperty -LiteralPath "$classes\Applications\CurseTorrent.exe" -Name FriendlyAppName -Value CurseTorrent -PropertyType String -Force | Out-Null
    New-Item -Path "$classes\Applications\CurseTorrent.exe\SupportedTypes" -Force | Out-Null
    New-ItemProperty -LiteralPath "$classes\Applications\CurseTorrent.exe\SupportedTypes" -Name '.torrent' -Value '' -PropertyType String -Force | Out-Null
    $capabilities = 'HKCU:\Software\CurseTorrent\Capabilities'
    New-Item -Path "$capabilities\FileAssociations" -Force | Out-Null
    New-Item -Path "$capabilities\UrlAssociations" -Force | Out-Null
    New-ItemProperty -LiteralPath $capabilities -Name ApplicationName -Value CurseTorrent -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $capabilities -Name ApplicationDescription -Value 'BitTorrent client' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $capabilities -Name ApplicationIcon -Value ('"' + $targetExe + '",0') -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath "$capabilities\FileAssociations" -Name '.torrent' -Value 'CurseTorrent.Torrent' -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath "$capabilities\UrlAssociations" -Name magnet -Value 'CurseTorrent.Magnet' -PropertyType String -Force | Out-Null
    New-Item -Path 'HKCU:\Software\RegisteredApplications' -Force | Out-Null
    New-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name CurseTorrent -Value 'Software\CurseTorrent\Capabilities' -PropertyType String -Force | Out-Null
}
if (-not $NoUninstallRegistration) {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CurseTorrent'
    New-Item -Path $uninstallKey -Force | Out-Null
    $uninstallCommand = 'powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $targetRoot 'uninstall.ps1') + '"'
    $version = (Get-Item -LiteralPath $targetExe).VersionInfo.ProductVersion
    foreach ($entry in @{DisplayName='CurseTorrent'; DisplayVersion=$version; Publisher='CurseTorrent contributors'; InstallLocation=$targetRoot; DisplayIcon=$targetExe; UninstallString=$uninstallCommand}.GetEnumerator()) {
        New-ItemProperty -LiteralPath $uninstallKey -Name $entry.Key -Value $entry.Value -PropertyType String -Force | Out-Null
    }
    New-ItemProperty -LiteralPath $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
}
$manifest = [ordered]@{
    Schema = 1
    InstallDirectory = $targetRoot
    Files = @($previousFiles + $relativeFiles.ToArray() | Select-Object -Unique)
    Shortcuts = @($previousShortcuts + $shortcuts.ToArray() | Select-Object -Unique)
    RegisteredUninstaller = ($previousUninstaller -or (-not $NoUninstallRegistration))
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $targetRoot '.cursetorrent-install.json') -Encoding UTF8
Write-Host "Installed: $targetExe"
Write-Host 'Downloads and application settings were preserved.'
if ($RegisterHandlers) { Write-Host 'Handlers registered. Choose CurseTorrent in Windows Default apps to make it the default.' }
