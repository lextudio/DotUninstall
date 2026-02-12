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

.PARAMETER DmgBackgroundPath
  Optional background image for DMG Finder window layout. If omitted and social-preview.png exists at repo root,
  it is used automatically.

.PARAMETER DmgBackgroundMaxWidth
  Maximum background/window width used for DMG layout (default: 1100).

.PARAMETER DmgBackgroundMaxHeight
  Maximum background/window height used for DMG layout (default: 700).

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
  [string]$DmgBackgroundPath,
  [int]$DmgBackgroundMaxWidth = 1100,
  [int]$DmgBackgroundMaxHeight = 700,
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

function Get-ImageDimensions([string]$Path) {
  try {
    $sips = Get-Command sips -ErrorAction SilentlyContinue
    if (-not $sips) { return $null }
    $out = & sips -g pixelWidth -g pixelHeight $Path 2>$null
    if (-not $out) { return $null }
    $w = $null
    $h = $null
    foreach ($line in $out) {
      $mW = [regex]::Match($line, 'pixelWidth:\s*([0-9]+)')
      if ($mW.Success) { $w = [int]$mW.Groups[1].Value }
      $mH = [regex]::Match($line, 'pixelHeight:\s*([0-9]+)')
      if ($mH.Success) { $h = [int]$mH.Groups[1].Value }
    }
    if ($w -and $h) { return @{ Width = $w; Height = $h } }
    return $null
  } catch {
    return $null
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

if (-not $IconPath) {
  $defaultIconPath = Join-Path $RepoRoot 'DotNetUninstall/Assets/Images/AppIcon.icns'
  if (Test-Path $defaultIconPath) {
    $IconPath = $defaultIconPath
    Info "Using default macOS icon: $IconPath"
  }
}

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
  $applicationsShortcut = Join-Path $Stage 'Applications'
  if (Test-Path $applicationsShortcut) {
    Remove-Item $applicationsShortcut -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
  }
  # Standard drag-and-drop install hint in DMG: app bundle + Applications shortcut.
  & ln -s /Applications $applicationsShortcut
  if ($LASTEXITCODE -ne 0) { throw 'Failed to create Applications shortcut in DMG stage.' }

  $resolvedBackground = $null
  if ($DmgBackgroundPath) {
    if (-not (Test-Path $DmgBackgroundPath)) { throw "DMG background file not found: $DmgBackgroundPath" }
    $resolvedBackground = Resolve-Path $DmgBackgroundPath
  } else {
    $defaultDmgBackground = Join-Path $RepoRoot 'social-preview.png'
    if (Test-Path $defaultDmgBackground) {
      $resolvedBackground = Resolve-Path $defaultDmgBackground
    } else {
      $legacyBackground = Join-Path $RepoRoot 'DotUninstall.png'
      if (Test-Path $legacyBackground) {
        Warn 'social-preview.png not found; falling back to DotUninstall.png for DMG background.'
        $resolvedBackground = Resolve-Path $legacyBackground
      }
    }
  }

  $hasBackground = $false
  $layoutWidth = 980
  $layoutHeight = 640
  $appIconX = 700
  $appsIconX = 860
  $iconsY = 500
  if ($resolvedBackground) {
    $backgroundDir = Join-Path $Stage '.background'
    New-Item -ItemType Directory -Force -Path $backgroundDir | Out-Null
    $dims = Get-ImageDimensions $resolvedBackground
    if ($dims) {
      # Keep aspect ratio while constraining to a practical Finder window size.
      $scale = [Math]::Min([double]$DmgBackgroundMaxWidth / $dims.Width, [double]$DmgBackgroundMaxHeight / $dims.Height)
      if ($scale -gt 1.0) { $scale = 1.0 }
      $layoutWidth = [Math]::Max(760, [int][Math]::Round($dims.Width * $scale))
      $layoutHeight = [Math]::Max(500, [int][Math]::Round($dims.Height * $scale))
    }

    $stageBackground = Join-Path $backgroundDir 'background.png'
    # Scale background to exactly match the Finder window to avoid cropped composition.
    & sips -z $layoutHeight $layoutWidth $resolvedBackground --out $stageBackground | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $stageBackground)) {
      Warn 'Failed to scale DMG background to window size; copying original image.'
      Copy-Item $resolvedBackground $stageBackground -Force
      $dims = Get-ImageDimensions $stageBackground
      if ($dims) {
        $layoutWidth = $dims.Width
        $layoutHeight = $dims.Height
      }
    }
    $hasBackground = $true

    # Place icons in lower-right whitespace while keeping generous insets to avoid scrollbars.
    $appIconX = [int][Math]::Round($layoutWidth * 0.70)
    $appsIconX = [int][Math]::Round($layoutWidth * 0.84)
    $iconsY = [int][Math]::Round($layoutHeight * 0.70)

    $rightInset = 170
    $bottomInset = 170
    if ($appsIconX -gt ($layoutWidth - $rightInset)) { $appsIconX = $layoutWidth - $rightInset }
    if ($appIconX -gt ($appsIconX - 180)) { $appIconX = $appsIconX - 180 }
    if ($iconsY -gt ($layoutHeight - $bottomInset)) { $iconsY = $layoutHeight - $bottomInset }

    Info "DMG background image applied: $resolvedBackground (scaled to ${layoutWidth}x${layoutHeight})"
  }

  if (Test-Path LICENSE) { Copy-Item LICENSE $Stage }
  $dmgName = "DotUninstall-macos-$Version.dmg"
  $dmgPath = Join-Path $ReleaseOut $dmgName
  $rwDmgPath = Join-Path $ReleaseOut "DotUninstall-macos-$Version-rw.dmg"
  Remove-Item $rwDmgPath -Force -ErrorAction SilentlyContinue | Out-Null
  Remove-Item $dmgPath -Force -ErrorAction SilentlyContinue | Out-Null

  Step "Creating writable DMG: $rwDmgPath"
  & hdiutil create -volname 'DotUninstall' -srcfolder $Stage -ov -format UDRW $rwDmgPath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Failed to create writable DMG: $rwDmgPath" }

  $deviceNode = $null
  $mountPoint = $null
  try {
    Step 'Applying Finder window layout for DMG'
    $attachOutput = & hdiutil attach -readwrite -noverify -noautoopen $rwDmgPath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to mount writable DMG for styling.' }

    foreach ($line in $attachOutput) {
      if ($line -match '^/dev/') {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 1) { $deviceNode = $parts[0] }
      }
      if ($line -match '/Volumes/') {
        $parts = $line -split '\s+'
        if ($parts.Count -ge 1) { $mountPoint = $parts[$parts.Count - 1] }
      }
    }
    if (-not $deviceNode) { throw 'Could not determine mounted DMG device node.' }
    if (-not $mountPoint) { throw 'Could not determine mounted DMG mount point.' }

    $finderDiskName = 'DotUninstall'
    $appInStage = Get-ChildItem $Stage -Directory -Filter '*.app' | Select-Object -First 1
    $appBundleName = if ($appInStage) { $appInStage.Name } else { 'DotUninstall.app' }

    $bgLine = ''
    if ($hasBackground) { $bgLine = 'set background picture of viewOptions to file ".background:background.png"' }

    $scriptLines = @(
      "tell application `"Finder`"",
      "  tell disk `"$finderDiskName`"",
      "    open",
      "    tell container window",
      "      set current view to icon view",
      "      set toolbar visible to false",
      "      set statusbar visible to false",
      "      set bounds to {120, 120, $([int](120 + $layoutWidth)), $([int](120 + $layoutHeight))}",
      "    end tell",
      "    set viewOptions to the icon view options of container window",
      "    set arrangement of viewOptions to not arranged",
      "    set icon size of viewOptions to 84",
      "    set text size of viewOptions to 14"
    )
    if ($bgLine) { $scriptLines += "    $bgLine" }
    $scriptLines += @(
      "    try",
      "      set position of item `"$appBundleName`" of container window to {$appIconX, $iconsY}",
      "    end try",
      "    try",
      "      set position of item `"Applications`" of container window to {$appsIconX, $iconsY}",
      "    end try",
      "    update without registering applications",
      "    delay 1",
      "    close",
      "    open",
      "    delay 1",
      "  end tell",
      "end tell"
    )
    $appleScript = $scriptLines -join [Environment]::NewLine
    & osascript -e $appleScript | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn 'Could not fully apply DMG Finder layout; continuing with default layout.' }
  } finally {
    if ($deviceNode) {
      $detached = $false
      for ($i = 0; $i -lt 6; $i++) {
        & hdiutil detach $deviceNode -quiet | Out-Null
        if ($LASTEXITCODE -eq 0) {
          $detached = $true
          break
        }
        Start-Sleep -Seconds 1
      }
      if (-not $detached) { throw "Failed to detach DMG device: $deviceNode" }
    }
  }

  Step "Converting DMG to compressed image: $dmgPath"
  & hdiutil convert $rwDmgPath -format UDZO -ov -o $dmgPath | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "Failed to convert DMG to compressed image: $dmgPath" }
  if (-not (Test-Path $dmgPath) -and (Test-Path "$dmgPath.dmg")) {
    Move-Item "$dmgPath.dmg" $dmgPath -Force
  }
  Remove-Item $rwDmgPath -Force -ErrorAction SilentlyContinue | Out-Null

  Info "DMG created: $dmgPath"
  Warn 'Remember to codesign & notarize for distribution.'
}

Step 'Summary'
Write-Host "Version: $Version"
Write-Host "Bundles:"; $AppBundles | ForEach-Object { Write-Host " - $($_.Rid): $($_.Path)" }
if ($Dmg) { Write-Host "DMG: $dmgPath" }

Step 'Done'
