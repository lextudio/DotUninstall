using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization; // For JsonPropertyName attributes mapping kebab-case properties
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotNetUninstall.Models;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Mono.Unix.Native; // Mono.Posix for elevation detection
using NuGet.Versioning;
using Octokit;
using Uno.Extensions.Navigation;
using Windows.Foundation.Metadata;

namespace DotNetUninstall.Presentation;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigator _navigator;

    public ObservableCollection<DotnetInstallEntry> SdkItems { get; } = new();
    public ObservableCollection<DotnetInstallEntry> RuntimeItems { get; } = new();
    public ObservableCollection<ChannelGroup> GroupedSdkItems { get; } = new();
    public ObservableCollection<ChannelGroup> GroupedRuntimeItems { get; } = new();
    public int SdkCount => SdkItems.Count;
    public int RuntimeCount => RuntimeItems.Count;
    public int TotalCount => SdkItems.Count + RuntimeItems.Count;

    // Elevation (sudo/root) detection on macOS
    [ObservableProperty]
    private bool isElevated; // True when effective UID == 0 (macOS/Linux)

    [ObservableProperty]
    private string? originalUser; // SUDO_USER or current user when elevated

    [ObservableProperty]
    private bool showElevationWarning; // Controls visibility of the banner

    [ObservableProperty]
    private bool showElevationOffer; // Shown when NOT elevated on macOS

    public bool CanPerformUninstalls
        => !OperatingSystem.IsMacOS() || IsElevated; // On macOS require elevation

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private string? errorMessage;

    // Legacy properties retained for minimal XAML changes but now constant
    [ObservableProperty]
    private bool hasUninstallTool = true; // Always true: using embedded logic

    [ObservableProperty]
    private string? uninstallToolPath; // Unused

    [ObservableProperty]
    private string? uninstallToolVersion; // Unused

    [ObservableProperty]
    private string? suggestedDownload; // Unused

    // GitHub update notification
    [ObservableProperty]
    private string? latestReleaseTag;

    [ObservableProperty]
    private bool hasUpdate;

    [ObservableProperty]
    private string? updateMessage;

    // Metadata acquisition status flags
    [ObservableProperty]
    private bool isUsingSnapshot; // True when data came from embedded snapshot (no network)

    [ObservableProperty]
    private bool isUsingCachedLive; // True when data served from on-disk cached JSON within TTL

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<DotnetInstallEntry> UninstallCommand { get; }
    public IAsyncRelayCommand BrowseCommand { get; }
    public IAsyncRelayCommand CheckUpdateNowCommand { get; }
    public IAsyncRelayCommand ClearCacheCommand { get; }

    public string Title { get; }
    public string AppVersion => GetCurrentVersion();

    public MainViewModel(IStringLocalizer localizer, IOptions<AppConfig> appInfo, INavigator navigator)
    {
        _navigator = navigator;
        Title = $".NET Uninstall Tool UI - {localizer["ApplicationName"]} {appInfo?.Value?.Environment}";
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        UninstallCommand = new AsyncRelayCommand<DotnetInstallEntry>(UninstallAsync, _ => HasUninstallTool);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        CheckUpdateNowCommand = new AsyncRelayCommand(ForceCheckForUpdatesAsync);
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync);
        SdkItems.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(SdkCount));
            OnPropertyChanged(nameof(TotalCount));
        };
        RuntimeItems.CollectionChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(RuntimeCount));
            OnPropertyChanged(nameof(TotalCount));
        };
        // External tool path no longer required.
        DetectElevation();
        // Fire and forget update check
        _ = Task.Run(CheckForUpdatesAsync);
    }

    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        StatusMessage = "Refreshing...";
        SdkItems.Clear();
        RuntimeItems.Clear();
        try
        {
            await ListFromEmbeddedAsync();
            BuildGroups();
            StatusMessage = $"Loaded {TotalCount} entries.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Failed.";
        }
        finally
        {
            IsLoading = false;
        }
    }


    private async Task ListFromEmbeddedAsync()
    {
        var parsed = new List<DotnetInstallEntry>();
        try
        {
            var list = DotNetUninstall.Tooling.BundleListing.List();
            foreach (var e in list)
            {
                parsed.Add(new DotnetInstallEntry(e.Type, e.Type, e.Version, e.Architecture, e.CanUninstall, e.Reason)
                {
                    UninstallCommand = e.UninstallCommand,
                    DisplayName = e.DisplayName,
                    SubType = e.SubType
                });
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        // Prepare metadata before adding to observable collections
        try
        {
            var neededChannels = new HashSet<string>(parsed.Select(p => DeriveChannel(p.Version)), StringComparer.OrdinalIgnoreCase);
            if (neededChannels.Count > 0)
            {
                await EnsureMetadataAsync(neededChannels);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = (StatusMessage ?? "") + $" (Metadata fetch failed: {ex.Message})";
        }

        int sdkCount = 0, rtCount = 0;
        foreach (var baseEntry in parsed)
        {
            DotnetInstallEntry finalEntry = baseEntry;
            try
            {
                var channel = DeriveChannel(baseEntry.Version);
                ChannelResolved? meta = null;
                if (_channelCache != null)
                {
                    _channelCache.TryGetValue(channel, out meta);
                }
                var (previewKind, previewNum) = DerivePreviewInfo(baseEntry.Version);
                bool isPreview = previewKind != "ga";
                bool outOfSupport = meta != null && (meta.SupportPhase == "eol" || (meta.EolDate.HasValue && meta.EolDate.Value < DateTime.UtcNow.Date));
                bool isSecurity = meta != null && ((baseEntry.Type == "sdk" && meta.SecurityVersions.Contains(baseEntry.Version)) || (baseEntry.Type == "runtime" && meta.SecurityVersions.Contains(baseEntry.Version)));
                SecurityStatus securityStatus = SecurityStatus.None;
                string? securityTooltip = null;
                if (meta != null)
                {
                    var latestSec = baseEntry.Type == "sdk" ? meta.LatestSecuritySdk : meta.LatestSecurityRuntime;
                    (securityStatus, securityTooltip) = DotNetUninstall.Core.SecurityClassificationHelper.Classify(baseEntry.Version, latestSec, isSecurity);
                }
                finalEntry = baseEntry with
                {
                    Channel = channel,
                    ReleaseType = meta?.ReleaseType?.ToUpperInvariant(),
                    SupportPhase = meta?.SupportPhase,
                    IsOutOfSupport = outOfSupport,
                    PreviewKind = previewKind,
                    PreviewNumber = previewNum,
                    IsPreview = isPreview,
                    IsSecurityUpdate = isSecurity,
                    EolDate = meta?.EolDate,
                    SecurityStatus = securityStatus,
                    SecurityTooltip = securityTooltip,
                    ReleaseDate = ResolveReleaseDate(meta, baseEntry.Version),
                    ReleaseNotesUrl = ResolveReleaseNotes(meta, baseEntry.Version)
                };
            }
            catch { }

            if (finalEntry.Type == "sdk")
            {
                SdkItems.Add(finalEntry);
                sdkCount++;
            }
            else
            {
                RuntimeItems.Add(finalEntry);
                rtCount++;
            }
        }
        StatusMessage = $"SDKs: {sdkCount}, Runtimes: {rtCount}";
    }

    private static readonly Uri ReleaseMetadataIndex = new("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json");

    // Real schema (subset) for releases-index.json
    private sealed class ReleasesIndexRoot
    {
        [JsonPropertyName("releases-index")] public List<ChannelInfo>? ReleasesIndex { get; set; }
    }
    private sealed class ChannelInfo
    {
        [JsonPropertyName("channel-version")] public string? ChannelVersion { get; set; }
        [JsonPropertyName("release-type")] public string? ReleaseType { get; set; }      // lts | sts
        [JsonPropertyName("support-phase")] public string? SupportPhase { get; set; }     // preview | go-live | active | maintenance | eol
        [JsonPropertyName("eol-date")] public DateTime? EolDate { get; set; }
        [JsonPropertyName("releases.json")] public string? ReleasesJson { get; set; }
    }
    // Per-channel releases.json subset
    private sealed class ChannelReleases { public List<ChannelRelease>? Releases { get; set; } }
    private sealed class ChannelRelease
    {
        [JsonPropertyName("release-version")] public string? ReleaseVersion { get; set; }
        [JsonPropertyName("release-date")] public DateTime? ReleaseDate { get; set; }
        [JsonPropertyName("release-notes")] public string? ReleaseNotes { get; set; }
        public bool? Security { get; set; }
        public SdkRelease? Sdk { get; set; }
        public List<SdkRelease>? Sdks { get; set; }
        public RuntimeRelease? Runtime { get; set; }
    }
    private sealed class SdkRelease { public string? Version { get; set; } }
    private sealed class RuntimeRelease { public string? Version { get; set; } }

    // Simple in-memory cache (lifetime of process)
    private static DateTime _metaCacheTime = DateTime.MinValue;
    private static Dictionary<string, ChannelResolved>? _channelCache; // key = channel
    private sealed class ChannelResolved
    {
        public string? ReleaseType { get; set; }
        public string? SupportPhase { get; set; }
        public DateTime? EolDate { get; set; }
        public HashSet<string> SdkVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RuntimeVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SecurityVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LatestSdk { get; set; }
        public string? LatestRuntime { get; set; }
        public DateTime? MauiEolDate { get; set; }
        public string? LatestSecuritySdk { get; set; }
        public string? LatestSecurityRuntime { get; set; }
        // Per-version release date & notes
        public Dictionary<string, DateTime?> ReleaseDates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string?> ReleaseNotes { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Map version -> CVE ids (flattened), union stored in AllCves
        public Dictionary<string, List<string>> VersionCves { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> AllCves { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, DateTime?>? _mauiLifecycle; // channel -> eolDate
    private static bool _mauiLifecycleLoaded;
    private static void EnsureMauiLifecycleLoaded()
    {
        if (_mauiLifecycleLoaded) return;
        _mauiLifecycleLoaded = true;
        try
        {
            var asm = typeof(MainViewModel).Assembly;
            var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MetadataSnapshot.maui-lifecycle.json", StringComparison.OrdinalIgnoreCase));
            if (res == null) return;
            using var s = asm.GetManifestResourceStream(res);
            if (s == null) return;
            using var doc = JsonDocument.Parse(s);
            var dict = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entries.EnumerateArray())
                {
                    var channel = e.TryGetProperty("channel", out var chEl) ? chEl.GetString() : null;
                    DateTime? eol = null;
                    if (e.TryGetProperty("eolDate", out var eolEl) && eolEl.ValueKind == JsonValueKind.String)
                    {
                        if (DateTime.TryParse(eolEl.GetString(), out var parsed)) eol = parsed.Date;
                    }
                    if (!string.IsNullOrWhiteSpace(channel)) dict[channel!] = eol;
                }
            }
            _mauiLifecycle = dict;
        }
        catch { }
    }

    private static string DeriveChannel(string version)
    {
        if (NuGet.Versioning.NuGetVersion.TryParse(version, out var nv))
        {
            return nv.Major + "." + nv.Minor;
        }
        // Fallback to previous heuristic
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _)) return parts[0] + "." + parts[1];
        if (parts.Length >= 1) return parts[0];
        return version;
    }

    private static (string kind, int? number) DerivePreviewInfo(string version)
    {
        if (NuGet.Versioning.NuGetVersion.TryParse(version, out var nv))
        {
            if (!nv.IsPrerelease) return ("ga", null);
            // nv.ReleaseLabels is IEnumerable<string>
            var labels = nv.ReleaseLabels?.ToArray() ?? Array.Empty<string>();
            if (labels.Length == 0) return ("ga", null);
            var first = labels[0].ToLowerInvariant();
            if (first == "preview" || first == "rc")
            {
                int? num = null;
                if (labels.Length > 1 && int.TryParse(labels[1], out var n)) num = n;
                return (first, num);
            }
            return ("ga", null);
        }
        // Fallback regex (should rarely be needed once parsing succeeds)
        var v = version.ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(v, "-(preview|rc)(?:\\.(?<n>[0-9]+))?");
        if (!m.Success) return ("ga", null);
        int? num2 = null;
        if (m.Groups["n"].Success && int.TryParse(m.Groups["n"].Value, out var parsed2)) num2 = parsed2;
        return (m.Groups[1].Value, num2);
    }

    private static DateTime? ResolveReleaseDate(ChannelResolved? meta, string version)
    {
        if (meta == null) return null;
        if (meta.ReleaseDates.TryGetValue(version, out var dt)) return dt;
        return null;
    }

    private static string? ResolveReleaseNotes(ChannelResolved? meta, string version)
    {
        if (meta == null) return null;
        if (meta.ReleaseNotes.TryGetValue(version, out var rn)) return rn;
        return null;
    }

    private async Task EnsureMetadataAsync(HashSet<string> neededChannels)
    {
        // Refresh in-memory cache every 12 hours
        if (_channelCache != null && (DateTime.UtcNow - _metaCacheTime) < TimeSpan.FromHours(12))
        {
            // If cache already contains all needed channels, we are done
            if (neededChannels.All(c => _channelCache.ContainsKey(c))) return;
        }

        _channelCache ??= new();
        ReleasesIndexRoot? indexRoot = null;
        bool usedSnapshot = false;
        bool usedDiskCache = false;

        // Determine disk cache paths
        string? cacheDir = null;
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                cacheDir = System.IO.Path.Combine(baseDir, "dotnet-uninstall-ui", "cache");
                System.IO.Directory.CreateDirectory(cacheDir);
            }
        }
        catch { cacheDir = null; }

        var indexCachePath = cacheDir is null ? null : System.IO.Path.Combine(cacheDir, "releases-index.json");
        TimeSpan diskTtl = TimeSpan.FromDays(1); // TTL for disk cached live results

        // Try disk cache first (fresh enough)
        if (indexCachePath != null && System.IO.File.Exists(indexCachePath))
        {
            try
            {
                var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(indexCachePath);
                if (age < diskTtl)
                {
                    var json = await System.IO.File.ReadAllTextAsync(indexCachePath);
                    indexRoot = JsonSerializer.Deserialize<ReleasesIndexRoot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    if (indexRoot != null) usedDiskCache = true;
                }
            }
            catch { }
        }

        // If not served from disk cache attempt live network
        if (indexRoot == null)
        {
            try
            {
                using var http = new HttpClient();
                using var s = await http.GetStreamAsync(ReleaseMetadataIndex);
                indexRoot = await JsonSerializer.DeserializeAsync<ReleasesIndexRoot>(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                // Write to disk cache
                if (indexRoot != null && indexCachePath != null)
                {
                    try
                    {
                        var json = JsonSerializer.Serialize(indexRoot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        await System.IO.File.WriteAllTextAsync(indexCachePath, json);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Fallback to embedded snapshot if still null
        if (indexRoot == null)
        {
            try
            {
                var asm = typeof(MainViewModel).Assembly;
                var snapshotName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MetadataSnapshot.releases-index.json", StringComparison.OrdinalIgnoreCase));
                if (snapshotName != null)
                {
                    using var rs = asm.GetManifestResourceStream(snapshotName);
                    if (rs != null)
                    {
                        indexRoot = await JsonSerializer.DeserializeAsync<ReleasesIndexRoot>(rs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                        usedSnapshot = indexRoot != null;
                    }
                }
            }
            catch { }
        }

        if (indexRoot?.ReleasesIndex == null) return;

        var indexLookup = indexRoot.ReleasesIndex
            .Where(c => !string.IsNullOrWhiteSpace(c.ChannelVersion))
            .ToDictionary(c => c.ChannelVersion!, StringComparer.OrdinalIgnoreCase);

        foreach (var ch in neededChannels)
        {
            if (_channelCache.ContainsKey(ch)) continue; // already cached
            if (!indexLookup.TryGetValue(ch, out var ci) || string.IsNullOrWhiteSpace(ci.ReleasesJson)) continue;
            try
            {
                ChannelReleases? rels = null;
                if (!usedSnapshot)
                {
                    try
                    {
                        ChannelReleases? diskCached = null;
                        string? channelCachePath = null;
                        if (cacheDir != null)
                        {
                            channelCachePath = System.IO.Path.Combine(cacheDir, $"channel-{ch}.json");
                            if (System.IO.File.Exists(channelCachePath))
                            {
                                try
                                {
                                    var age = DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(channelCachePath);
                                    if (age < diskTtl)
                                    {
                                        var cachedJson = await System.IO.File.ReadAllTextAsync(channelCachePath);
                                        diskCached = JsonSerializer.Deserialize<ChannelReleases>(cachedJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                                        if (diskCached != null) usedDiskCache = true;
                                    }
                                }
                                catch { }
                            }
                        }
                        if (diskCached != null)
                        {
                            rels = diskCached;
                        }
                        else
                        {
                            using var http = new HttpClient();
                            using var rs = await http.GetStreamAsync(ci.ReleasesJson);
                            rels = await JsonSerializer.DeserializeAsync<ChannelReleases>(rs, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            if (rels != null && channelCachePath != null)
                            {
                                try
                                {
                                    var json = JsonSerializer.Serialize(rels, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                                    await System.IO.File.WriteAllTextAsync(channelCachePath, json);
                                }
                                catch { }
                            }
                        }
                    }
                    catch { /* will attempt snapshot below */ }
                }
                if (rels == null)
                {
                    // Attempt snapshot file: replace domain path with local resource naming convention
                    // Expect file name like 'releases-index' provides channel 'x.y' file pattern maybe not shipped; skip if unavailable
                    var asm = typeof(MainViewModel).Assembly;
                    var channelToken = ch.Replace('.', '_');
                    var resourceName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains($"MetadataSnapshot.{channelToken}", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    if (resourceName != null)
                    {
                        try
                        {
                            using var rs2 = asm.GetManifestResourceStream(resourceName);
                            if (rs2 != null)
                            {
                                rels = await JsonSerializer.DeserializeAsync<ChannelReleases>(rs2, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                            }
                        }
                        catch { }
                    }
                }
                var resolved = new ChannelResolved
                {
                    ReleaseType = ci.ReleaseType?.ToLowerInvariant(),
                    SupportPhase = ci.SupportPhase?.ToLowerInvariant(),
                    EolDate = ci.EolDate?.Date
                };
                if (rels?.Releases != null)
                {
                    int dbgTotalReleases = 0;
                    int dbgWithDate = 0;
                    // We prefer highest stable (non-prerelease) versions; if none exist for a channel (e.g., during preview / RC)
                    // we fallback to the highest prerelease so the UI can still surface a Latest badge (e.g., 10.0.0-rc.2).
                    NuGetVersion? maxStableSdk = null;
                    NuGetVersion? maxStableRuntime = null;
                    NuGetVersion? maxStableSecSdk = null;
                    NuGetVersion? maxStableSecRuntime = null;
                    NuGetVersion? maxAnySdk = null;
                    NuGetVersion? maxAnyRuntime = null;
                    NuGetVersion? maxAnySecSdk = null;       // any security (incl. prerelease)
                    NuGetVersion? maxAnySecRuntime = null;    // any security (incl. prerelease)
                    foreach (var r in rels.Releases)
                    {
                        dbgTotalReleases++;
                        bool sec = r.Security == true;
                        List<string>? cves = null;
                        if (sec && r is not null)
                        {
                            try
                            {
                                if (r is ChannelRelease cr && cr.GetType().GetProperty("CveList") != null) { }
                            }
                            catch { }
                        }
                        void AddSdk(string? v) { if (!string.IsNullOrWhiteSpace(v)) { resolved.SdkVersions.Add(v); if (sec) resolved.SecurityVersions.Add(v); } }
                        void AddRuntime(string? v) { if (!string.IsNullOrWhiteSpace(v)) { resolved.RuntimeVersions.Add(v); if (sec) resolved.SecurityVersions.Add(v); } }
#pragma warning disable CS8602
                        var localSdk = r.Sdk;
#pragma warning restore CS8602
                        if (localSdk is not null)
                        {
                            var sdkVerTmp = localSdk.Version;
                            if (!string.IsNullOrWhiteSpace(sdkVerTmp)) AddSdk(sdkVerTmp);
                        }
                        if (r.Sdks != null) foreach (var srel in r.Sdks) { var sv = srel.Version; if (!string.IsNullOrWhiteSpace(sv)) AddSdk(sv); }
                        var rtVerTmp = r.Runtime?.Version;
                        if (!string.IsNullOrWhiteSpace(rtVerTmp)) AddRuntime(rtVerTmp);

                        // Track latest versions (stable and any) for SDK
                        var sdkVersionString = r.Sdk?.Version;
                        if (!string.IsNullOrWhiteSpace(sdkVersionString) && NuGetVersion.TryParse(sdkVersionString, out var sdkNv))
                        {
                            if (maxAnySdk == null || sdkNv > maxAnySdk) maxAnySdk = sdkNv;
                            if (!sdkNv.IsPrerelease && (maxStableSdk == null || sdkNv > maxStableSdk)) maxStableSdk = sdkNv;
                            if (sec)
                            {
                                if (maxAnySecSdk == null || sdkNv > maxAnySecSdk) maxAnySecSdk = sdkNv;
                                if (!sdkNv.IsPrerelease && (maxStableSecSdk == null || sdkNv > maxStableSecSdk)) maxStableSecSdk = sdkNv;
                            }
                        }
                        // Track latest versions (stable and any) for Runtime
                        if (r.Runtime?.Version != null && NuGetVersion.TryParse(r.Runtime.Version, out var rtNv))
                        {
                            if (maxAnyRuntime == null || rtNv > maxAnyRuntime) maxAnyRuntime = rtNv;
                            if (!rtNv.IsPrerelease && (maxStableRuntime == null || rtNv > maxStableRuntime)) maxStableRuntime = rtNv;
                            if (sec)
                            {
                                if (maxAnySecRuntime == null || rtNv > maxAnySecRuntime) maxAnySecRuntime = rtNv;
                                if (!rtNv.IsPrerelease && (maxStableSecRuntime == null || rtNv > maxStableSecRuntime)) maxStableSecRuntime = rtNv;
                            }
                        }

                        // Parse CVE list placeholder
                        if (sec)
                        {
                            try
                            {
                                var keyVer = r.Sdk?.Version ?? r.Runtime?.Version;
                                if (!string.IsNullOrWhiteSpace(keyVer)) resolved.VersionCves[keyVer!] = cves ?? new List<string>();
                            }
                            catch { }
                        }
                        // Track release date / notes keyed by primary version (prefer SDK version, else runtime, else channel release-version)
                        // Capture all plausible version keys (some installed entries show shorter release-version while metadata contains full build)
                        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        if (!string.IsNullOrWhiteSpace(r.Sdk?.Version)) keys.Add(r.Sdk!.Version!);
                        if (!string.IsNullOrWhiteSpace(r.Runtime?.Version)) keys.Add(r.Runtime!.Version!);
                        if (!string.IsNullOrWhiteSpace(r.ReleaseVersion)) keys.Add(r.ReleaseVersion!);
                        // Some metadata includes "version-display"; attempt reflection-free extraction by scanning runtime JSON? (Not strongly typed here)
                        // If runtime version contains a build suffix (e.g., -rc.2.25502.107) and release-version is shorter (-rc.2), our keys set already has both.
                        foreach (var k in keys)
                        {
                            if (r.ReleaseDate.HasValue)
                            {
                                dbgWithDate++;
                                if (!resolved.ReleaseDates.ContainsKey(k)) resolved.ReleaseDates[k] = r.ReleaseDate.Value.Date;
                            }
                            if (!string.IsNullOrWhiteSpace(r.ReleaseNotes) && !resolved.ReleaseNotes.ContainsKey(k)) resolved.ReleaseNotes[k] = r.ReleaseNotes;
                        }
                    }
                    // Apply fallback: prefer stable, else prerelease
                    resolved.LatestSdk = (maxStableSdk ?? maxAnySdk)?.ToNormalizedString();
                    resolved.LatestRuntime = (maxStableRuntime ?? maxAnyRuntime)?.ToNormalizedString();
                    resolved.LatestSecuritySdk = (maxStableSecSdk ?? maxAnySecSdk)?.ToNormalizedString();
                    resolved.LatestSecurityRuntime = (maxStableSecRuntime ?? maxAnySecRuntime)?.ToNormalizedString();
                }
                _channelCache[ch] = resolved;
            }
            catch { /* ignore per-channel errors */ }
        }
        _metaCacheTime = DateTime.UtcNow;
        IsUsingSnapshot = usedSnapshot;
        IsUsingCachedLive = !usedSnapshot && usedDiskCache;
    }

    private async Task ClearCacheAsync()
    {
        if (IsLoading) return;
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                var cacheDir = System.IO.Path.Combine(baseDir, "dotnet-uninstall-ui", "cache");
                if (System.IO.Directory.Exists(cacheDir))
                {
                    System.IO.Directory.Delete(cacheDir, true);
                }
            }
        }
        catch { /* ignore errors deleting cache */ }
        _channelCache = null;
        _metaCacheTime = DateTime.MinValue;
        IsUsingCachedLive = false;
        IsUsingSnapshot = false;
        StatusMessage = "Cache cleared. Refreshing...";
        await RefreshAsync();
    }

    // TagEntriesAsync removed; tagging now performed during initial list parsing for better Uno binding behavior.

    private async Task UninstallAsync(DotnetInstallEntry? entry)
    {
        if (entry == null) return;
        // Internal logic always available
        ErrorMessage = null;
        StatusMessage = $"Uninstalling {entry.Type} {entry.Version}...";
        try
        {
            if (string.IsNullOrWhiteSpace(entry.UninstallCommand)) { ErrorMessage = "Missing uninstall command."; return; }
            // Simple confirmation: in UI frameworks without dialog support fallback to console; else proceed.
            if (!await ConfirmAsync($"Are you sure you want to uninstall {entry.Type} {entry.Version}?"))
            {
                StatusMessage = "Uninstall canceled.";
                return;
            }
            var (success, err) = DotNetUninstall.Tooling.BundleListing.Uninstall(new DotNetUninstall.Tooling.BundleInfoEntry(entry.Type, entry.Version, entry.Architecture, entry.CanUninstall, entry.Reason, entry.Version, entry.UninstallCommand));
            if (!success) ErrorMessage = err ?? "Uninstall failed.";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private Task<bool> ConfirmAsync(string message)
    {
#if WINDOWS
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Confirm Uninstall",
                Content = message,
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel"
            };
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Microsoft.UI.Xaml.Window.Current);
            WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
            var tcs = new TaskCompletionSource<bool>();
            _ = dialog.ShowAsync().AsTask().ContinueWith(t =>
            {
                var result = t.Result == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
                tcs.TrySetResult(result);
            });
            return tcs.Task;
        }
        catch { }
#endif
        // Fallback immediate approve (could extend with platform-specific dialogs later)
        return Task.FromResult(true);
    }

    private async Task BrowseAsync()
    {
        try
        {
            var pickerAvailable = ApiInformation.IsTypePresent("Windows.Storage.Pickers.FileOpenPicker");
            if (!pickerAvailable)
            {
                ErrorMessage = "File picker API unavailable; paste path manually.";
                return;
            }
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
#if WINDOWS
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Microsoft.UI.Xaml.Window.Current);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            catch { }
#endif
            picker.FileTypeFilter.Add("*");
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                UninstallToolPath = file.Path;
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private Task ApplyToolPathAsync() => Task.CompletedTask; // No-op

    private static string[] SplitLines(string text) => text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcessAsync(string fileName, string arguments)
    {
        var tcs = new TaskCompletionSource<(int, string, string)>();
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        string so = string.Empty, se = string.Empty;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) so += e.Data + "\n"; };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) se += e.Data + "\n"; };
        proc.Exited += (_, _) => tcs.TrySetResult((proc.ExitCode, so, se));
        if (!proc.Start()) throw new InvalidOperationException($"Cannot start process {fileName}");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return await tcs.Task.ConfigureAwait(false);
    }

    private void BuildGroups()
    {
        GroupedSdkItems.Clear();
        GroupedRuntimeItems.Clear();
        static (int major, int minor) ParseChannel(string? channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return (-1, -1);
            var parts = channel.Split('.', StringSplitOptions.RemoveEmptyEntries);
            int major = -1, minor = -1;
            if (parts.Length > 0) int.TryParse(parts[0], out major);
            if (parts.Length > 1) int.TryParse(parts[1], out minor);
            return (major, minor);
        }
        IOrderedEnumerable<IGrouping<string?, DotnetInstallEntry>> OrderGroups(IEnumerable<IGrouping<string?, DotnetInstallEntry>> groups)
            => groups.OrderByDescending(g => ParseChannel(g.Key).major)
                     .ThenByDescending(g => ParseChannel(g.Key).minor);
        if (SdkItems.Count > 0)
        {
            foreach (var grp in OrderGroups(SdkItems.GroupBy(i => i.Channel ?? DeriveChannel(i.Version))))
            {
                var first = grp.FirstOrDefault();
                var rt = first?.ReleaseType?.ToUpperInvariant();
                ChannelResolved? cr = null;
                _channelCache?.TryGetValue(grp.Key!, out cr);
                EnsureMauiLifecycleLoaded();
                DateTime? mauiEol = null;
                if (_mauiLifecycle != null && grp.Key != null && _mauiLifecycle.TryGetValue(grp.Key, out var mdt)) mauiEol = mdt;
                bool latestRelevantIsSecurity = false;
                if (cr?.LatestSecuritySdk != null && cr.LatestSdk == cr.LatestSecuritySdk)
                {
                    latestRelevantIsSecurity = true;
                }
                GroupedSdkItems.Add(new ChannelGroup(
                    grp.Key!,
                    grp,
                    rt,
                    first?.SupportPhase,
                    first?.EolDate,
                    mauiEol,
                    cr?.LatestSdk,
                    cr?.LatestRuntime,
                    cr?.LatestSecuritySdk,
                    cr?.LatestSecurityRuntime,
                    isSdkGroup: true,
                    latestRelevantIsSecurity: latestRelevantIsSecurity));
            }
        }
        if (RuntimeItems.Count > 0)
        {
            foreach (var grp in OrderGroups(RuntimeItems.GroupBy(i => i.Channel ?? DeriveChannel(i.Version))))
            {
                var first = grp.FirstOrDefault();
                var rt2 = first?.ReleaseType?.ToUpperInvariant();
                ChannelResolved? cr2 = null;
                _channelCache?.TryGetValue(grp.Key!, out cr2);
                EnsureMauiLifecycleLoaded();
                DateTime? mauiEol2 = null;
                if (_mauiLifecycle != null && grp.Key != null && _mauiLifecycle.TryGetValue(grp.Key, out var mdt2)) mauiEol2 = mdt2;
                bool latestRelevantIsSecurityRt = false;
                if (cr2?.LatestSecurityRuntime != null && cr2.LatestRuntime == cr2.LatestSecurityRuntime)
                {
                    latestRelevantIsSecurityRt = true;
                }
                GroupedRuntimeItems.Add(new ChannelGroup(
                    grp.Key!,
                    grp,
                    rt2,
                    first?.SupportPhase,
                    first?.EolDate,
                    mauiEol2,
                    cr2?.LatestSdk,
                    cr2?.LatestRuntime,
                    cr2?.LatestSecuritySdk,
                    cr2?.LatestSecurityRuntime,
                    isSdkGroup: false,
                    latestRelevantIsSecurity: latestRelevantIsSecurityRt));
            }
        }
        OnPropertyChanged(nameof(GroupedSdkItems));
        OnPropertyChanged(nameof(GroupedRuntimeItems));
    }

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            // Limit to at most once per day using a small persisted stamp file.
            var stamp = GetUpdateStampPath();
            if (File.Exists(stamp))
            {
                try
                {
                    var txt = await File.ReadAllTextAsync(stamp);
                    if (DateTimeOffset.TryParse(txt, out var last) && (DateTimeOffset.UtcNow - last) < TimeSpan.FromDays(1))
                    {
                        return; // already checked within past day
                    }
                }
                catch { }
            }

            var owner = "lextudio";
            var repo = "dotuninstall"; // current repository name
            var client = new GitHubClient(new ProductHeaderValue("dotuninstall"))
            {
                // Set a very small request timeout guard via a CTS when calling below
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            Release? latestRelease = null;
            try
            {
                latestRelease = await client.Repository.Release.GetLatest(owner, repo).WaitAsync(cts.Token);
            }
            catch { return; } // ignore errors/timeouts
            var tag = latestRelease?.TagName;
            if (string.IsNullOrWhiteSpace(tag)) return;
            LatestReleaseTag = tag;
            var current = GetCurrentVersion();
            if (TryParseVersion(tag, out var latest) && TryParseVersion(current, out var currentV))
            {
                if (latest > currentV)
                {
                    HasUpdate = true;
                    UpdateMessage = $"A newer release {latest} is available (current {currentV}).";
                }
            }
            // Persist stamp only after a successful request (even if no update available)
            try { await File.WriteAllTextAsync(stamp, DateTimeOffset.UtcNow.ToString("o")); } catch { }
        }
        catch { }
    }

    // Manual update check invoked from Settings page, bypassing daily throttle
    public async Task ForceCheckForUpdatesAsync()
    {
        try
        {
            var stamp = GetUpdateStampPath();
            if (File.Exists(stamp))
            {
                try { File.Delete(stamp); } catch { }
            }
            await CheckForUpdatesAsync();
            if (!HasUpdate)
            {
                UpdateMessage = $"You are running the latest version ({GetCurrentVersion()}).";
            }
        }
        catch { }
    }

    private static string GetUpdateStampPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dir = Path.Combine(home, ".dotuninstall");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "last_update_check.txt");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "dotuninstall_last_update_check.txt");
        }
    }

    private static string GetCurrentVersion()
    {
        try
        {
            var asm = typeof(MainViewModel).Assembly;
            var info = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault();
            return info?.InformationalVersion ?? "0.0";
        }
        catch { return "0.0"; }
    }

    private static bool TryParseVersion(string? text, out NuGetVersion version)
    {
        version = new NuGetVersion(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.StartsWith('v')) text = text[1..];
        if (!NuGetVersion.TryParse(text, out var parsed) || parsed is null)
        {
            return false;
        }
        version = parsed;
        return true;
    }

    private Task UpdateToolVersionAsync() => Task.CompletedTask; // No-op

    private void DetectElevation()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return; // Scope detection to macOS per requirement
            var euid = Syscall.geteuid();
            if (euid == 0)
            {
                IsElevated = true;
                OriginalUser = Environment.GetEnvironmentVariable("SUDO_USER") ?? Environment.UserName;
                ShowElevationWarning = true; // Only show if actually elevated
                OnPropertyChanged(nameof(CanPerformUninstalls));
            }
            else
            {
                // Not elevated – offer to restart with privileges
                ShowElevationOffer = true;
                OnPropertyChanged(nameof(CanPerformUninstalls));
            }
        }
        catch
        {
            // Swallow any unexpected errors; elevation detection is advisory only
        }
    }

    [RelayCommand]
    private void DismissElevationWarning()
    {
        ShowElevationWarning = false;
    }

    [RelayCommand]
    private async Task ElevateAsync()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return; // Only implemented for macOS right now
            if (IsElevated) return; // Already elevated

            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return;

            // Derive bundle executable (prefer launching the actual binary, not using 'open').
            string launchTarget = exe; // default fallback
            try
            {
                var exeDir = Path.GetDirectoryName(exe) ?? string.Empty;
                var contentsDir = Directory.GetParent(exeDir); // .../Contents/MacOS
                var appRoot = contentsDir?.Parent?.FullName;   // .../AppName.app
                if (!string.IsNullOrWhiteSpace(appRoot) && appRoot.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(appRoot))
                {
                    var candidate = Path.Combine(appRoot, "Contents", "MacOS", Path.GetFileName(exe));
                    if (File.Exists(candidate)) launchTarget = candidate;
                }
            }
            catch { }

            // Escape for shell inside AppleScript (we wrap in double quotes).
            string EscapeForShell(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
            // Preserve original command-line arguments (so hosts like 'dotnet' get their args, e.g. 'run')
            var rawArgs = Environment.GetCommandLineArgs();
            string argsPart = string.Empty;
            if (rawArgs != null && rawArgs.Length > 0)
            {
                var quoted = rawArgs.Select(a => $"\"{EscapeForShell(a)}\"");
                argsPart = " " + string.Join(' ', quoted);
            }
            // Build the shell command (one single string) and then escape it for embedding in AppleScript.
            var shellCmd = $"\"{EscapeForShell(launchTarget)}\"{argsPart}";
            // AppleScript string literal needs backslashes and quotes escaped again
            var appleScriptInner = shellCmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var appleScript = $"do shell script \"{appleScriptInner}\" with administrator privileges";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", appleScript },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                ErrorMessage = "Elevation failed: could not start osascript.";
                ShowElevationOffer = true;
                return;
            }

            // Read output & error while process runs, then evaluate result.
            //string stderr = await proc.StandardError.ReadToEndAsync();
            // string stdout = await proc.StandardOutput.ReadToEndAsync();
            // Wait (with reasonable timeout) for AppleScript to finish prompting.
            proc.WaitForExit(1500);
            if (proc.HasExited)
            {
                var code = proc.ExitCode;
                if (code != 0)
                {
                    ErrorMessage = $"Elevation script error (code {code})";
                    ShowElevationOffer = true; // keep offer visible
                    return;
                }
            }

            await Task.Delay(1500);
            // Exit current (non-elevated) instance so only one UI remains.
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            // Surface any failure through status / error messages without crashing.
            ErrorMessage = "Elevation failed: " + ex.Message;
            ShowElevationOffer = true; // keep banner so user can try again
        }
    }

    [RelayCommand]
    private void DismissElevationOffer()
    {
        ShowElevationOffer = false;
    }
}
