param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$OutputDirectory = '',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stageRoot = Join-Path $outputRoot ('stage-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $stageRoot 'CurseTorrent'
$appProject = Join-Path $repoRoot 'src/CurseTorrent.App/CurseTorrent.App.csproj'
$testProject = Join-Path $repoRoot 'tests/CurseTorrent.Tests/CurseTorrent.Tests.csproj'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
try {
    Push-Location $repoRoot
    try {
        if (-not $SkipTests) {
            & dotnet restore $testProject --locked-mode --disable-parallel -m:1
            if ($LASTEXITCODE -ne 0) { throw 'Test dependency restore failed.' }
            & dotnet build $testProject --configuration $Configuration --no-restore -m:1
            if ($LASTEXITCODE -ne 0) { throw 'Core test build failed.' }
            & dotnet run --project $testProject --configuration $Configuration --no-build --no-restore
            if ($LASTEXITCODE -ne 0) { throw 'Core verification failed.' }
        }
        & dotnet restore $appProject --runtime win-x64 --locked-mode --disable-parallel -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Windows dependency restore failed.' }
        & dotnet publish $appProject --configuration $Configuration --runtime win-x64 --self-contained true --no-restore -m:1 --output $packageRoot -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw 'Windows publication failed.' }
    } finally { Pop-Location }
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $packageRoot
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/QUICKSTART-RU.md') -Destination (Join-Path $packageRoot 'README-RU.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $packageRoot -Recurse
    # A self-contained release redistributes runtime packs. Preserve their exact notices.
    $deps = Get-Content -LiteralPath (Join-Path $packageRoot 'CurseTorrent.deps.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $repoRoot 'src/CurseTorrent.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    $packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
    $dotnetRoot = Split-Path -Parent (Get-Command dotnet).Source
    $runtimePackCount = 0
    foreach ($libraryName in @($deps.libraries.PSObject.Properties.Name)) {
        if ($libraryName -notmatch '^(?:runtimepack\.)?(Microsoft\.(?:NETCore|WindowsDesktop)\.App\.Runtime\.win-x64)/([^/]+)$') { continue }
        $packId = $Matches[1]
        $packVersion = $Matches[2]
        $packDirectory = $null
        foreach ($folder in $packageFolders) {
            $candidate = Join-Path (Join-Path $folder $packId.ToLowerInvariant()) $packVersion
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packDirectory = $candidate; break }
        }
        if (-not $packDirectory) {
            $candidate = Join-Path (Join-Path (Join-Path $dotnetRoot 'packs') $packId) $packVersion
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packDirectory = $candidate }
        }
        if (-not $packDirectory) { throw "Cannot find license source for $packId $packVersion." }
        $noticeFiles = @(Get-ChildItem -LiteralPath $packDirectory -File | Where-Object { $_.Name -match '(?i)^(license|third.?party.?notices)(\.(txt|md))?$' })
        if ($noticeFiles.Count -eq 0) { throw "No license notices found for $packId $packVersion." }
        $noticeTarget = Join-Path $packageRoot "licenses/runtime/$packId-$packVersion"
        New-Item -ItemType Directory -Path $noticeTarget -Force | Out-Null
        foreach ($noticeFile in $noticeFiles) { Copy-Item -LiteralPath $noticeFile.FullName -Destination $noticeTarget }
        $runtimePackCount++
    }
    if ($runtimePackCount -lt 2) { throw 'The package must include notices for both .NET and Windows Desktop runtime packs.' }
    foreach ($name in @('install.ps1', 'install.cmd', 'uninstall.ps1', 'uninstall.cmd', 'Installer.Common.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $packageRoot
    }
    $exe = Join-Path $packageRoot 'CurseTorrent.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'CurseTorrent.exe is missing from the published package.' }
    $zipPath = Join-Path $outputRoot 'CurseTorrent-win-x64.zip'
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal -Force
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $outputRoot 'SHA256SUMS.txt'), "$hash  CurseTorrent-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Portable package: $zipPath"
    Write-Host "SHA-256: $hash"
} finally {
    if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
}
