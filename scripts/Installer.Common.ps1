# Shared by install.ps1 and uninstall.ps1. Compatible with Windows PowerShell 5.1.
# All file changes are staged; a durable journal allows the next invocation to roll
# back an interrupted operation. A local manifest is bookkeeping, not a signature.
$script:InstallManifestName = '.cursetorrent-install.json'
$script:InstallJournalName = '.cursetorrent-install-pending.json'

function Get-InstallRoot([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not [IO.Path]::IsPathRooted($Path)) { throw 'Use an absolute installation path.' }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($full.Length -le [IO.Path]::GetPathRoot($full).Length) { throw 'A drive root cannot be an installation folder.' }
    return $full
}

function Get-InstallRelativePath([string]$Path, [switch]$AllowManifest) {
    if ([string]::IsNullOrWhiteSpace($Path) -or [IO.Path]::IsPathRooted($Path)) { throw 'Invalid installation manifest path.' }
    $relative = $Path.Replace('/', '\')
    foreach ($part in $relative.Split('\')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw 'Invalid installation manifest path.' }
        $stem = $part.Split('.')[0]
        if ($stem -match '^(?i:CON|PRN|AUX|NUL|COM[1-9\u00b9\u00b2\u00b3]|LPT[1-9\u00b9\u00b2\u00b3])$') { throw 'Reserved installation manifest path.' }
    }
    if ($relative.StartsWith('.cursetorrent-', [StringComparison]::OrdinalIgnoreCase) -and -not ($AllowManifest -and $relative -eq $script:InstallManifestName)) { throw 'The package or manifest contains an installer control path.' }
    return $relative
}

function Assert-InstallPath([string]$Root, [string]$Relative = '') {
    $current = $Root
    $parts = @('')
    if ($Relative) { $parts += $Relative.Split('\') }
    foreach ($part in $parts) {
        if ($part) { $current = Join-Path $current $part }
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Directory and file links are not supported in the installation: $current" }
        if ($null -ne $item -and $current -ne (Join-Path $Root $Relative) -and -not $item.PSIsContainer) { throw "A file blocks the installation directory: $current" }
    }
}

function Get-InstallHash([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "A directory occupies a program file path: $Path" }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
}

function Get-InstallTextHash([string]$Content) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($Content)))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

function Enter-InstallLock([string]$Root) {
    $name = 'Local\CurseTorrent.Install.' + (Get-InstallTextHash $Root.ToUpperInvariant())
    $mutex = [Threading.Mutex]::new($false, $name)
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'Another CurseTorrent installer or uninstaller is working with this folder.' }
        return $mutex
    } catch { $mutex.Dispose(); throw }
}

function Read-InstallJson([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 8MB) { throw 'Invalid installer metadata file.' }
    return ([IO.File]::ReadAllText($Path) | ConvertFrom-Json -ErrorAction Stop)
}

function Read-InstallManifest([string]$Root) {
    $path = Join-Path $Root $script:InstallManifestName
    Assert-InstallPath $Root $script:InstallManifestName
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $value = Read-InstallJson $path
    if ($value.Schema -notin @(1, 2) -or (Get-InstallRoot $value.InstallDirectory) -ne $Root) { throw 'The existing installation manifest does not match this folder.' }
    if ($value.Files -isnot [Array] -or $value.Files.Count -eq 0 -or $value.Files.Count -gt 20000 -or $value.Shortcuts -isnot [Array] -or $value.RegisteredUninstaller -isnot [bool]) { throw 'Invalid installation manifest fields.' }
    $files = @{}
    foreach ($entry in $value.Files) {
        if ($entry -isnot [string]) { throw 'Invalid installation manifest path.' }
        $relative = Get-InstallRelativePath $entry
        if ($files.ContainsKey($relative)) { throw 'Duplicate installation manifest path.' }
        Assert-InstallPath $Root $relative
        $fingerprint = $null
        if ($value.Schema -eq 2) {
            $property = $value.Hashes.PSObject.Properties[$relative]
            if ($null -eq $property -or $property.Value -isnot [string] -or $property.Value -notmatch '^[a-fA-F0-9]{64}$') { throw 'Missing or invalid file fingerprint in installation manifest.' }
            $fingerprint = $property.Value.ToLowerInvariant()
        }
        $files[$relative] = $fingerprint
    }
    $expectedShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'CurseTorrent.lnk'
    foreach ($shortcut in $value.Shortcuts) { if ($shortcut -isnot [string] -or $shortcut -ne $expectedShortcut) { throw 'Invalid shortcut in installation manifest.' } }
    return [pscustomobject]@{ Schema = $value.Schema; Files = $files; Shortcuts = @($value.Shortcuts); RegisteredUninstaller = $value.RegisteredUninstaller }
}

function Get-InstallPackageFiles([string]$Root) {
    Assert-InstallPath $Root
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Symbolic links and junctions are not supported in an installation package.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = Get-InstallRelativePath $item.FullName.Substring($Root.Length + 1)
            [pscustomobject]@{ Path = $relative; SourcePath = $item.FullName; Hash = (Get-InstallHash $item.FullName) }
        }
    }
}

function Get-InstallTransactionPaths([string]$Root, [string]$Id) {
    if ($Id -notmatch '^[a-f0-9]{32}$') { throw 'Invalid installation transaction identifier.' }
    $parent = Split-Path -Parent $Root
    $name = Split-Path -Leaf $Root
    $key = (Get-InstallTextHash $Root.ToUpperInvariant()).Substring(0, 8)
    return [pscustomobject]@{
        # A short sibling name avoids unnecessarily extending Windows PowerShell 5.1 paths.
        Directory = (Join-Path $parent ('.ct-' + $key + '-' + $Id))
        LegacyBackup = (Join-Path $parent ($name + '-before-upgrade-' + $Id))
        Journal = (Join-Path $Root $script:InstallJournalName)
    }
}

function Assert-InstallTransaction([string]$Root, $Journal) {
    if ($Journal.Version -ne 1 -or $Journal.InstallDirectory -ne $Root -or $Journal.Phase -notin @('Prepared', 'Committed') -or $Journal.Kind -notin @('Install', 'Uninstall') -or $Journal.KeepBackup -isnot [bool] -or $Journal.Operations -isnot [Array] -or $Journal.Operations.Count -eq 0 -or $Journal.Operations.Count -gt 20001) { throw 'Invalid installation recovery journal. No recovery files were changed.' }
    $paths = Get-InstallTransactionPaths $Root $Journal.Id
    Assert-InstallPath $paths.Directory
    $owner = Read-InstallJson (Join-Path $paths.Directory 'owner.json')
    if ($owner.Id -ne $Journal.Id -or $owner.InstallDirectory -ne $Root) { throw 'The installation recovery folder does not belong to this operation.' }
    $seen = @{}
    foreach ($op in $Journal.Operations) {
        $relative = Get-InstallRelativePath $op.Path -AllowManifest
        if ($seen.ContainsKey($relative)) { throw 'Duplicate path in installation recovery journal.' }
        $seen[$relative] = $true
        foreach ($hash in @($op.OldHash, $op.NewHash)) { if ($null -ne $hash -and ($hash -isnot [string] -or $hash -notmatch '^[a-f0-9]{64}$')) { throw 'Invalid fingerprint in installation recovery journal.' } }
        if ($null -eq $op.OldHash -and $null -eq $op.NewHash) { throw 'Empty operation in installation recovery journal.' }
        Assert-InstallPath $Root $relative
    }
    if ($Journal.Operations[-1].Path -ne $script:InstallManifestName) { throw 'The installation recovery journal is incomplete.' }
    return $paths
}

function Remove-InstallEmptyDirectories([string]$Root, $Operations) {
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($op in $Operations) {
        $parent = Split-Path -Parent (Join-Path $Root $op.Path)
        while ($parent -and $parent.Length -ge $Root.Length) {
            [void]$directories.Add($parent)
            if ($parent -eq $Root) { break }
            $parent = Split-Path -Parent $parent
        }
    }
    foreach ($directory in @($directories | Sort-Object Length -Descending)) {
        if ((Test-Path -LiteralPath $directory -PathType Container) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory) }
    }
}

function Remove-InstallTransactionDirectory([string]$Directory) {
    # Never follow a link while cleaning a private staging/backup folder.
    Assert-InstallPath $Directory
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'A link was found in the recovery folder; that folder was retained.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
        }
    }
    Remove-Item -LiteralPath $Directory -Recurse -Force
}

function Complete-InstallTransaction([string]$Root, $Journal) {
    $paths = Assert-InstallTransaction $Root $Journal
    if ($Journal.KeepBackup -and $Journal.Phase -eq 'Committed') {
        $backup = Join-Path $paths.Directory 'old'
        if (Test-Path -LiteralPath $backup) {
            if (Test-Path -LiteralPath $paths.LegacyBackup) { throw 'A legacy backup already occupies the recovery destination; the recovery folder was retained.' }
            [IO.Directory]::Move($backup, $paths.LegacyBackup)
        }
        Write-Host ('Files replaced during the legacy upgrade were preserved in: ' + $paths.LegacyBackup)
    }
    # Commit or rollback is now complete. Clear its journal before removing the
    # private staging directory; a failed cleanup must not block a valid installation.
    $journalPath = $paths.Journal
    Remove-Item -LiteralPath $journalPath -Force
    try { Remove-InstallTransactionDirectory $paths.Directory }
    catch { Write-Warning ('Installation recovery files were retained: ' + $paths.Directory + '. ' + $_.Exception.Message) }
    Remove-InstallEmptyDirectories $Root $Journal.Operations
}

function Undo-InstallTransaction([string]$Root, $Journal) {
    $paths = Assert-InstallTransaction $Root $Journal
    for ($index = $Journal.Operations.Count - 1; $index -ge 0; $index--) {
        $op = $Journal.Operations[$index]
        $target = Join-Path $Root $op.Path
        Assert-InstallPath $Root $op.Path
        $currentHash = Get-InstallHash $target
        if ($currentHash -eq $op.OldHash) { continue }
        if ($null -ne $currentHash -and $currentHash -ne $op.NewHash) { throw ('A file changed during recovery and was preserved: ' + $target + '. Backups remain in ' + $paths.Directory) }
        if ($null -eq $op.OldHash) {
            if ($null -ne $currentHash) { [IO.File]::Delete($target) }
        } else {
            $backup = Join-Path (Join-Path $paths.Directory 'old') $op.Path
            Assert-InstallPath (Join-Path $paths.Directory 'old') $op.Path
            if ((Get-InstallHash $backup) -ne $op.OldHash) { throw 'A recovery backup is missing or changed. Existing files and recovery data were retained.' }
            $restore = Join-Path $paths.Directory ('restore-' + [Guid]::NewGuid().ToString('N'))
            [IO.File]::Copy($backup, $restore, $false)
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            if ($null -ne $currentHash) { [IO.File]::Replace($restore, $target, [NullString]::Value) } else { [IO.File]::Move($restore, $target) }
        }
    }
    Complete-InstallTransaction $Root $Journal
}

function Repair-InstallTransaction([string]$Root) {
    $path = Join-Path $Root $script:InstallJournalName
    Assert-InstallPath $Root $script:InstallJournalName
    if (-not (Test-Path -LiteralPath $path)) { return }
    $journal = Read-InstallJson $path
    if ($journal.Phase -eq 'Committed') { Complete-InstallTransaction $Root $journal }
    else { Undo-InstallTransaction $Root $journal; Write-Host 'Recovered the previous installation after an interrupted operation.' }
}

function Invoke-InstallTransaction([string]$Root, [object[]]$Operations, [string]$Kind, [bool]$KeepBackup = $false, [string]$Id = [Guid]::NewGuid().ToString('N')) {
    $paths = Get-InstallTransactionPaths $Root $Id
    if (Test-Path -LiteralPath $paths.Directory) { throw 'The installation staging folder already exists.' }
    $journalCreated = $false
    $committed = $false
    $journal = $null
    [IO.Directory]::CreateDirectory($paths.Directory) | Out-Null
    try {
        $owner = [ordered]@{ Id = $Id; InstallDirectory = $Root }
        [IO.File]::WriteAllText((Join-Path $paths.Directory 'owner.json'), ($owner | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        $records = [Collections.Generic.List[object]]::new()
        foreach ($op in $Operations) {
            $relative = Get-InstallRelativePath $op.Path -AllowManifest
            Assert-InstallPath $Root $relative
            $target = Join-Path $Root $relative
            if ((Get-InstallHash $target) -ne $op.OldHash) { throw ('A file changed before installation: ' + $target) }
            if ($null -ne $op.NewHash) {
                $staged = Join-Path (Join-Path $paths.Directory 'new') $relative
                [IO.Directory]::CreateDirectory((Split-Path -Parent $staged)) | Out-Null
                if ($op.PSObject.Properties['Content']) { [IO.File]::WriteAllText($staged, $op.Content, [Text.UTF8Encoding]::new($false)) }
                else { [IO.File]::Copy($op.SourcePath, $staged, $false) }
                if ((Get-InstallHash $staged) -ne $op.NewHash) { throw 'A package file changed while it was being staged.' }
            }
            if ($null -ne $op.OldHash) {
                $backup = Join-Path (Join-Path $paths.Directory 'old') $relative
                [IO.Directory]::CreateDirectory((Split-Path -Parent $backup)) | Out-Null
                [IO.File]::Copy($target, $backup, $false)
                if ((Get-InstallHash $backup) -ne $op.OldHash) { throw 'An installed file changed while its recovery copy was being made.' }
            }
            $records.Add([pscustomobject]@{ Path = $relative; OldHash = $op.OldHash; NewHash = $op.NewHash })
        }
        $journal = [pscustomobject]@{ Version = 1; Id = $Id; InstallDirectory = $Root; Phase = 'Prepared'; Kind = $Kind; KeepBackup = $KeepBackup; Operations = $records.ToArray() }
        [void](Assert-InstallTransaction $Root $journal)
        $journalTemp = Join-Path $paths.Directory 'journal.tmp'
        [IO.File]::WriteAllText($journalTemp, ($journal | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
        [IO.Directory]::CreateDirectory($Root) | Out-Null
        [IO.File]::Move($journalTemp, $paths.Journal)
        $journalCreated = $true
        foreach ($op in $journal.Operations) {
            Assert-InstallPath $Root $op.Path
            $target = Join-Path $Root $op.Path
            if ((Get-InstallHash $target) -ne $op.OldHash) { throw ('A file changed during installation: ' + $target) }
            if ($null -eq $op.NewHash) { [IO.File]::Delete($target); continue }
            $staged = Join-Path (Join-Path $paths.Directory 'new') $op.Path
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            if ($null -ne $op.OldHash) { [IO.File]::Replace($staged, $target, [NullString]::Value) } else { [IO.File]::Move($staged, $target) }
        }
        $journal.Phase = 'Committed'
        [IO.File]::WriteAllText($journalTemp, ($journal | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
        [IO.File]::Replace($journalTemp, $paths.Journal, [NullString]::Value)
        $committed = $true
        try { Complete-InstallTransaction $Root $journal }
        catch { Write-Warning ('The file operation completed, but recovery cleanup needs a retry: ' + $_.Exception.Message) }
    } catch {
        $originalError = $_
        if ($journalCreated -and -not $committed) {
            try { $journal.Phase = 'Prepared'; Undo-InstallTransaction $Root $journal }
            catch { throw ('Installation failed and automatic recovery could not finish. Keep the recovery files in ' + $paths.Directory + '. ' + $_.Exception.Message) }
        } elseif (-not $journalCreated -and (Test-Path -LiteralPath $paths.Directory)) {
            Remove-InstallTransactionDirectory $paths.Directory
        }
        throw $originalError
    }
}
