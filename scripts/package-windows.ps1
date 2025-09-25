Param(
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$Trim
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

$commonPublishArgs = @('publish', $project, '-c', $Configuration, '-p:PublishSingleFile=true', '-p:DebugType=none', '--nologo')

if ($SelfContained) {
    $commonPublishArgs += '-p:SelfContained=true'
    $commonPublishArgs += '-p:IncludeNativeLibrariesForSelfExtract=true'
    $commonPublishArgs += '-p:EnableCompressionInSingleFile=true'
} else {
    $commonPublishArgs += '-p:SelfContained=false'
}

if ($Trim) {
    $commonPublishArgs += '-p:PublishTrimmed=true'
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$manifest = @()

foreach ($rid in $ridList) {
    Write-Host "Publishing for $rid..." -ForegroundColor Cyan
    $outDir = Join-Path $windowsStage $rid
    dotnet @commonPublishArgs -r $rid -o $outDir | Write-Host

    $exe = Get-ChildItem -Path $outDir -Filter '*.exe' | Select-Object -First 1
    if (-not $exe) { Write-Warning "No executable produced for $rid"; continue }

    $finalName = "dotnet-uninstall-ui-$rid" + ($SelfContained ? '-sc' : '') + ($Trim ? '-trim' : '') + '.exe'
    $targetPath = Join-Path $releaseOut $finalName
    Copy-Item $exe.FullName $targetPath -Force

    $sizeMB = [Math]::Round((Get-Item $targetPath).Length / 1MB,2)
    $manifest += [pscustomobject]@{ RID=$rid; File=$finalName; SizeMB=$sizeMB }
}

$manifestPath = Join-Path $releaseOut ("manifest-windows-" + $timestamp + '.txt')
$manifest | Format-Table | Out-String | Set-Content $manifestPath
Write-Host "Manifest written: $manifestPath" -ForegroundColor Green
Write-Host "Done." -ForegroundColor Green
