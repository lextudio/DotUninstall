Param(
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$Trim,
    [string]$Framework = 'net9.0-desktop',
    [switch]$NoClean,
    [switch]$Manifest, # opt-in: generate a manifest & checksums (package-all hashes anyway)
    [string]$WingetManifestsOut,
    [string]$PackageId = 'lextudio.DotUninstall',
    [string]$PackageName = 'DotUninstall',
    [string]$Publisher = 'lextudio',
    [string]$PortableCommand = 'dotuninstall',
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot "..\DotNetUninstall\DotNetUninstall.csproj"
if (-not (Test-Path $project)) { throw "Project file not found: $project" }

# Defensive: Sometimes Framework was observed resolving incorrectly to the configuration value (e.g. 'Release').
if ($Framework -and ($Framework -ieq $Configuration -or $Framework -notmatch '^net[0-9]')) {
    Write-Warning "Framework parameter value '$Framework' is invalid; defaulting to 'net9.0-desktop'."
    $Framework = 'net9.0-desktop'
}
Write-Host "Config=$Configuration Framework=$Framework" -ForegroundColor DarkGray

function Resolve-Version {
    param([string]$ProjectPath)
    $v = $null
    # 1. Try local tool (manifest)
    try {
        $line = & dotnet nbgv get-version -v SemVer2 2>$null
        if ($LASTEXITCODE -eq 0 -and $line) { $v = $line.Trim() }
    } catch { }
    # 2. Attempt install locally if still missing (idempotent)
    if (-not $v) {
        Write-Host '(version) ensuring local nbgv tool is installed' -ForegroundColor DarkGray
        try { & dotnet tool install nbgv --version 3.6.143 2>$null | Out-Null } catch { }
        try {
            $line = & dotnet nbgv get-version -v SemVer2 2>$null
            if ($LASTEXITCODE -eq 0 -and $line) { $v = $line.Trim() }
        } catch { }
    }
    # 3. Fallback: version.json
    if (-not $v) {
        $root = Resolve-Path (Join-Path (Split-Path $ProjectPath -Parent) '..') 2>$null
        if ($root) {
            $vj = Join-Path $root 'version.json'
            if (Test-Path $vj) {
                try { $json = Get-Content $vj -Raw | ConvertFrom-Json; if ($json.version) { $v = $json.version } } catch { }
            }
        }
    }
    # 4. Fallback: project ApplicationDisplayVersion
    if (-not $v) {
        $xml = Get-Content $ProjectPath -Raw
        if ($xml -match '<ApplicationDisplayVersion>(?<v>[^<]+)</ApplicationDisplayVersion>') { $v = $Matches['v'] }
    }
    if ($v -and $v -match '^[0-9]+\.[0-9]+$') { $v = "$v.0" }
    if (-not $v) { $v = '0.1.0' }
    return $v
}

$Version = Resolve-Version -ProjectPath $project
Write-Host "Using version: $Version" -ForegroundColor Cyan

$baseArtifacts = Join-Path $PSScriptRoot "..\artifacts"
$windowsStage  = Join-Path $baseArtifacts 'windows'
$releaseOut    = Join-Path $baseArtifacts 'release'
New-Item -ItemType Directory -Force -Path $windowsStage | Out-Null
New-Item -ItemType Directory -Force -Path $releaseOut | Out-Null

$ridList = @('win-x64','win-arm64')

# Base publish args. We deliberately do NOT set a custom -o so that the SDK creates the standard publish folder structure:
# bin/<Configuration>/<Framework>/<RID>/publish/
# NOTE: Uno's EmbeddedResourceInjector task expects to parse a PDB; using DebugType=portable keeps a portable PDB available.
$commonPublishArgs = @('publish', $project, '-c', $Configuration, '-f', $Framework, '-p:PublishSingleFile=true', '-p:DebugType=portable', '--nologo')

if ($SelfContained) {
    $commonPublishArgs += '-p:SelfContained=true'
    $commonPublishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $commonPublishArgs += '-p:IncludeAllContentForSelfExtract=true'
} else {
    $commonPublishArgs += '-p:SelfContained=false'
}

if ($Trim) {
    $commonPublishArgs += '-p:PublishTrimmed=true'
}

# Only used if -Manifest is passed
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$manifestEntries = @()

${artifactRecords} = @()
foreach ($rid in $ridList) {
    Write-Host "Publishing for $rid..." -ForegroundColor Cyan

    if (-not $NoClean) {
        Write-Host "Cleaning previous build output for $rid to avoid cross-RID symbol reuse..." -ForegroundColor DarkGray
        & dotnet clean $project -c $Configuration -f $Framework -r $rid --nologo 2>$null | Out-Null
    }

    # Execute publish. Add EnableWindowsTargeting when cross-compiling on non-Windows hosts.
    $publishArgs = $commonPublishArgs
    if ($rid -like 'win-*' -and $IsWindows -ne $true) { $publishArgs += '-p:EnableWindowsTargeting=true' }
    $publishOutput = & dotnet @publishArgs -r $rid @ExtraArgs 2>&1
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        Write-Warning "Publish failed for $rid (exit $publishExit); output follows:" 
        $publishOutput | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
        continue
    }

    # Determine standard publish directory
    $projDir = Split-Path -Parent $project
    $publishDir = Join-Path $projDir "bin/$Configuration/$Framework/$rid/publish"
    if (-not (Test-Path $publishDir)) {
        Write-Warning ("Publish folder not found for {0}: {1}" -f $rid, $publishDir)
        continue
    }

    # Ensure we have the single-file exe (should be exactly one .exe present)
    $exeList = @(Get-ChildItem -Path $publishDir -Filter '*.exe')
    if ($exeList.Length -eq 0) { Write-Warning "No .exe found in publish folder for $rid"; continue }
    if ($exeList.Length -gt 1) { Write-Warning "Multiple executables found; taking first: $($exeList[0].Name)" }
    $exe = $exeList[0]

    # Copy publish folder to staging (for inspection) and the exe to release with canonical name
    $stageDir = Join-Path $windowsStage $rid
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    Copy-Item $publishDir $stageDir -Recurse -Force

    $finalName = "${PackageName}-$rid-$Version.exe"
    $targetPath = Join-Path $releaseOut $finalName
    Copy-Item $exe.FullName $targetPath -Force

    $sizeBytes = (Get-Item $targetPath).Length
    $sizeMB = [Math]::Round($sizeBytes / 1MB,2)
    $fullHash = (Get-FileHash -Algorithm SHA256 $targetPath).Hash
    $shortHash = $fullHash.Substring(0,12)
    Write-Host ("Packaged {0} => {1} ({2} MB, sha256:{3}...)" -f $rid, $finalName, $sizeMB, $shortHash) -ForegroundColor Green

    $artifactRecords += [pscustomobject]@{ Rid=$rid; File=$finalName; Path=$targetPath; Sha256=$fullHash; SizeBytes=$sizeBytes; SizeMB=$sizeMB }

    if ($Manifest) {
        $sha256 = (Get-FileHash -Algorithm SHA256 $targetPath).Hash.Substring(0,16)
        $manifestEntries += [pscustomobject]@{ RID=$rid; File=$finalName; SizeMB=$sizeMB; SHA256=$sha256 }
    }
}
if ($Manifest -and $manifestEntries.Count -gt 0) {
    $manifestPath = Join-Path $releaseOut ("manifest-windows-" + $timestamp + '.txt')
    $manifestEntries | Format-Table | Out-String | Set-Content $manifestPath
    Write-Host "Manifest written: $manifestPath" -ForegroundColor Green
    $checksumsPath = Join-Path $releaseOut 'sha256sums.txt'
    $manifestEntries | ForEach-Object {
        $full = Join-Path $releaseOut $_.File
        $fullHash = (Get-FileHash -Algorithm SHA256 $full).Hash
        "$fullHash  $($_.File)" }
        | Out-File -FilePath $checksumsPath -Encoding ascii
    Write-Host "Checksums written: $checksumsPath" -ForegroundColor Green
}

if ($artifactRecords.Count -gt 0) {
    $json = [pscustomobject]@{
        PackageId = $PackageId
        PackageName = $PackageName
        Version = $Version
        Generated = (Get-Date).ToString('o')
        Artifacts = $artifactRecords
    } | ConvertTo-Json -Depth 4
    $jsonPath = Join-Path $releaseOut "artifacts-windows-$Version.json"
    $json | Set-Content $jsonPath -Encoding utf8
    Write-Host "Artifact metadata JSON: $jsonPath" -ForegroundColor DarkCyan
}

if ($WingetManifestsOut -and -not [string]::IsNullOrWhiteSpace($WingetManifestsOut)) {
    Write-Host "Generating winget manifests..." -ForegroundColor Cyan
    $outDir = Join-Path (Resolve-Path $WingetManifestsOut -ErrorAction SilentlyContinue | ForEach-Object { $_ } ) $Version
    if (-not $outDir) { $outDir = Join-Path $WingetManifestsOut $Version }
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    $repoUrl = 'https://github.com/lextudio/DotUninstall'
    $licenseUrl = "$repoUrl/blob/v$Version/LICENSE"
    $releaseNotesUrl = "$repoUrl/releases/tag/v$Version"
    $locale = 'en-US'

    @(
        "PackageIdentifier: $PackageId",
        "PackageVersion: $Version",
        "DefaultLocale: $locale",
        'Moniker: dotuninstall',
        'ManifestType: version',
        'ManifestVersion: 1.6.0'
    ) | Set-Content (Join-Path $outDir "$PackageId.yaml") -Encoding utf8

    @(
        "PackageIdentifier: $PackageId",
        "PackageVersion: $Version",
        "PackageLocale: $locale",
        "Publisher: $Publisher",
        "PublisherUrl: https://github.com/lextudio",
        "PublisherSupportUrl: $repoUrl/issues",
        "Author: $Publisher",
        "PackageName: $PackageName",
        "PackageUrl: $repoUrl",
        'License: MIT',
        "LicenseUrl: $licenseUrl",
        'ShortDescription: Cross-platform minimalist UI for the dotnet-core-uninstall tool.',
    "Description: A minimalist UI wrapper around Microsoft's dotnet-core-uninstall CLI to visually inspect and remove installed .NET SDKs and runtimes.",
        "ReleaseNotes: Release $Version.",
        "ReleaseNotesUrl: $releaseNotesUrl",
        'Tags:',
        '  - dotnet',
        '  - uninstall',
        '  - sdk',
        '  - runtime',
        '  - developer-tools',
        'ManifestType: defaultLocale',
        'ManifestVersion: 1.6.0'
    ) | Set-Content (Join-Path $outDir "$PackageId.locale.en-US.yaml") -Encoding utf8

    $installerLines = @(
        "PackageIdentifier: $PackageId",
        "PackageVersion: $Version",
        'InstallerType: portable',
        'Installers:'
    )
    foreach ($rec in $artifactRecords) {
        $arch = if ($rec.Rid -match 'arm64') { 'arm64' } elseif ($rec.Rid -match 'x64') { 'x64' } else { $rec.Rid }
        $assetUrl = "$repoUrl/releases/download/v$Version/$($rec.File)"
        $installerLines += "  - Architecture: $arch"
        $installerLines += "    InstallerUrl: $assetUrl"
        $installerLines += "    InstallerSha256: $($rec.Sha256)"
        $installerLines += "    Commands:"
        $installerLines += "      - $PortableCommand"
    }
    $installerLines += 'ManifestType: installer'
    $installerLines += 'ManifestVersion: 1.6.0'
    $installerLines | Set-Content (Join-Path $outDir "$PackageId.installer.yaml") -Encoding utf8
    Write-Host "Winget manifests written: $outDir" -ForegroundColor Green
}

Write-Host "Done." -ForegroundColor Green
