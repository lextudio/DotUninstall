# Offline Metadata & Air-Gapped Enrichment

This application enriches listed SDK/runtime installs with lifecycle information (support phase, EOL date), latest available channel versions, and security update highlighting. These features depend on public .NET release metadata JSON published by Microsoft.

In disconnected or restricted (air‑gapped) environments, live HTTP retrieval will fail. To preserve functionality the app ships with an embedded snapshot of the metadata. This document explains how that snapshot is produced, refreshed, and consumed.

## Data Sources

The tool consumes:

1. `releases-index.json` – top‑level list of supported .NET channels including release type (STS/LTS), support phase and EOL date, plus a per‑channel `releases.json` URL.
2. Per‑channel `releases.json` – enumerates SDK & runtime releases (preview, GA, security), enabling detection of latest stable versions and security releases.

## Resolution / Fallback Order

1. Attempt live download of `releases-index.json`.
2. If unavailable, load embedded snapshot `releases-index.json`.
3. For each required channel: attempt live `releases.json`.
4. If that fails, fall back to embedded channel JSON (e.g. `8_0.json`).
5. If neither live nor snapshot channel data is present, the UI still renders installed items but without enrichment (badges may be missing).

## Snapshot Layout

Embedded files reside under `DotNetUninstall/MetadataSnapshot/`:

- `releases-index.json`
- `<major>_<minor>.json` per channel (e.g. `8_0.json`, `9_0.json`).

They are included as `<EmbeddedResource>` during build and loaded via reflection when live fetch fails.

## Generating / Updating the Snapshot Locally

Use the provided PowerShell script (PowerShell 7+ recommended):

```powershell
./scripts/update-metadata-snapshot.ps1       # downloads index + all channel release files
./scripts/update-metadata-snapshot.ps1 -Force # clears old snapshot first
```

After running, commit changed files in `DotNetUninstall/MetadataSnapshot/`.

## Automated Refresh (CI)

A scheduled GitHub Actions workflow (`.github/workflows/update-metadata-snapshot.yml`) runs biweekly (1st & 15th 02:30 UTC) and on manual dispatch to:

- Download the current public metadata.
- Detect any diff.
- Commit updated JSON snapshot files back to the repo.

This keeps the embedded snapshot reasonably fresh for offline distributions without manual intervention.

## Live Results Caching

To reduce network calls and provide resilience, live metadata is cached:

- Disk cache directory: `%AppData%/dotnet-uninstall-ui/cache` (platform‑appropriate path).
- Files: `releases-index.json` plus `channel-<major.minor>.json` for each used channel.
- TTL: 24 hours (previously 6h).
- In‑memory cache also exists (12h) for the active session.

Indicators in the UI:

- “Using embedded metadata snapshot (offline)” – snapshot fallback was used.
- “Showing cached live metadata (may be up to 24h old)” – served from disk cache.

## Clearing the Cache

In the Settings tab, press “Clear Metadata Cache” to:

- Delete the on‑disk cache directory.
- Reset in‑memory state.
- Trigger an immediate refresh (attempting live again, then fallback).

## When to Refresh the Snapshot Manually

- Before publishing a release intended for offline environments and wanting the most current lifecycle info.
- Shortly after major .NET channel GA or EOL transitions.

## Potential Enhancements

- Embed a manifest file with snapshot timestamp & channel list for staleness display.
- Configurable TTL via a small JSON settings file.
- UI display of last successful live fetch time.

## Troubleshooting

| Symptom | Possible Cause | Action |
| ------- | -------------- | ------ |
| No lifecycle / latest badges | Missing or failed metadata loads | Run snapshot script; verify embedded files present |
| Snapshot banner always visible even online | Network blocked | Confirm outbound HTTPS to `https://dotnetcli.blob.core.windows.net` / `https://aka.ms` endpoints |
| Cache never updates | TTL not expired & using disk cache | Clear cache manually in Settings |

If issues persist, run the script manually with `-Force` and inspect downloaded JSON for structure changes.

---
Maintainers: Keep this document updated if workflow schedule, TTL, or snapshot mechanism changes.
