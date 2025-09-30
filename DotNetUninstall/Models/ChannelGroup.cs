using System.Collections.ObjectModel;
using NuGet.Versioning;

namespace DotNetUninstall.Models;

public sealed class ChannelGroup
{
    public string Channel { get; }
    public ObservableCollection<DotnetInstallEntry> Items { get; }
    public string? ReleaseType { get; }    // lts | sts
    public string? SupportPhase { get; }   // active | maintenance | eol | preview | go-live
    public DateTime? EolDate { get; }
    public string? EolDisplay => EolDate.HasValue ? $"End of life {EolDate:yyyy-MM-dd}" : null;
    public string? EolBadge => EolDate.HasValue ? $"EOL:{EolDate:yyyy-MM-dd}" : null;
    public string LifecycleState { get; }  // eol | expiring | supported
    public bool IsExpiringSoon => LifecycleState == "expiring";
    public bool IsEol => LifecycleState == "eol";
    public string? LatestSdkVersion { get; }
    public string? LatestRuntimeVersion { get; }
    public bool IsLatestSdkInstalled { get; }
    public bool IsLatestRuntimeInstalled { get; }
    public bool IsSdkGroup { get; }
    public string ChannelDownloadUrl { get; }
    // Relevant (tab-specific) latest version properties
    public string? LatestRelevantVersion => IsSdkGroup ? LatestSdkVersion : LatestRuntimeVersion;
    public bool IsLatestRelevantInstalled => IsSdkGroup ? IsLatestSdkInstalled : IsLatestRuntimeInstalled;
    public bool ShowLatestRelevantMissing => !string.IsNullOrWhiteSpace(LatestRelevantVersion) && !IsLatestRelevantInstalled;
    public string? LatestRelevantSummary => ShowLatestRelevantMissing ? $"Latest available: {LatestRelevantVersion}" : null;
    public string? LatestRelevantLabel => LatestRelevantVersion is null ? null : $"latest:{LatestRelevantVersion}";

    // Backwards-compat (not used in UI after refinement)
    public bool ShowLatestMissing => ShowLatestRelevantMissing;
    public string? LatestMissingSummary => LatestRelevantSummary;

    public ChannelGroup(
        string channel,
        IEnumerable<DotnetInstallEntry> items,
        string? releaseType,
        string? supportPhase,
        DateTime? eolDate,
        string? latestSdkVersion,
        string? latestRuntimeVersion,
        bool isSdkGroup)
    {
        Channel = string.IsNullOrWhiteSpace(channel) ? "Other" : channel;

        // Order items by semantic version descending (stable > prerelease when equal core, higher patch first)
        var ordered = items
            .Select(i => {
                NuGetVersion? v = NuGetVersion.TryParse(i.Version, out var parsed) ? parsed : null;
                return (entry: i, version: v);
            })
            // Ascending semantic version order (older first, newest last); unparsable go last
            .OrderBy(t => t.version, new NuGetVersionDescComparer())
            .ThenBy(t => t.entry.Version, StringComparer.OrdinalIgnoreCase) // stable deterministic secondary
            .Select(t => t.entry);

        Items = new ObservableCollection<DotnetInstallEntry>(ordered);
        ReleaseType = releaseType;
        SupportPhase = supportPhase;
        EolDate = eolDate;
        LatestSdkVersion = latestSdkVersion;
        LatestRuntimeVersion = latestRuntimeVersion;
        IsSdkGroup = isSdkGroup;
        // Determine if latest versions are installed (simple string match)
        if (!string.IsNullOrWhiteSpace(LatestSdkVersion))
        {
            IsLatestSdkInstalled = Items.Any(i => i.Type == "sdk" && string.Equals(i.Version, LatestSdkVersion, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(LatestRuntimeVersion))
        {
            IsLatestRuntimeInstalled = Items.Any(i => i.Type == "runtime" && string.Equals(i.Version, LatestRuntimeVersion, StringComparison.OrdinalIgnoreCase));
        }
        ChannelDownloadUrl = $"https://dotnet.microsoft.com/download/dotnet/{Channel}"; // Generic channel landing page
        LifecycleState = ComputeLifecycleState();
    }

    private sealed class NuGetVersionDescComparer : IComparer<NuGetVersion?>
    {
        public int Compare(NuGetVersion? x, NuGetVersion? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;  // null/unparsable versions last
            if (y is null) return -1;
            return y.CompareTo(x);    // natural descending
        }
    }

    private string ComputeLifecycleState()
    {
        var today = DateTime.UtcNow.Date;
        if (SupportPhase == "eol" || (EolDate.HasValue && EolDate.Value < today)) return "eol";
        if (EolDate.HasValue)
        {
            var days = (EolDate.Value - today).TotalDays;
            if (days <= 90) return "expiring"; // threshold window
        }
        return "supported";
    }
}
