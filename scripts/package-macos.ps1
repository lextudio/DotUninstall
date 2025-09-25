#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  macOS packaging helper using Uno Platform's own publish-produced .app bundle.

.DESCRIPTION
  Relies on `dotnet publish -r osx-<arch>` which (for Uno Skia/macOS targets) produces a .app bundle.
  This script wraps that publish, locates the generated .app, duplicates for each requested RID, and
  bundles them (optionally) into a DMG without manually crafting Info.plist.

.PARAMETER Rids
  Runtime identifiers to publish. Default: osx-arm64, osx-x64.

.PARAMETER Configuration
  Build configuration (Default: Release).

.PARAMETER Version
  Overrides detected display version (from ApplicationDisplayVersion).

.PARAMETER Dmg
  Switch: also create a DMG containing the produced .app bundles.

.EXAMPLE
  ./scripts/package-macos.ps1 -Dmg

.EXAMPLE
  ./scripts/package-macos.ps1 -Rids osx-arm64 -Configuration Release -Dmg

#>
param(
  [string[]]$Rids = @('osx-arm64','osx-x64'),
  [string]$Configuration = 'Release',
  [string]$Version,
  [switch]$Dmg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }

$Project = 'DotNetUninstall/DotNetUninstall.csproj'
if (-not (Test-Path $Project)) { throw "Project file not found: $Project" }

if (-not $Version) {
  $xml = Get-Content $Project -Raw
  $Version = if ($xml -match '<ApplicationDisplayVersion>(?<v>[^<]+)') { $Matches['v'] } else { '0.0.0' }
}

$Artifacts = Join-Path (Get-Location) 'artifacts'
$OutRoot   = Join-Path $Artifacts 'publish'
Remove-Item $OutRoot -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

class BundleInfo {
  [string]$Rid
  [string]$Path
}

$AppBundles = @()

foreach ($rid in $Rids) {
  Step "Publishing $rid"
  $ridOut = Join-Path $OutRoot $rid
  dotnet publish $Project -c $Configuration -r $rid -p:PackageFormat=app -o $ridOut | Out-Null
  # Search for .app produced by Uno (common under publish root)
  $app = Get-ChildItem $ridOut -Directory -Filter '*.app' -Recurse | Select-Object -First 1
  if (-not $app) { throw "No .app bundle found for $rid (check Uno macOS target configuration)." }
  Info "Found bundle: $($app.FullName)"
  $bi = [BundleInfo]::new()
  $bi.Rid = $rid
  $bi.Path = $app.FullName
  $AppBundles += $bi
}

if ($Dmg) {
  $Stage = Join-Path $Artifacts 'macos-stage'
  Remove-Item $Stage -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
  New-Item -ItemType Directory -Force -Path $Stage | Out-Null
  $multi = ($AppBundles.Count -gt 1)
  foreach ($b in $AppBundles) {
    $leaf = Split-Path $b.Path -Leaf
    $dest = if ($multi) {
      $nameNoExt = [System.IO.Path]::GetFileNameWithoutExtension($leaf)
      $arch = if ($b.Rid -match 'arm64') { 'arm64' } elseif ($b.Rid -match 'x64') { 'x64' } else { $b.Rid }
      "$nameNoExt ($arch).app"
    } else { $leaf }
    Copy-Item $b.Path (Join-Path $Stage $dest) -Recurse
  }
  if (Test-Path LICENSE) { Copy-Item LICENSE $Stage }
  $dmgName = "DotNetUninstallToolUI-macOS-$Version.dmg"
  $dmgPath = Join-Path $Artifacts $dmgName
  Step "Creating DMG: $dmgPath"
  & hdiutil create -volname 'DotNetUninstallToolUI' -srcfolder $Stage -ov -format UDZO $dmgPath | Out-Null
  Info "DMG created: $dmgPath"
  Warn 'Remember to codesign & notarize for distribution.'
}

Step 'Summary'
Write-Host "Version: $Version"
Write-Host "Bundles:"; $AppBundles | ForEach-Object { Write-Host " - $($_.Rid): $($_.Path)" }
if ($Dmg) { Write-Host "DMG: $dmgPath" }

Step 'Done'
