param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\CurseTorrent'),
    [switch]$RegisterHandlers,
    [switch]$NoShortcuts,
    [switch]$NoUninstallRegistration
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows 11 x64 is required.' }
. (Join-Path $PSScriptRoot 'Installer.Common.ps1')
$sourceRoot = Get-InstallRoot $PSScriptRoot
$targetRoot = Get-InstallRoot $InstallDirectory
$sourceExe = Join-Path $sourceRoot 'CurseTorrent.exe'
$targetExe = Join-Path $targetRoot 'CurseTorrent.exe'
if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) { throw 'Run install.cmd from an extracted CurseTorrent release package.' }
if ($sourceRoot -eq $targetRoot -or $targetRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $sourceRoot.StartsWith($targetRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use separate package and installation folders; neither folder can contain the other.' }
foreach ($protected in @($env:WINDIR, $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    if ($protected -and $targetRoot -eq [IO.Path]::GetFullPath($protected).TrimEnd('\')) { throw 'Choose a dedicated CurseTorrent folder.' }
}
$installLock = Enter-InstallLock $targetRoot
try {
    foreach ($process in @(Get-Process -Name CurseTorrent -ErrorAction SilentlyContinue)) {
        if ($process.Path -and [IO.Path]::GetFullPath($process.Path) -eq $targetExe) { throw 'Exit CurseTorrent from its tray menu before installing an update.' }
    }
    Assert-InstallPath $targetRoot
    if ((Test-Path -LiteralPath $targetRoot) -and -not (Test-Path -LiteralPath $targetRoot -PathType Container)) { throw 'The installation folder must be a regular directory.' }
    Repair-InstallTransaction $targetRoot
    $oldManifest = Read-InstallManifest $targetRoot
    if ((Test-Path -LiteralPath $targetRoot) -and @(Get-ChildItem -LiteralPath $targetRoot -Force).Count -gt 0 -and $null -eq $oldManifest) { throw 'The folder is not empty and was not created by this installer. Choose another folder.' }

    # Read and fingerprint the entire package before changing the existing program.
    # Never follow source links or import installer control files from another install.
    $packageFiles = @(Get-InstallPackageFiles $sourceRoot | Sort-Object Path)
    if ($packageFiles.Count -eq 0 -or $packageFiles.Count -gt 20000) { throw 'The package contains an unsupported number of files.' }
    $incoming = @{}
    $operations = [Collections.Generic.List[object]]::new()
    $hashes = [ordered]@{}
    foreach ($file in $packageFiles) {
        if ($incoming.ContainsKey($file.Path)) { throw 'Duplicate file in the installation package.' }
        $incoming[$file.Path] = $true
        $hashes[$file.Path] = $file.Hash
        Assert-InstallPath $targetRoot $file.Path
        $target = Join-Path $targetRoot $file.Path
        $oldHash = Get-InstallHash $target
        if ($null -ne $oldHash) {
            if ($null -eq $oldManifest -or -not $oldManifest.Files.ContainsKey($file.Path)) { throw ('A user-owned file occupies a package path and was preserved: ' + $target + '. Move it to another folder before updating.') }
            if ($oldManifest.Schema -eq 2 -and $oldHash -ne $oldManifest.Files[$file.Path] -and $oldHash -ne $file.Hash) { throw ('A file was changed since installation and was preserved: ' + $target + '. Move it to another folder before updating.') }
        }
        if ($oldHash -ne $file.Hash) { $operations.Add([pscustomobject]@{ Path = $file.Path; OldHash = $oldHash; NewHash = $file.Hash; SourcePath = $file.SourcePath }) }
    }
    if ($null -ne $oldManifest -and $oldManifest.Schema -eq 2) {
        # Remove obsolete program files only while they still match the old manifest.
        foreach ($relative in @($oldManifest.Files.Keys | Sort-Object)) {
            if ($incoming.ContainsKey($relative)) { continue }
            $current = Get-InstallHash (Join-Path $targetRoot $relative)
            if ($null -ne $current -and $current -eq $oldManifest.Files[$relative]) { $operations.Add([pscustomobject]@{ Path = $relative; OldHash = $current; NewHash = $null }) }
            elseif ($null -ne $current) { Write-Host ('Preserved changed file from the previous version: ' + $relative) }
        }
    }
    $shortcuts = @()
    $previousUninstaller = $false
    if ($null -ne $oldManifest) { $shortcuts = @($oldManifest.Shortcuts); $previousUninstaller = $oldManifest.RegisteredUninstaller }
    $shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'CurseTorrent.lnk'
    if (-not $NoShortcuts) { $shortcuts = @($shortcuts + $shortcutPath | Select-Object -Unique) }
    $id = [Guid]::NewGuid().ToString('N')
    $legacy = $null -ne $oldManifest -and $oldManifest.Schema -eq 1
    $manifest = [ordered]@{
        Schema = 2
        InstallDirectory = $targetRoot
        Files = @($packageFiles | ForEach-Object { $_.Path })
        Hashes = $hashes
        Shortcuts = @($shortcuts)
        RegisteredUninstaller = ($previousUninstaller -or (-not $NoUninstallRegistration))
    }
    if ($legacy) { $manifest.LegacyBackupDirectory = (Get-InstallTransactionPaths $targetRoot $id).LegacyBackup }
    $manifestContent = $manifest | ConvertTo-Json -Depth 5
    $operations.Add([pscustomobject]@{ Path = $script:InstallManifestName; OldHash = (Get-InstallHash (Join-Path $targetRoot $script:InstallManifestName)); NewHash = (Get-InstallTextHash $manifestContent); Content = $manifestContent })
    Invoke-InstallTransaction $targetRoot $operations.ToArray() 'Install' $legacy $id

    # Shell integration is optional and occurs only after a complete file commit.
    # Failure here leaves a usable, fully recorded program that can be re-registered.
    try {
        if (-not $NoShortcuts) {
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $targetExe
            $shortcut.WorkingDirectory = $targetRoot
            $shortcut.IconLocation = "$targetExe,0"
            $shortcut.Description = 'CurseTorrent - BitTorrent client'
            $shortcut.Save()
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
    } catch { Write-Warning ('Program files were installed successfully, but Windows shortcuts or registration could not be completed. Run the installer again to retry. ' + $_.Exception.Message) }
    Write-Host "Installed: $targetExe"
    Write-Host 'Downloads and application settings were preserved.'
    if ($RegisterHandlers) { Write-Host 'Handlers registered when Windows registration succeeded. Choose CurseTorrent in Windows Default apps to make it the default.' }
} finally {
    $installLock.ReleaseMutex()
    $installLock.Dispose()
}
