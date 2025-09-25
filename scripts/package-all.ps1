#!/usr/bin/env pwsh
param(
  [string]$Configuration = 'Release',
  # Windows build modifiers
  [switch]$WindowsSelfContained,
  [switch]$WindowsTrim,
  # Skips
  [switch]$SkipWindows,
  [switch]$SkipMac,
  # Disable DMG creation (mac builds still produced)
  [switch]$NoDmg,
  # macOS specifics
  [string[]]$MacRids = @('osx-arm64','osx-x64'),
  [string]$MacIconPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$artifacts = Join-Path $root 'artifacts'
$releaseOut = Join-Path $artifacts 'release'
New-Item -ItemType Directory -Force -Path $releaseOut | Out-Null

if (-not $SkipMac) {
  Step 'Packaging macOS'
  $macScript = Join-Path $PSScriptRoot 'package-macos.ps1'
  # Auto-detect icon if not provided
  if (-not $MacIconPath) {
    $autoIcon = Join-Path $root 'DotNetUninstall/Assets/Images/AppIcon.icns'
    if (Test-Path $autoIcon) { $MacIconPath = $autoIcon }
  }
  $macParams = @{ Rids = $MacRids; Configuration = $Configuration }
  if (-not $NoDmg) { $macParams.Dmg = $true }
  if ($MacIconPath) { $macParams.IconPath = $MacIconPath }
  & $macScript @macParams | Out-Null
}

if (-not $SkipWindows) {
  Step 'Packaging Windows'
  $winScript = Join-Path $PSScriptRoot 'package-windows.ps1'
  $winArgs = @('-Configuration', $Configuration)
  if ($WindowsSelfContained.IsPresent) { $winArgs += '-SelfContained' }
  if ($WindowsTrim.IsPresent) { $winArgs += '-Trim' }
  & $winScript @winArgs | Out-Null
}

# 3. Generate SHA256 hashes for all artifacts (.exe, .dmg)
Step 'Generating SHA256 hashes'
$hashFile = Join-Path $releaseOut 'sha256sums.txt'
Remove-Item $hashFile -ErrorAction SilentlyContinue
$targets = Get-ChildItem $releaseOut -File | Where-Object { $_.Name -match '\.(exe|dmg)$' } | Sort-Object Name
if ($targets.Count -eq 0) {
  Warn 'No .exe or .dmg artifacts found to hash.'
} else {
  foreach ($f in $targets) {
    $line = (Get-FileHash $f.FullName -Algorithm SHA256).Hash.ToLower() + '  ' + $f.Name
    $line | Add-Content $hashFile
  }
  Info "SHA256 hashes written: $hashFile"
}

# 4. Summary
Step 'Summary'
Get-ChildItem $releaseOut -File | Sort-Object Name | Format-Table Name,Length,LastWriteTime
Write-Host "Hashes written to: $hashFile" -ForegroundColor Green
Step 'Done'
