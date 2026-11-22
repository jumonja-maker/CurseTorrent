param([Parameter(Mandatory=$true)][string]$RepositoryRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This audit check requires Windows.' }
$root = Join-Path ([IO.Path]::GetTempPath()) ('CurseTorrent-install-audit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Make-Package([string]$Name) {
    $directory = Join-Path $root $Name
    New-Item -ItemType Directory -Path $directory | Out-Null
    foreach ($file in @('install.ps1','uninstall.ps1')) { Copy-Item -LiteralPath (Join-Path $RepositoryRoot ('scripts/' + $file)) -Destination $directory }
    [IO.File]::WriteAllText((Join-Path $directory 'CurseTorrent.exe'), 'Temporary installer audit fixture version 1')
    return $directory
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Host ('PASS: ' + $Message) }
try {
    $package = Make-Package 'ordinary-package'
    $target = Join-Path $root 'ordinary-target'
    & (Join-Path $package 'install.ps1') -InstallDirectory $target -NoShortcuts -NoUninstallRegistration
    [IO.File]::WriteAllText((Join-Path $target 'user-notes.txt'), 'Preserve this temporary user fixture')
    [IO.File]::WriteAllText((Join-Path $package 'CurseTorrent.exe'), 'Temporary installer audit fixture version 2')
    & (Join-Path $package 'install.ps1') -InstallDirectory $target -NoShortcuts -NoUninstallRegistration
    Require (([IO.File]::ReadAllText((Join-Path $target 'CurseTorrent.exe'))) -like '*version 2') 'Ordinary update replaced the program fixture'
    Require (([IO.File]::ReadAllText((Join-Path $target 'user-notes.txt'))) -eq 'Preserve this temporary user fixture') 'Ordinary update preserved the unknown file'
    & (Join-Path $target 'uninstall.ps1')
    Require (([IO.File]::ReadAllText((Join-Path $target 'user-notes.txt'))) -eq 'Preserve this temporary user fixture') 'Uninstall preserved the unknown file'
    Require (-not (Test-Path -LiteralPath (Join-Path $target 'CurseTorrent.exe'))) 'Uninstall removed the program fixture'

    $failurePackage = Make-Package 'locked-package'
    $failureTarget = Join-Path $root 'partial-target'
    $lockedPath = Join-Path $failurePackage 'z-locked.bin'
    [IO.File]::WriteAllText($lockedPath, 'Temporary locked source file')
    $fileLock = [IO.File]::Open($lockedPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $initialError = $null
    try { & (Join-Path $failurePackage 'install.ps1') -InstallDirectory $failureTarget -NoShortcuts -NoUninstallRegistration }
    catch { $initialError = $_.Exception.Message }
    finally { $fileLock.Dispose() }
    Require ($null -ne $initialError) 'An ordinary file-lock error interrupted initial installation'
    $partialFiles = @(Get-ChildItem -LiteralPath $failureTarget -Recurse -File | ForEach-Object { $_.Name })
    $hasManifest = Test-Path -LiteralPath (Join-Path $failureTarget '.cursetorrent-install.json')
    $retryError = $null
    try { & (Join-Path $failurePackage 'install.ps1') -InstallDirectory $failureTarget -NoShortcuts -NoUninstallRegistration }
    catch { $retryError = $_.Exception.Message }
    [ordered]@{Scenario='Interrupted initial install and normal retry'; InitialError=$initialError; PartialFiles=$partialFiles; HasManifestAfterFailure=$hasManifest; RetryError=$retryError} | ConvertTo-Json -Depth 4 | Write-Host
    Require ($partialFiles.Count -gt 0 -and -not $hasManifest -and $retryError -like '*folder is not empty*') 'Observed recovery defect: a partial initial installation blocks a normal retry'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
