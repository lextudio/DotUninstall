#!/usr/bin/env pwsh
<#
  Exact SVG propagation script.
  Copies logo.svg verbatim into target SVGs (icon_foreground.svg, splash_screen.svg, etc.)
  Adds a signature comment containing a hash of the source so subsequent runs can skip.
  Excludes icon.svg explicitly (left for manual design).
  Usage examples:
    pwsh ./update-svgs-from-logo.ps1 -DryRun
    pwsh ./update-svgs-from-logo.ps1 -Force
    pwsh ./update-svgs-from-logo.ps1 -Include splash*
#>
[CmdletBinding()]
param(
  [string]$Source = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'logo.svg'),
  [string]$Root   = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')) 'DotNetUninstall/Assets'),
  [switch]$Force,
  [switch]$DryRun,
  [string]$Include,
  [string]$Exclude
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Info($m){ Write-Host "[INFO] $m" -ForegroundColor DarkGray }
function Step($m){ Write-Host "[STEP] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[WARN] $m" -ForegroundColor Yellow }
function Err($m){ Write-Host "[ERR ] $m" -ForegroundColor Red }

if (-not (Test-Path $Source)) { throw "Source SVG not found: $Source" }

$raw = Get-Content -Raw -Path $Source
if ($raw -notmatch '<svg[\s\S]*?</svg>') { throw 'Source does not look like a valid SVG.' }

# (Exact mode) No parsing or resizing beyond hash/signature is performed.

# Hash signature for idempotency
$hash = (Get-FileHash -Algorithm SHA256 -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($raw)))).Hash.Substring(0,12)
$signature = "Generated-from-logo:$hash"
$modeString = 'exact-copy'

# (No size map in exact mode)

# Discover targets
$targets = Get-ChildItem -Path $Root -Recurse -Filter *.svg | Where-Object { $_.FullName -ne (Resolve-Path $Source).Path }
if ($Include) {
  $pattern = $Include -replace '\*','.*'
  $targets = $targets | Where-Object { $_.Name -match "^$pattern$" }
}
if ($Exclude) {
  $patternX = $Exclude -replace '\*','.*'
  $targets = $targets | Where-Object { $_.Name -notmatch "^$patternX$" }
}
# Always leave icon.svg untouched (user preference) unless user explicitly runs separately.
$targets = $targets | Where-Object { $_.Name -ne 'icon.svg' }
if (-not $targets) { Warn 'No target SVGs found after filters.'; return }

Step "Processing $($targets.Count) SVG(s)"

foreach ($t in $targets) {
  $name = $t.Name
  $content = Get-Content -Raw -Path $t.FullName
  # Only skip if file already has same source hash AND is already an exact-copy (legacy single-path variants will be updated)
  if (-not $Force -and $content -match $signature -and $content -match 'Mode:\s*exact-copy') {
    Info "$name already up-to-date (signature present). Skipping."
    continue
  }

  # Exact copy mode: we do NOT alter width/height anymore – literal source SVG is used (plus signature comment)
  $copy = $raw
  if ($copy -match '(?s)^<\?xml.*?\?>') {
    $copy = $copy -replace '(?s)^(<\?xml.*?\?>)', "`$1`n<!-- $signature | Source: $(Split-Path -Leaf $Source) | Mode: $modeString | $(Get-Date -Format o) -->"
  } elseif ($copy -match '<svg') {
    $copy = "<!-- $signature | Source: $(Split-Path -Leaf $Source) | Mode: $modeString | $(Get-Date -Format o) -->`n" + $copy
  }
  $new = $copy

  if ($DryRun) {
    Write-Host "[DRY] Would update: $name" -ForegroundColor Magenta
  } else {
    Set-Content -Path $t.FullName -Value $new -Encoding UTF8
    Write-Host "[OK ] Updated $name (mode: $modeString)" -ForegroundColor Green
  }
}

if ($DryRun) { Write-Host 'Dry run complete. Re-run without -DryRun to apply.' -ForegroundColor Cyan }
else { Write-Host 'SVG update complete.' -ForegroundColor Cyan }
