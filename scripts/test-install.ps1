param([Parameter(Mandatory=$true)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\')
. (Join-Path $packageRoot 'Installer.Common.ps1')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('CurseTorrent-install-test-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$script:checks = 0
function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Host ('PASS ' + $script:checks + ': ' + $Message)
}
function Install-Package([string]$Source, [string]$Target) { & (Join-Path $Source 'install.ps1') -InstallDirectory $Target -NoShortcuts -NoUninstallRegistration }
function Expect-Failure([scriptblock]$Action, [string]$MessagePart) {
    $errorMessage = $null
    try { & $Action } catch { $errorMessage = $_.Exception.Message }
    Require ($null -ne $errorMessage -and $errorMessage -like ('*' + $MessagePart + '*')) ('Expected failure: ' + $MessagePart)
}
function Write-Fixture([string]$Path, [string]$Value) { [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false)) }
function New-FixturePackage([string]$Name) {
    $path = Join-Path $testRoot $Name
    [IO.Directory]::CreateDirectory($path) | Out-Null
    foreach ($scriptName in @('install.ps1', 'uninstall.ps1', 'Installer.Common.ps1')) { Copy-Item -LiteralPath (Join-Path $packageRoot $scriptName) -Destination $path }
    Write-Fixture (Join-Path $path 'CurseTorrent.exe') 'Temporary installer fixture version one; never executed.'
    Write-Fixture (Join-Path $path 'zz-owned.txt') 'Original program fixture'
    return $path
}
function Require-NoStaging([string]$Target) {
    $parent = Split-Path -Parent $Target
    $prefix = '.ct-' + (Get-InstallTextHash $Target.ToUpperInvariant()).Substring(0, 8) + '-'
    $remaining = @(Get-ChildItem -LiteralPath $parent -Directory -Force | Where-Object { $_.Name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    Require ($remaining.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $Target '.cursetorrent-install-pending.json'))) 'No transaction journal or staging files remain'
}
try {
    # Exercise the real published file list first.
    $destination = Join-Path $testRoot 'Installed application'
    Install-Package $packageRoot $destination
    Require (Test-Path -LiteralPath (Join-Path $destination 'CurseTorrent.exe')) 'Real package installs its executable'
    $manifest = Get-Content -LiteralPath (Join-Path $destination '.cursetorrent-install.json') -Raw | ConvertFrom-Json
    Require ($manifest.Schema -eq 2 -and $null -ne $manifest.Hashes.'Installer.Common.ps1') 'Installed manifest fingerprints the packaged helper'
    $downloadDirectory = Join-Path $destination 'My downloads'
    [IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
    $download = Join-Path $downloadDirectory 'keep-me.txt'
    Write-Fixture $download 'User-owned download. Must survive uninstall.'
    Install-Package $packageRoot $destination
    Require ([IO.File]::ReadAllText($download) -eq 'User-owned download. Must survive uninstall.') 'Ordinary update preserves unknown downloads'
    & (Join-Path $destination 'uninstall.ps1')
    Require (-not (Test-Path -LiteralPath (Join-Path $destination 'CurseTorrent.exe'))) 'Ordinary uninstall removes the program'
    Require ([IO.File]::ReadAllText($download) -eq 'User-owned download. Must survive uninstall.') 'Ordinary uninstall preserves unknown downloads'
    Require-NoStaging $destination

    # Small fixtures keep failure and rollback checks independent of the runtime size.
    $source = New-FixturePackage 'failure package'
    $target = Join-Path $testRoot 'retry target'
    $lockedSource = [IO.File]::Open((Join-Path $source 'zz-owned.txt'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { Expect-Failure { Install-Package $source $target } 'being used by another process' } finally { $lockedSource.Dispose() }
    Require (-not (Test-Path -LiteralPath $target) -or @(Get-ChildItem -LiteralPath $target -Force).Count -eq 0) 'Failed first install leaves no partial program folder'
    Require-NoStaging $target
    Install-Package $source $target
    Require (Test-Path -LiteralPath (Join-Path $target '.cursetorrent-install.json')) 'Normal retry succeeds after the source lock is released'

    $exe = Join-Path $target 'CurseTorrent.exe'
    $owned = Join-Path $target 'zz-owned.txt'
    $originalExe = [IO.File]::ReadAllText($exe)
    $originalOwned = [IO.File]::ReadAllText($owned)
    $originalManifest = [IO.File]::ReadAllText((Join-Path $target '.cursetorrent-install.json'))
    Write-Fixture (Join-Path $target 'personal.txt') 'Unknown user file'
    Write-Fixture (Join-Path $source 'CurseTorrent.exe') 'Temporary installer fixture version two; never executed.'
    Write-Fixture (Join-Path $source 'zz-owned.txt') 'Updated program fixture'
    Write-Fixture (Join-Path $source 'a-new.txt') 'New program file'
    # Read sharing permits backup/hash reads but prevents replacement: this exercises
    # rollback after earlier replacements, not just the package-read preflight.
    $lockedTarget = [IO.File]::Open($owned, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Expect-Failure { Install-Package $source $target } 'being used by another process' } finally { $lockedTarget.Dispose() }
    Require ([IO.File]::ReadAllText($exe) -eq $originalExe -and [IO.File]::ReadAllText($owned) -eq $originalOwned) 'Failed update restores all previous program bytes'
    Require ([IO.File]::ReadAllText((Join-Path $target '.cursetorrent-install.json')) -eq $originalManifest) 'Failed update keeps the previous manifest'
    Require (-not (Test-Path -LiteralPath (Join-Path $target 'a-new.txt'))) 'Rollback removes only its newly introduced program file'
    Require ([IO.File]::ReadAllText((Join-Path $target 'personal.txt')) -eq 'Unknown user file') 'Rollback preserves the unknown file'
    Require-NoStaging $target
    Install-Package $source $target
    Require ([IO.File]::ReadAllText($exe) -like '*version two*') 'Update retry succeeds after the destination lock is released'

    Write-Fixture (Join-Path $source 'personal.txt') 'Package file with a conflicting name'
    Expect-Failure { Install-Package $source $target } 'user-owned file'
    Require ([IO.File]::ReadAllText((Join-Path $target 'personal.txt')) -eq 'Unknown user file') 'Update refuses to claim an unknown file with a matching package name'
    Remove-Item -LiteralPath (Join-Path $source 'personal.txt') -Force
    Write-Fixture $owned 'A user replaced this tracked file'
    Expect-Failure { Install-Package $source $target } 'changed since installation'
    Require ([IO.File]::ReadAllText($owned) -eq 'A user replaced this tracked file') 'Update refuses to overwrite a modified tracked file'

    $lockedExe = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Expect-Failure { & (Join-Path $target 'uninstall.ps1') } 'being used by another process' } finally { $lockedExe.Dispose() }
    Require ((Test-Path -LiteralPath $exe) -and (Test-Path -LiteralPath (Join-Path $target 'a-new.txt')) -and (Test-Path -LiteralPath (Join-Path $target '.cursetorrent-install.json'))) 'Failed uninstall restores program files and keeps its manifest'
    Require-NoStaging $target
    & (Join-Path $target 'uninstall.ps1')
    Require (-not (Test-Path -LiteralPath $exe)) 'Uninstall retry removes the program after the lock is released'
    Require ([IO.File]::ReadAllText($owned) -eq 'A user replaced this tracked file') 'Uninstall preserves modified tracked files'
    Require ([IO.File]::ReadAllText((Join-Path $target 'personal.txt')) -eq 'Unknown user file') 'Uninstall also preserves unknown files'
    Require-NoStaging $target

    $legacySource = New-FixturePackage 'legacy package'
    $legacyTarget = Join-Path $testRoot 'legacy target'
    Install-Package $legacySource $legacyTarget
    $legacyManifestPath = Join-Path $legacyTarget '.cursetorrent-install.json'
    $schemaOne = Get-Content -LiteralPath $legacyManifestPath -Raw | ConvertFrom-Json
    $schemaOne.Schema = 1
    $schemaOne.PSObject.Properties.Remove('Hashes')
    Write-Fixture $legacyManifestPath ($schemaOne | ConvertTo-Json -Depth 5)
    Write-Fixture (Join-Path $legacyTarget 'zz-owned.txt') 'Legacy file replaced by the user'
    Expect-Failure { & (Join-Path $legacyTarget 'uninstall.ps1') } 'no file fingerprints'
    Write-Fixture (Join-Path $legacySource 'CurseTorrent.exe') 'Temporary legacy upgrade version two; never executed.'
    Install-Package $legacySource $legacyTarget
    $upgraded = Get-Content -LiteralPath $legacyManifestPath -Raw | ConvertFrom-Json
    Require ($upgraded.Schema -eq 2) 'A schema 1 installation upgrades to a fingerprinted manifest'
    $legacyBackup = $upgraded.LegacyBackupDirectory
    Require ([IO.File]::ReadAllText((Join-Path $legacyBackup 'zz-owned.txt')) -eq 'Legacy file replaced by the user') 'Legacy upgrade retains the original bytes it cannot authenticate'
    & (Join-Path $legacyTarget 'uninstall.ps1')
    Require ([IO.File]::ReadAllText((Join-Path $legacyBackup 'zz-owned.txt')) -eq 'Legacy file replaced by the user') 'Uninstall does not remove the separate legacy backup'
    Require-NoStaging $legacyTarget

    $boundarySource = New-FixturePackage 'manifest package'
    $boundaryTarget = Join-Path $testRoot 'manifest target'
    Install-Package $boundarySource $boundaryTarget
    $manifestPath = Join-Path $boundaryTarget '.cursetorrent-install.json'
    $goodManifest = [IO.File]::ReadAllText($manifestPath)
    $invalid = $goodManifest | ConvertFrom-Json
    $invalid.Schema = 1
    $invalid.PSObject.Properties.Remove('Hashes')
    $invalid.Files += '..\outside-note.txt'
    $outside = Join-Path $testRoot 'outside-note.txt'
    Write-Fixture $outside 'Outside file must remain intact'
    Write-Fixture $manifestPath ($invalid | ConvertTo-Json -Depth 5)
    Expect-Failure { Install-Package $boundarySource $boundaryTarget } 'Invalid installation manifest path'
    Expect-Failure { & (Join-Path $boundaryTarget 'uninstall.ps1') } 'Invalid installation manifest path'
    Require ([IO.File]::ReadAllText($outside) -eq 'Outside file must remain intact') 'Invalid manifest paths never affect a neighboring file'
    Write-Fixture $manifestPath $goodManifest

    # Model interruption after one ordinary replacement. This uses only generated
    # fixture files and the documented recovery journal, never malformed torrent data.
    . (Join-Path $packageRoot 'Installer.Common.ps1')
    $id = [Guid]::NewGuid().ToString('N')
    $paths = Get-InstallTransactionPaths $boundaryTarget $id
    [IO.Directory]::CreateDirectory((Join-Path $paths.Directory 'old')) | Out-Null
    $boundaryExe = Join-Path $boundaryTarget 'CurseTorrent.exe'
    $oldHash = Get-InstallHash $boundaryExe
    Copy-Item -LiteralPath $boundaryExe -Destination (Join-Path $paths.Directory 'old/CurseTorrent.exe')
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $paths.Directory ('old/' + $script:InstallManifestName))
    Write-Fixture (Join-Path $paths.Directory 'owner.json') ([ordered]@{Id=$id; InstallDirectory=$boundaryTarget} | ConvertTo-Json)
    Write-Fixture $boundaryExe 'Partly installed fixture version'
    $journal = [ordered]@{Version=1; Id=$id; InstallDirectory=$boundaryTarget; Phase='Prepared'; Kind='Install'; KeepBackup=$false; Operations=@(
        [ordered]@{Path='CurseTorrent.exe'; OldHash=$oldHash; NewHash=(Get-InstallHash $boundaryExe)},
        [ordered]@{Path=$script:InstallManifestName; OldHash=(Get-InstallHash $manifestPath); NewHash=(Get-InstallHash $manifestPath)}
    )}
    Write-Fixture $paths.Journal ($journal | ConvertTo-Json -Depth 6)
    Install-Package $boundarySource $boundaryTarget
    Require ((Get-InstallHash $boundaryExe) -eq $oldHash) 'A normal install recovers an interrupted replacement before continuing'
    Require-NoStaging $boundaryTarget
    Write-Host ('PASS: ' + $script:checks + ' installer checks; package installation, rollback, retry, modified-file retention, legacy migration, manifest boundaries, interrupted-operation recovery.')
} finally {
    if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
}
