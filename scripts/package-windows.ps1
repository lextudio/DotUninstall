Param(
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$Trim,
    [string]$Framework = 'net9.0-desktop',
    [switch]$NoClean,
    [switch]$Manifest, # opt-in: generate a manifest & checksums (package-all hashes anyway)
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot "..\DotNetUninstall\DotNetUninstall.csproj"
if (-not (Test-Path $project)) { throw "Project file not found: $project" }

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

foreach ($rid in $ridList) {
    Write-Host "Publishing for $rid..." -ForegroundColor Cyan

    if (-not $NoClean) {
        Write-Host "Cleaning previous build output for $rid to avoid cross-RID symbol reuse..." -ForegroundColor DarkGray
        & dotnet clean $project -c $Configuration -f $Framework -r $rid --nologo 2>$null | Out-Null
    }

    # Execute publish. ExtraArgs lets callers extend publishing without modifying the script.
    & dotnet @commonPublishArgs -r $rid @ExtraArgs 2>&1
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        Write-Warning "Publish failed for $rid (exit $publishExit); skipping artifact collection."
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
    $exeList = Get-ChildItem -Path $publishDir -Filter '*.exe'
    if ($exeList.Count -eq 0) { Write-Warning "No .exe found in publish folder for $rid"; continue }
    if ($exeList.Count -gt 1) { Write-Warning "Multiple executables found; taking first: $($exeList[0].Name)" }
    $exe = $exeList[0]

    # Copy publish folder to staging (for inspection) and the exe to release with canonical name
    $stageDir = Join-Path $windowsStage $rid
    if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
    Copy-Item $publishDir $stageDir -Recurse -Force

    $finalName = "dotnet-uninstall-ui-$rid.exe"
    $targetPath = Join-Path $releaseOut $finalName
    Copy-Item $exe.FullName $targetPath -Force

    $sizeMB = [Math]::Round((Get-Item $targetPath).Length / 1MB,2)
    $shortHash = (Get-FileHash -Algorithm SHA256 $targetPath).Hash.Substring(0,12)
    Write-Host ("Packaged {0} => {1} ({2} MB, sha256:{3}...)" -f $rid, $finalName, $sizeMB, $shortHash) -ForegroundColor Green

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

Write-Host "Done." -ForegroundColor Green
