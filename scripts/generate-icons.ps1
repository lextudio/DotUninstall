#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Generate multi-platform icons (ICO for Windows, ICNS for macOS) from a source PNG.

.DESCRIPTION
  Uses ImageMagick's `magick` if available for ICO generation; falls back to `sips` + `iconutil` for ICNS on macOS.
  You can install ImageMagick via Homebrew: brew install imagemagick
  For Windows ICO without ImageMagick on macOS, attempts `png2ico` if present (brew install png2ico), otherwise warns.

.PARAMETER Source
  Path to the base square PNG (at least 512x512 recommended).

.PARAMETER OutputDir
  Directory where generated artifacts are placed. Defaults to Assets/Images relative to repo root.

.EXAMPLE
  ./scripts/generate-icons.ps1 -Source ./logo.png

#>
param(
  [string]$Source = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'logo.png'),
  [string]$OutputDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Err($m){ Write-Host "[ERR ] $m" -ForegroundColor Red }

if (-not (Test-Path $Source)) { throw "Source image not found: $Source" }

if (-not $OutputDir) {
  $OutputDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'DotNetUninstall/Assets/Images'
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

# Generate resized PNG set
$pngSizes = @(16,24,32,48,64,128,256,512)
$baseName = 'logo'
$srcAbs = (Resolve-Path $Source).Path

Step 'Generating resized PNG assets'
foreach ($s in $pngSizes) {
  $dest = Join-Path $OutputDir ("$baseName-$s.png")
  & sips -z $s $s $srcAbs --out $dest | Out-Null
  Info "Generated $dest"
}

# Windows ICO
$icoPath = Join-Path $OutputDir 'app.ico'
$magick = Get-Command magick -ErrorAction SilentlyContinue
$png2ico = Get-Command png2ico -ErrorAction SilentlyContinue
if ($magick) {
  Step 'Generating ICO via ImageMagick'
  $pngList = Get-ChildItem $OutputDir -Filter 'logo-*.png' | Where-Object { $_.Name -match 'logo-(16|24|32|48|64|128|256).png' } | Sort-Object Name | ForEach-Object { $_.FullName }
  & magick $pngList $icoPath
  Info "ICO created: $icoPath"
} elseif ($png2ico) {
  Step 'Generating ICO via png2ico'
  Push-Location $OutputDir
  & $png2ico.Name app.ico logo-16.png logo-24.png logo-32.png logo-48.png logo-64.png logo-128.png logo-256.png
  Pop-Location
  Info "ICO created: $icoPath"
} else {
  Warn 'Neither ImageMagick (magick) nor png2ico found; skipping ICO generation. Install via `brew install imagemagick` or `brew install png2ico`.'
}

# macOS ICNS
$icnsDir = Join-Path $OutputDir 'AppIcon.iconset'
$icnsPath = Join-Path $OutputDir 'AppIcon.icns'
Step 'Preparing iconset for ICNS'
Remove-Item $icnsDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
New-Item -ItemType Directory -Path $icnsDir | Out-Null
# Required macOS icon sizes
$macPairs = @(
  @{Size=16; Scale=1}, @{Size=16; Scale=2},
  @{Size=32; Scale=1}, @{Size=32; Scale=2},
  @{Size=128; Scale=1}, @{Size=128; Scale=2},
  @{Size=256; Scale=1}, @{Size=256; Scale=2},
  @{Size=512; Scale=1}, @{Size=512; Scale=2}
)
foreach ($p in $macPairs) {
  $suffix = if ($p.Scale -eq 2) { '@2x' } else { '' }
  $destName = "icon_$($p.Size)x$($p.Size)$suffix.png"
  $targetPx = $p.Size * $p.Scale
  $srcForSize = Join-Path $OutputDir ("$baseName-$targetPx.png")
  if (-not (Test-Path $srcForSize)) {
    & sips -z $targetPx $targetPx $srcAbs --out $srcForSize | Out-Null
  }
  Copy-Item $srcForSize (Join-Path $icnsDir $destName)
}
Step 'Creating ICNS'
$iconutil = Get-Command iconutil -ErrorAction SilentlyContinue
if ($iconutil) {
  & iconutil -c icns $icnsDir -o $icnsPath
  Info "ICNS created: $icnsPath"
} else {
  Warn 'iconutil not found; skipping ICNS generation.'
}

Write-Host 'Done.' -ForegroundColor Green
