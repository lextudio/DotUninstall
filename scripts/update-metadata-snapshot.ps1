<#
.SYNOPSIS
  Fetches .NET release metadata (index + per-channel releases.json) and stores them as embedded snapshot files for offline use.

.DESCRIPTION
  Downloads the releases-index.json and then for each channel listed downloads its releases.json.
  Files are saved under DotNetUninstall/MetadataSnapshot as:
    - releases-index.json
    - <channel-version with dot replaced by underscore>.json (e.g. 8_0.json)

  These get embedded as resources (see csproj) and used as offline fallback.

.PARAMETER Force
  If specified, deletes existing snapshot files before downloading.

.EXAMPLE
  ./scripts/update-metadata-snapshot.ps1

.EXAMPLE
  ./scripts/update-metadata-snapshot.ps1 -Force
#>
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$metaDir = Join-Path $root 'DotNetUninstall/MetadataSnapshot'
if(!(Test-Path $metaDir)) { New-Item -ItemType Directory -Path $metaDir | Out-Null }

if($Force) {
  Get-ChildItem $metaDir -Filter '*.json' | Remove-Item -Force -ErrorAction SilentlyContinue
}

$indexUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json'
$indexPath = Join-Path $metaDir 'releases-index.json'
Write-Host "Downloading releases-index.json ..."
Invoke-RestMethod -Uri $indexUrl -OutFile $indexPath

$indexJson = Get-Content $indexPath -Raw | ConvertFrom-Json
$channels = @($indexJson.'releases-index' | Where-Object { $_.'channel-version' -ne $null })

foreach($ch in $channels) {
  $channelVersion = $ch.'channel-version'
  $relUrl = $ch.'releases.json'
  if([string]::IsNullOrWhiteSpace($relUrl)) { continue }
  $fileName = ($channelVersion -replace '\.', '_') + '.json'
  $dest = Join-Path $metaDir $fileName
  Write-Host "Downloading channel $channelVersion -> $fileName"
  try {
    Invoke-RestMethod -Uri $relUrl -OutFile $dest
  }
  catch {
    Write-Warning "Failed to download $relUrl : $_"
  }
}

Write-Host "Snapshot update complete. Files:" -ForegroundColor Green
Get-ChildItem $metaDir -Filter '*.json' | Select-Object Name, Length | Format-Table
