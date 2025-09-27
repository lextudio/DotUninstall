#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Prepare a winget manifest submission using a sparse, shallow clone (minimal download).

.DESCRIPTION
  Clones only the required manifest path in the winget-pkgs repository (letter bucket + publisher + package id),
  copies the already-generated YAML manifests from artifacts/winget/<version>/, commits them on a new branch, and
  prints the next steps for pushing and opening a PR.

  This avoids cloning the entire multi‑GB winget-pkgs repository history.

.PARAMETER Version
  Explicit package version. If omitted, resolves from the single directory under artifacts/winget (or NBGV).

.PARAMETER PackageId
  Winget PackageIdentifier (default: lextudio.DotUninstall).

.PARAMETER WingetRepo
  Git URL of the winget-pkgs fork to clone sparsely (default: https://github.com/microsoft/winget-pkgs.git).
  For contribution you normally fork first (e.g. https://github.com/lextudio/winget-pkgs.git) and supply that URL here.

.PARAMETER TempDir
  Temporary working directory (default: artifacts/winget-work).

.PARAMETER NoCommit
  If set, prepares the files but skips the git commit (for inspection).

.EXAMPLE
  ./scripts/winget-sparse-submit.ps1 -WingetRepo https://github.com/lextudio/winget-pkgs.git

.EXAMPLE
  ./scripts/winget-sparse-submit.ps1 -Version 1.0.1 -PackageId lextudio.DotUninstall -NoCommit

.NOTES
  After running (without -NoCommit) you can:
    cd <TempDir>/winget-pkgs
    git push origin <created-branch>
  Then open a PR against microsoft/winget-pkgs.
#>
param(
  [string]$Version,
  [string]$PackageId = 'lextudio.DotUninstall',
  [string]$WingetRepo = 'https://github.com/lextudio/winget-pkgs.git',
  [string]$TempDir = 'artifacts/winget-work',
  [switch]$NoCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Die($m){ throw $m }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $repoRoot 'artifacts'
$wingetArtifactsRoot = Join-Path $artifacts 'winget'
if (-not (Test-Path $wingetArtifactsRoot)) { Die "No artifacts/winget directory found. Run packaging with -WingetManifestsOut first." }

if (-not $Version) {
  # Collect directories (force array) then pick newest (descending lexical which matches SemVer ordering if zero-padded/standard).
  $dirs = @(Get-ChildItem -Path $wingetArtifactsRoot -Directory)
  if ($dirs.Count -eq 0) { Die "No version directories under artifacts/winget." }
  $latest = $dirs | Sort-Object Name -Descending | Select-Object -First 1
  $Version = $latest.Name
}

$sourceDir = Join-Path $wingetArtifactsRoot $Version
if (-not (Test-Path $sourceDir)) { Die "Expected manifest directory not found: $sourceDir" }

# Basic validation of required files
$required = @(
  "$PackageId.yaml",
  "$PackageId.locale.en-US.yaml",
  "$PackageId.installer.yaml"
)
foreach ($r in $required) { if (-not (Test-Path (Join-Path $sourceDir $r))) { Die "Missing required manifest file: $r in $sourceDir" } }

Step "Preparing sparse clone workspace"
$tempFull = Join-Path $repoRoot $TempDir
if (-not (Test-Path $tempFull)) { New-Item -ItemType Directory -Force -Path $tempFull | Out-Null }
$absTemp = Resolve-Path $tempFull
$workRoot = $absTemp.ProviderPath
$cloneDir = Join-Path $workRoot 'winget-pkgs'
if (Test-Path $cloneDir) {
  Warn "Existing clone directory found; removing for a clean run."; Remove-Item $cloneDir -Recurse -Force
}

Step "Sparse shallow clone ($WingetRepo)"
# Use blob-less partial clone & sparse checkout of just the package path bucket
$bucketLetter = ($PackageId.Split('.')[0][0]).ToString().ToLowerInvariant()
$packagePath = "manifests/$bucketLetter/" + $PackageId.Split('.')[0].ToLower() + "/" + $PackageId.Split('.')[1] + "/$Version"

# We clone just enough to add new version path (need parent chain up to package root)
$parentPath = [System.IO.Path]::GetDirectoryName($packagePath)

& git clone --depth 1 --filter=blob:none --sparse $WingetRepo $cloneDir | Out-Null
Push-Location $cloneDir
try {
  & git sparse-checkout set $parentPath | Out-Null
  # Ensure bucket path exists
  New-Item -ItemType Directory -Force -Path $packagePath | Out-Null
  Step "Copying manifests into sparse tree: $packagePath"
  Copy-Item (Join-Path $sourceDir '*') $packagePath -Force

  if (-not $NoCommit) {
    & git add $packagePath
    # Sanitize PackageId for branch name (replace dots with dashes)
    $sanitizedId = $PackageId -replace '\.','-'
    $branch = "add-$sanitizedId-$Version"
    & git checkout -b $branch | Out-Null
    & git commit -m "Add $PackageId version $Version" | Out-Null
    & git push --set-upstream origin $branch | Out-Null
    Step "Commit created on branch: $branch and pushed to origin"
    Write-Host "Next: open PR comparing to microsoft/winget-pkgs:master" -ForegroundColor Green
  } else {
    Warn 'Skipping commit due to -NoCommit. Inspect files manually.'
  }
}
finally {
  Pop-Location
}

Step 'Done'
Write-Host "Sparse clone location: $cloneDir" -ForegroundColor DarkGray
Write-Host "Manifests staged from: $sourceDir" -ForegroundColor DarkGray
