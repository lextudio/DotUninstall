#!/usr/bin/env pwsh
param(
  [string]$Configuration = 'Release',
  [switch]$WindowsSelfContained,
  [switch]$WindowsTrim,
  [switch]$MacDmg,
  [string[]]$MacRids = @('osx-arm64','osx-x64')
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

# 1. macOS packages (DMG)
if ($MacDmg.IsPresent) {
  Step 'Packaging macOS'
  $macScript = Join-Path $PSScriptRoot 'package-macos.ps1'
  & $macScript -Rids $MacRids -Configuration $Configuration -Dmg | Out-Null
}

# 2. Windows packages (.exe single files)
Step 'Packaging Windows'
$winScript = Join-Path $PSScriptRoot 'package-windows.ps1'
$winArgs = @('-Configuration', $Configuration)
if ($WindowsSelfContained.IsPresent) { $winArgs += '-SelfContained' }
if ($WindowsTrim.IsPresent) { $winArgs += '-Trim' }
& $winScript @winArgs | Out-Null

# 3. Generate SHA256 hashes for all artifacts (.exe, .dmg)
Step 'Generating SHA256 hashes'
$hashFile = Join-Path $releaseOut 'sha256sums.txt'
Remove-Item $hashFile -ErrorAction SilentlyContinue
Get-ChildItem $releaseOut -File -Include *.exe,*.dmg | Sort-Object Name | ForEach-Object {
  $hashInfo = Get-FileHash $_.FullName -Algorithm SHA256
  "${($hashInfo.Hash).ToLower()}  $($_.Name)" | Add-Content $hashFile
}

# 4. Summary
Step 'Summary'
Get-ChildItem $releaseOut -File | Sort-Object Name | Format-Table Name,Length,LastWriteTime
Write-Host "Hashes written to: $hashFile" -ForegroundColor Green
Step 'Done'
