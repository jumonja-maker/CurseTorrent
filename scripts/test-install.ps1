param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CurseTorrent-install-test-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $testRoot 'Installed application'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
try {
    & (Join-Path $packageRoot 'install.ps1') -InstallDirectory $destination -NoShortcuts -NoUninstallRegistration
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'CurseTorrent.exe'))) { throw 'Installer did not install the executable.' }
    $downloadDirectory = Join-Path $destination 'My downloads'
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $download = Join-Path $downloadDirectory 'keep-me.txt'
    [IO.File]::WriteAllText($download, 'User-owned download. Must survive uninstall.')
    & (Join-Path $packageRoot 'install.ps1') -InstallDirectory $destination -NoShortcuts -NoUninstallRegistration
    if ([IO.File]::ReadAllText($download) -ne 'User-owned download. Must survive uninstall.') { throw 'Updating the installation modified a user-owned download.' }
    & (Join-Path $destination 'uninstall.ps1')
    if (Test-Path -LiteralPath (Join-Path $destination 'CurseTorrent.exe')) { throw 'Uninstaller left the executable behind.' }
    if (-not (Test-Path -LiteralPath $download)) { throw 'Uninstaller removed a user-owned download.' }
    if ([IO.File]::ReadAllText($download) -ne 'User-owned download. Must survive uninstall.') { throw 'Uninstaller modified a user-owned download.' }
    Write-Host 'PASS: installation and manifest-only uninstall preserve downloaded files.'
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
