$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Windows is required.' }
# Load all helpers before the transaction removes its own scripts from the install.
. (Join-Path $PSScriptRoot 'Installer.Common.ps1')
$targetRoot = Get-InstallRoot $PSScriptRoot
$targetExe = Join-Path $targetRoot 'CurseTorrent.exe'
$installLock = Enter-InstallLock $targetRoot
try {
    foreach ($process in @(Get-Process -Name CurseTorrent -ErrorAction SilentlyContinue)) {
        if ($process.Path -and [IO.Path]::GetFullPath($process.Path) -eq $targetExe) { throw 'Exit CurseTorrent from its tray menu before uninstalling.' }
    }
    Repair-InstallTransaction $targetRoot
    $manifest = Read-InstallManifest $targetRoot
    if ($null -eq $manifest) { throw 'No installation manifest was found. For a portable copy, move any downloads elsewhere before deleting its program folder.' }
    if ($manifest.Schema -eq 1) { throw 'This older installation has no file fingerprints. Run the current release installer to upgrade it first. No files were removed.' }
    $operations = [Collections.Generic.List[object]]::new()
    $preserved = [Collections.Generic.List[string]]::new()
    foreach ($relative in @($manifest.Files.Keys | Sort-Object)) {
        Assert-InstallPath $targetRoot $relative
        $current = Get-InstallHash (Join-Path $targetRoot $relative)
        if ($null -eq $current) { continue }
        if ($current -eq $manifest.Files[$relative]) { $operations.Add([pscustomobject]@{ Path = $relative; OldHash = $current; NewHash = $null }) }
        else { $preserved.Add($relative) }
    }
    $operations.Add([pscustomobject]@{ Path = $script:InstallManifestName; OldHash = (Get-InstallHash (Join-Path $targetRoot $script:InstallManifestName)); NewHash = $null })
    Invoke-InstallTransaction $targetRoot $operations.ToArray() 'Uninstall'
    # Registry and shortcuts are touched only after the file transaction succeeded.
    try {
        $ownsTorrentHandler = $false
        foreach ($id in @('CurseTorrent.Torrent', 'CurseTorrent.Magnet')) {
            $key = "HKCU:\Software\Classes\$id"
            if (Test-Path -LiteralPath "$key\shell\open\command") {
                $command = (Get-Item -LiteralPath "$key\shell\open\command").GetValue('')
                if ($command -eq ('"' + $targetExe + '" "%1"')) {
                    Remove-Item -LiteralPath $key -Recurse -Force
                    if ($id -eq 'CurseTorrent.Torrent') { $ownsTorrentHandler = $true }
                }
            }
        }
        if ($ownsTorrentHandler) {
            Remove-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.torrent\OpenWithProgids' -Name 'CurseTorrent.Torrent' -ErrorAction SilentlyContinue
        }
        $capabilitiesKey = 'HKCU:\Software\CurseTorrent\Capabilities'
        if ((Test-Path -LiteralPath $capabilitiesKey) -and (Get-Item -LiteralPath $capabilitiesKey).GetValue('ApplicationIcon') -eq ('"' + $targetExe + '",0')) {
            Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name CurseTorrent -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $capabilitiesKey -Recurse -Force -ErrorAction SilentlyContinue
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
    } catch { Write-Warning ('Program files were removed, but Windows registration cleanup was incomplete. ' + $_.Exception.Message) }
    foreach ($relative in $preserved) { Write-Host ('Preserved a file changed since installation: ' + $relative) }
    Write-Host 'CurseTorrent was uninstalled. Downloads, changed or unknown files, saved application data, and legacy upgrade backups were preserved.'
} finally {
    $installLock.ReleaseMutex()
    $installLock.Dispose()
}
