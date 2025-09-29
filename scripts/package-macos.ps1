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
  [switch]$Dmg,
  [string]$IconPath,
  [switch]$VerifyIcon
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }

$Project = Join-Path $PSScriptRoot '../DotNetUninstall/DotNetUninstall.csproj'
if (-not (Test-Path $Project)) { throw "Project file not found: $Project" }

# Resolve version using latest git tag matching v* (fallback to 0.1.0)
function Resolve-Version {
  $tag = ''
  try { $tag = git describe --tags --abbrev=0 2>$null } catch { }
  if ($tag) { $tag = $tag.Trim(); if ($tag -match '^v') { $tag = $tag.Substring(1) } }
  if (-not $tag) { $tag = '0.1.0' }
  return $tag
}

$Version = Resolve-Version
Step "Version resolved: $Version"

$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$Artifacts = Join-Path $RepoRoot 'artifacts'
$ReleaseOut = Join-Path $Artifacts 'release'
New-Item -ItemType Directory -Force -Path $ReleaseOut | Out-Null
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
  $app = Get-ChildItem $ridOut -Directory -Filter '*.app' -Recurse | Select-Object -First 1
  if (-not $app) { throw "No .app bundle found for $rid (check Uno macOS target configuration)." }
  Info "Found bundle: $($app.FullName)"
  $bi = [BundleInfo]::new(); $bi.Rid = $rid; $bi.Path = $app.FullName; $AppBundles += $bi

  if ($IconPath) {
    if (-not (Test-Path $IconPath)) { throw "Icon file not found: $IconPath" }
    $resourcesDir = Join-Path $app.FullName 'Contents/Resources'
    if (-not (Test-Path $resourcesDir)) { throw "Unexpected bundle layout, missing Resources: $resourcesDir" }
    $targetIcns = Join-Path $resourcesDir 'icon.icns'
    Step "Applying custom icon -> $targetIcns"
    Copy-Item $IconPath $targetIcns -Force
    $plistPath = Join-Path $app.FullName 'Contents/Info.plist'
    if (Test-Path $plistPath) {
      # Convert binary plist to XML (safe even if already XML)
      & plutil -convert xml1 $plistPath
      $plist = Get-Content $plistPath -Raw
      if ($plist -match '<key>CFBundleIconFile</key>\s*<string>') {
        $plist = [regex]::Replace($plist, '(<key>CFBundleIconFile</key>\s*<string>)([^<]+)(</string>)', '$1icon$3')
      } else {
        Step "Injecting CFBundleIconFile into $plistPath"
        $injection = "    <key>CFBundleIconFile</key>`n    <string>icon</string>" + [Environment]::NewLine
        $plist = $plist -replace '(?s)(</dict>\s*</plist>)', ($injection + '$1')
      }
      $plist | Set-Content $plistPath
      # Optionally convert back to binary for compactness
      & plutil -convert binary1 $plistPath
    } else { Warn "Info.plist not found in bundle: $plistPath" }

    if ($VerifyIcon) {
      $iconutil = Get-Command iconutil -ErrorAction SilentlyContinue
      if (-not $iconutil) { Warn 'iconutil not found; cannot verify icon visually.' }
      else {
        $verifyRoot = Join-Path $Artifacts 'icon-preview'
        New-Item -ItemType Directory -Force -Path $verifyRoot | Out-Null
        $ridVerify = Join-Path $verifyRoot $rid
        Remove-Item $ridVerify -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
        New-Item -ItemType Directory -Force -Path $ridVerify | Out-Null
        $iconsetPath = Join-Path $ridVerify 'extracted.iconset'
        Step "Extracting icon sizes for visual verification ($rid)"
        & iconutil -c iconset $targetIcns -o $iconsetPath | Out-Null
        # Flatten copies of PNGs for quick viewing
        Get-ChildItem $iconsetPath -Filter '*.png' | ForEach-Object {
          Copy-Item $_.FullName (Join-Path $ridVerify $_.Name)
        }
        # Write a small README with guidance
        @(
          'Icon verification output',
          "RID: $rid", 'Generated from: ' + $IconPath, 'Files:', ''
        ) + (Get-ChildItem $ridVerify -Filter '*.png' | Sort-Object Name | ForEach-Object { $_.Name }) | Set-Content (Join-Path $ridVerify 'README.txt')
        Info "Verification assets: $ridVerify (open the PNGs to inspect clarity)"
      }
    }
  }
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
  $dmgName = "DotUninstall-macos-$Version.dmg"
  $dmgPath = Join-Path $ReleaseOut $dmgName
  Step "Creating DMG: $dmgPath"
  & hdiutil create -volname 'DotUninstall' -srcfolder $Stage -ov -format UDZO $dmgPath | Out-Null
  Info "DMG created: $dmgPath"
  Warn 'Remember to codesign & notarize for distribution.'
}

Step 'Summary'
Write-Host "Version: $Version"
Write-Host "Bundles:"; $AppBundles | ForEach-Object { Write-Host " - $($_.Rid): $($_.Path)" }
if ($Dmg) { Write-Host "DMG: $dmgPath" }

Step 'Done'
