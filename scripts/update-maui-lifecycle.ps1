<#
.SYNOPSIS
  Scrapes the Microsoft .NET MAUI support / EOL documentation page and outputs a machine-readable lifecycle JSON.
.DESCRIPTION
  The official MAUI repo does not publish a lifecycle JSON. This script fetches the public documentation page,
  parses the version/support end date table, and emits a JSON file consumed by the application to display
  MAUI-specific EOL badges per .NET channel.

  Run this script in CI (e.g., biweekly) similarly to the existing metadata snapshot job.

.OUTPUTS
  Writes DotNetUninstall/MetadataSnapshot/maui-lifecycle.json with schema:
  {
    "lastUpdated": "2025-10-02T12:34:56Z",
    "source": "https://learn.microsoft.com/dotnet/maui/supported-versions",
    "entries": [ { "channel": "8.0", "mauiVersion": "8.0.x", "eolDate": "2026-11-10" }, ... ]
  }

.NOTES
  Parsing is HTML-fragile; if the docs page layout changes, adjust the selectors/regex below.
#>
 [CmdletBinding()]
 param(
     [string]$OutFile = (Join-Path $PSScriptRoot '..' 'DotNetUninstall' 'MetadataSnapshot' 'maui-lifecycle.json'),
     [string[]]$UriCandidates = @(
         # Correct current support policy pages (dotnet.microsoft.com)
         'https://dotnet.microsoft.com/platform/support/policy/maui',
         'https://dotnet.microsoft.com/en-us/platform/support/policy/maui'
     ),
     [switch]$FailIfEmpty
 )

 Write-Host "Attempting to fetch MAUI lifecycle data from candidate URLs..." -ForegroundColor Cyan
 $resp = $null
 $usedUrl = $null
 $ua = 'dotuninstall-maui-scraper/1.0 (+https://github.com/lextudio)'
 $headers = @{ 'User-Agent' = $ua; 'Accept-Language' = 'en-US,en;q=0.8'; 'Accept'='text/html,application/xhtml+xml' }
 foreach ($u in $UriCandidates) {
     Write-Host "Trying: $u" -ForegroundColor DarkCyan
     try {
         $resp = Invoke-WebRequest -Uri $u -UseBasicParsing -Headers $headers -ErrorAction Stop
         $code = $resp.StatusCode
         Write-Host "  -> status: $code length=$($resp.Content.Length)" -ForegroundColor DarkGray
         if ($code -ge 200 -and $code -lt 300 -and $resp.Content) { $usedUrl = $u; break }
     } catch {
         Write-Host "  -> failed: $($_.Exception.Message)" -ForegroundColor DarkGray
     }
 }
 if (-not $resp -or -not $usedUrl) {
     Write-Warning "All candidate URLs failed. Will attempt derived fallback using releases-index.json.";
 }
 else {
     Write-Host "Using source: $usedUrl" -ForegroundColor Green
 }

 $html = $resp.Content
 if ($usedUrl -and -not $html) { Write-Error 'Empty response body.'; exit 1 }
 if (-not $usedUrl) { $html = '' } # allow fallback

 # Normalize whitespace for regex scanning
 $normalized = ($html -replace '\s+', ' ')

 # Strategy: Try to find a lifecycle table which may contain any of these header cues.
 $entries = @()
 # Extract both supported and out-of-support tables via aria-labelledby ids
 $tableBlocks = @()
 $tableBlocks += ([Regex]::Matches($normalized, '<table[^>]*aria-labelledby="supported-versions".*?</table>', 'IgnoreCase') | ForEach-Object { $_.Value })
 $tableBlocks += ([Regex]::Matches($normalized, '<table[^>]*aria-labelledby="out-of-support-versions".*?</table>', 'IgnoreCase') | ForEach-Object { $_.Value })
foreach ($tb in $tableBlocks) {
    $rowSources = @()
    $tbodyMatches = [Regex]::Matches($tb, '<tbody[^>]*?>(.*?)</tbody>', 'IgnoreCase')
    if ($tbodyMatches.Count -gt 0) {
        $rowSources = $tbodyMatches | ForEach-Object { $_.Groups[1].Value }
    } else {
        # Fallback to entire table HTML if tbody tags disappear again
        $rowSources = @($tb)
    }
    foreach ($chunk in $rowSources) {
        $rows = [Regex]::Matches($chunk, '<tr[^>]*?>.*?</tr>', 'IgnoreCase') | ForEach-Object { $_.Value }
        if ($rows.Count -eq 0) { continue }
        # Skip thead row(s); identify data rows from tbody (simple approach: skip first row containing <th>)
        foreach ($r in $rows) {
            if ($r -match '<th') { continue }
            $cells = [Regex]::Matches($r, '<t[dh][^>]*?>(.*?)</t[dh]>', 'IgnoreCase') | ForEach-Object {
                $inner = $_.Groups[1].Value
                $inner = [Regex]::Replace($inner, '<.*?>', '')
                $inner = [System.Net.WebUtility]::HtmlDecode($inner)
                $inner.Trim()
            }
            # Some minification anomalies may collapse tags; ensure we still have at least 5 cells with td after filtering
            if (($cells | Where-Object { $_ -ne '' }).Count -lt 5) { continue }
            $versionText = $cells[0]
            if ($versionText -match '(?i)preview') { continue }
            # Expect columns: Version | Original release date | Latest patch version | Patch release date | End of support
            $latestPatch = $cells[2]
            $endRaw = $cells[4]
            $eolIso = $null
            if (-not [string]::IsNullOrWhiteSpace($endRaw)) {
                $candidate = $endRaw -replace '^[A-Za-z]+,\s*',''  # strip weekday
                $parsedEnd = [datetime]::MinValue
                if ([DateTime]::TryParse($candidate, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AllowWhiteSpaces, [ref]$parsedEnd)) {
                    $eolIso = $parsedEnd.ToUniversalTime().ToString('yyyy-MM-dd')
                }
            }
            $channel = $null
            if ($versionText -match '(\d+\.\d+)') { $channel = $Matches[1] }
            elseif ($versionText -match '(\d+)') { $channel = "$($Matches[1]).0" }
            if (-not $channel) { continue }
            # Debug: uncomment for troubleshooting
            # Write-Host "Row => version='$versionText' channel='$channel' patch='$latestPatch' eol='$eolIso'" -ForegroundColor DarkGray
            $entries += [pscustomobject]@{
                channel     = $channel
                mauiVersion = $versionText
                latestPatch = $latestPatch
                eolDate     = $eolIso
            }
        }
    }
}
if ($entries.Count -eq 0) { Write-Warning 'No data rows parsed from policy page tables.' }

# Fallback extraction: direct row pattern scan for '.NET MAUI X' sequences if we have fewer than 3 entries
if ($entries.Count -lt 3 -and $normalized) {
  Write-Host "Attempting fallback row pattern extraction..." -ForegroundColor DarkCyan
  $rowPattern = '<td>\.NET MAUI (?<major>\d+)</td>\s*<td>(?<orig>.*?)</td>\s*<td>(?<patch>[0-9\.]+)</td>\s*<td>(?<patchDate>.*?)</td>\s*<td>(?<eol>.*?)</td>'
  $rowMatchesLocal = [Regex]::Matches($normalized, $rowPattern, 'IgnoreCase')
  foreach ($m in $rowMatchesLocal) {
    $major = $m.Groups['major'].Value
    if (-not $major) { continue }
    $patch = $m.Groups['patch'].Value
    $eolRaw = $m.Groups['eol'].Value
    $eolIso = $null
    if ($eolRaw -and $eolRaw -notmatch '(?i)TBD|N/?A') {
      $cand = $eolRaw -replace '^[A-Za-z]+,\s*',''
      $dt = [datetime]::MinValue
      if ([DateTime]::TryParse($cand, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AllowWhiteSpaces, [ref]$dt)) {
        $eolIso = $dt.ToUniversalTime().ToString('yyyy-MM-dd')
      }
    }
    $versionLabel = ".NET MAUI $major"
    $channel = "$major.0"
    if (-not ($entries | Where-Object { $_.channel -eq $channel })) {
      $entries += [pscustomobject]@{ channel = $channel; mauiVersion = $versionLabel; latestPatch = $patch; eolDate = $eolIso }
    }
  }
  Write-Host "Fallback extraction produced $($entries.Count) total entries." -ForegroundColor DarkCyan
}

# Deduplicate by channel keeping first non-null eolDate (or last with date)
$dedup = @{}
foreach ($e in $entries) {
    if (-not $dedup.ContainsKey($e.channel)) {
        $dedup[$e.channel] = $e
    } elseif (-not $dedup[$e.channel].eolDate -and $e.eolDate) {
        $dedup[$e.channel] = $e
    }
}

$result = [pscustomobject]@{
    lastUpdated = (Get-Date).ToUniversalTime().ToString('o')
    source      = $usedUrl
    entries     = [System.Collections.Generic.List[object]]::new()
}
$orderedChannels = $dedup.Keys | Sort-Object { [version]$_ }
foreach ($v in $orderedChannels) {
    $result.entries.Add($dedup[$v])
}

$null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutFile)
 if ($FailIfEmpty -and $result.entries.Count -eq 0) { Write-Error 'No MAUI lifecycle entries parsed from policy page.'; exit 2 }
 $json = $result | ConvertTo-Json -Depth 6
 $json | Out-File -FilePath $OutFile -Encoding UTF8
Write-Host "Wrote lifecycle JSON: $OutFile (entries=$($result.entries.Count))" -ForegroundColor Green
