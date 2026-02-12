#!/usr/bin/env pwsh
<#!
.SYNOPSIS
  macOS packaging helper using Uno Platform's own publish-produced .app bundle.

.DESCRIPTION
  Relies on `dotnet publish -r osx-<arch>` which (for Uno Skia/macOS targets) produces a .app bundle.
  This script wraps that publish, locates the generated .app, and when both osx-arm64 + osx-x64 are
  requested it can merge native binaries into a single universal .app bundle. DMG creation then embeds
  the universal app by default.

.PARAMETER Rids
  Runtime identifiers to publish. Default: osx-arm64, osx-x64.

.PARAMETER Configuration
  Build configuration (Default: Release).

.PARAMETER Dmg
  Switch: also create a DMG containing the produced .app bundle.

.PARAMETER SkipUniversal
  Disable universal merge (keeps per-RID app bundles).

.EXAMPLE
  ./scripts/package-macos.ps1 -Dmg

.EXAMPLE
  ./scripts/package-macos.ps1 -Rids osx-arm64 -Configuration Release -Dmg

#>
param(
  [string[]]$Rids = @('osx-arm64','osx-x64'),
  [string]$Configuration = 'Release',
  [switch]$Dmg,
  [switch]$SkipUniversal,
  [string]$IconPath,
  [switch]$VerifyIcon,
  [string]$MacMinVersion = '12.0',      # e.g. 12.0 ; if provided, will rewrite Mach-O LC_BUILD_VERSION minos
  [string]$MacSdkVersion = '14.2'       # e.g. 14.2 ; optional, defaults to MacMinVersion if omitted
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }

function Test-IsMachO([string]$Path) {
  if (-not (Test-Path $Path)) { return $false }
  try {
    $desc = (& file -b $Path 2>$null)
    return ($desc -match 'Mach-O')
  } catch {
    return $false
  }
}

function Get-MachOArchs([string]$Path) {
  if (-not (Test-IsMachO $Path)) { return '' }
  try {
    return ((& lipo -archs $Path 2>$null) | Out-String).Trim()
  } catch {
    return ''
  }
}

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
  Step "Restoring runtime pack for $rid"
  dotnet restore $Project -r $rid | Out-Null
  dotnet publish $Project -c $Configuration -r $rid -p:PackageFormat=app -o $ridOut | Out-Null
  $app = Get-ChildItem $ridOut -Directory -Filter '*.app' -Recurse | Select-Object -First 1
  if (-not $app) { throw "No .app bundle found for $rid (check Uno macOS target configuration)." }
  Info "Found bundle: $($app.FullName)"
  $bi = [BundleInfo]::new(); $bi.Rid = $rid; $bi.Path = $app.FullName; $AppBundles += $bi

  # Optionally adjust Mach-O deployment target via vtool
  if ($MacMinVersion) {
    $exeName = 'DotNetUninstall'
    $exePath = Join-Path $app.FullName "Contents/MacOS/$exeName"
    if (-not (Test-Path $exePath)) { Warn "Executable not found for vtool edit: $exePath" }
    else {
      $vtool = Get-Command vtool -ErrorAction SilentlyContinue
      if (-not $vtool) { Warn 'vtool not found (Xcode command-line tools). Skipping minos rewrite.' }
      else {
        if (-not $MacSdkVersion) { $MacSdkVersion = $MacMinVersion }
        # Read current build info
        $current = & vtool -show-build $exePath 2>$null
        $currentMin = $null; $currentSdk = $null
        if ($current) {
          $m1 = [regex]::Match($current, 'minos\s+([0-9\.]+)')
          if ($m1.Success) { $currentMin = $m1.Groups[1].Value }
          $m2 = [regex]::Match($current, 'sdk\s+([0-9\.]+)')
          if ($m2.Success) { $currentSdk = $m2.Groups[1].Value }
        }
        if ($currentMin -and $currentMin -eq $MacMinVersion -and $currentSdk -and $currentSdk -eq $MacSdkVersion) {
          Info "Mach-O already has minos=$currentMin sdk=$currentSdk for $rid; skipping vtool rewrite."
        } else {
          Step "Setting Mach-O build version (minos=$MacMinVersion sdk=$MacSdkVersion) for $rid"
          & vtool -set-build-version macos $MacMinVersion $MacSdkVersion -replace -output $exePath $exePath
          $updated = & vtool -show-build $exePath 2>$null
          if ($updated) { Write-Host $updated }
          $updatedMin = $null; $updatedSdk = $null
          $u1 = [regex]::Match($updated, 'minos\s+([0-9\.]+)')
          if ($u1.Success) { $updatedMin = $u1.Groups[1].Value }
          $u2 = [regex]::Match($updated, 'sdk\s+([0-9\.]+)')
          if ($u2.Success) { $updatedSdk = $u2.Groups[1].Value }
          if ($updatedMin -ne $MacMinVersion -or $updatedSdk -ne $MacSdkVersion) {
            Warn "vtool rewrite verification failed (expected minos=$MacMinVersion sdk=$MacSdkVersion, saw minos=$updatedMin sdk=$updatedSdk)."
          } else {
            Info "vtool rewrite successful (minos=$updatedMin sdk=$updatedSdk)."
          }
        }
      }
    }
  }

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

$UniversalBundle = $null
if (-not $SkipUniversal -and $AppBundles.Count -ge 2) {
  $arm64Bundle = $AppBundles | Where-Object { $_.Rid -eq 'osx-arm64' } | Select-Object -First 1
  $x64Bundle = $AppBundles | Where-Object { $_.Rid -eq 'osx-x64' } | Select-Object -First 1

  if ($arm64Bundle -and $x64Bundle) {
    $lipo = Get-Command lipo -ErrorAction SilentlyContinue
    if (-not $lipo) {
      Warn 'lipo not found (Xcode command-line tools). Skipping universal app merge.'
    } else {
      Step 'Creating universal macOS app bundle'
      $universalOut = Join-Path $OutRoot 'osx-universal'
      Remove-Item $universalOut -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
      New-Item -ItemType Directory -Force -Path $universalOut | Out-Null

      $sourceApp = $arm64Bundle.Path
      $sourceX64App = $x64Bundle.Path
      $universalAppPath = Join-Path $universalOut (Split-Path $sourceApp -Leaf)
      Copy-Item $sourceApp $universalAppPath -Recurse

      $arm64MacOS = Join-Path $sourceApp 'Contents/MacOS'
      $x64MacOS = Join-Path $sourceX64App 'Contents/MacOS'
      $universalMacOS = Join-Path $universalAppPath 'Contents/MacOS'
      if (-not (Test-Path $arm64MacOS) -or -not (Test-Path $x64MacOS) -or -not (Test-Path $universalMacOS)) {
        throw 'Unexpected app bundle layout while creating universal app (missing Contents/MacOS).'
      }

      $arm64Names = @(Get-ChildItem $arm64MacOS -File | Select-Object -ExpandProperty Name)
      $x64Names = @(Get-ChildItem $x64MacOS -File | Select-Object -ExpandProperty Name)
      $allNames = @($arm64Names + $x64Names)
      $allNames = @($allNames | Sort-Object -Unique)

      foreach ($name in $allNames) {
        $arm64File = Join-Path $arm64MacOS $name
        $x64File = Join-Path $x64MacOS $name
        $destFile = Join-Path $universalMacOS $name

        if (-not (Test-Path $arm64File)) {
          Warn "File only present in x64 output, copying as-is: $name"
          Copy-Item $x64File $destFile -Force
          continue
        }
        if (-not (Test-Path $x64File)) {
          Warn "File only present in arm64 output, keeping arm64 variant: $name"
          continue
        }

        if (-not (Test-IsMachO $arm64File) -or -not (Test-IsMachO $x64File)) {
          continue
        }

        $armArchs = Get-MachOArchs $arm64File
        $x64Archs = Get-MachOArchs $x64File

        if (-not $armArchs -or -not $x64Archs) {
          Warn "Could not inspect architectures for $name; keeping arm64 variant."
          continue
        }

        if ($armArchs -eq $x64Archs) {
          Info "Skipping lipo for $name (already same archs: $armArchs)."
          Copy-Item $arm64File $destFile -Force
        } else {
          Step "Merging Mach-O binary with lipo: $name"
          & lipo -create $x64File $arm64File -output $destFile
          if ($LASTEXITCODE -ne 0) { throw "lipo failed for $name" }
        }

        & chmod +x $destFile
      }

      $exePath = Join-Path $universalMacOS 'DotNetUninstall'
      if (Test-Path $exePath) {
        $exeArchs = Get-MachOArchs $exePath
        if ($exeArchs) { Info "Universal executable architectures: $exeArchs" }
      }

      $UniversalBundle = [BundleInfo]::new()
      $UniversalBundle.Rid = 'osx-universal'
      $UniversalBundle.Path = $universalAppPath
      $AppBundles += $UniversalBundle
      Info "Universal app created: $universalAppPath"
    }
  } else {
    Info 'Skipping universal app merge (requires both osx-arm64 and osx-x64 outputs).'
  }
}

if ($Dmg) {
  $Stage = Join-Path $Artifacts 'macos-stage'
  Remove-Item $Stage -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
  New-Item -ItemType Directory -Force -Path $Stage | Out-Null
  $BundlesForDmg = @()
  if ($UniversalBundle) { $BundlesForDmg = @($UniversalBundle) } else { $BundlesForDmg = @($AppBundles) }
  $multi = ($BundlesForDmg.Count -gt 1)
  foreach ($b in $BundlesForDmg) {
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
