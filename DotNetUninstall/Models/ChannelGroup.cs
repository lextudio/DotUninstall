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
    public string? EolDateValue => EolDate.HasValue ? EolDate.Value.ToString("yyyy-MM-dd") : null; // For two-segment badge (label|value)
    public string LifecycleState { get; }  // eol | expiring | supported
    public bool IsExpiringSoon => LifecycleState == "expiring";
    public bool IsEol => LifecycleState == "eol";
    // MAUI-specific lifecycle (scraped separately)
    public DateTime? MauiEolDate { get; }
    public string? MauiEolBadge => MauiEolDate.HasValue ? $"MAUI EOL:{MauiEolDate:yyyy-MM-dd}" : null;
    public string? MauiEolDateValue => MauiEolDate.HasValue ? MauiEolDate.Value.ToString("yyyy-MM-dd") : null;
    public string? MauiEolInfoUrl => MauiEolDate.HasValue ? "https://dotnet.microsoft.com/platform/support/policy/maui" : null;
    public string? LatestSdkVersion { get; }
    public string? LatestRuntimeVersion { get; }
    public string? LatestSecuritySdkVersion { get; }
    public string? LatestSecurityRuntimeVersion { get; }
    // Security update presence no longer surfaced as separate badge; we track only if latest relevant is security.
    public bool HasSecurityUpdate => false; // retained for backwards compatibility (always false now)
    public bool IsLatestSecuritySdkInstalled { get; }
    public bool IsLatestSecurityRuntimeInstalled { get; }
    public bool IsLatestSdkInstalled { get; }
    public bool IsLatestRuntimeInstalled { get; }
    public bool IsSdkGroup { get; }
    public string ChannelDownloadUrl { get; }
    // Relevant (tab-specific) latest version properties
    public string? LatestRelevantVersion => IsSdkGroup ? LatestSdkVersion : LatestRuntimeVersion;
    // Relevant latest security version (SDK vs Runtime depending on tab)
    public string? LatestSecurityRelevantVersion => IsSdkGroup ? LatestSecuritySdkVersion : LatestSecurityRuntimeVersion;
    public bool IsLatestRelevantInstalled => IsSdkGroup ? IsLatestSdkInstalled : IsLatestRuntimeInstalled;
    public bool ShowLatestRelevantMissing => !string.IsNullOrWhiteSpace(LatestRelevantVersion) && !IsLatestRelevantInstalled;
    public string? LatestRelevantLabel => LatestRelevantVersion is null ? null : $"latest:{LatestRelevantVersion}";
    public string? SecurityUpdateTooltip => null; // deprecated
    // Indicates whether the latest relevant (SDK or Runtime) version is itself a security release
    public bool LatestRelevantIsSecurity { get; }
    // Distinguish missing latest into security vs normal for styling
    public bool ShowLatestRelevantMissingSecurity => ShowLatestRelevantMissing && LatestRelevantIsSecurity;
    public bool ShowLatestRelevantMissingNormal => ShowLatestRelevantMissing && !LatestRelevantIsSecurity;

    // Documentation links
    public string? ReleaseTypeInfoUrl => ReleaseType is null ? null : "https://learn.microsoft.com/lifecycle/faq/dotnet-core";
    public string? SupportPhaseInfoUrl => SupportPhase is null ? null : "https://dotnet.microsoft.com/platform/support/policy/dotnet-core";
    public string? EolInfoUrl => EolDate.HasValue ? "https://learn.microsoft.com/lifecycle/products/microsoft-net-and-net-core" : null;

    // Backwards-compat (not used in UI after refinement)
    public bool ShowLatestMissing => ShowLatestRelevantMissing;

    public ChannelGroup(
        string channel,
        IEnumerable<DotnetInstallEntry> items,
        string? releaseType,
        string? supportPhase,
        DateTime? eolDate,
        DateTime? mauiEolDate,
        string? latestSdkVersion,
        string? latestRuntimeVersion,
        string? latestSecuritySdkVersion,
        string? latestSecurityRuntimeVersion,
        bool isSdkGroup,
        bool latestRelevantIsSecurity)
    {
        Channel = string.IsNullOrWhiteSpace(channel) ? "Other" : channel;

        // Order items by semantic version descending (stable > prerelease when equal core, higher patch first)
        var ordered = items
            .Select(i =>
            {
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
        MauiEolDate = mauiEolDate;
        LatestSdkVersion = latestSdkVersion;
        LatestRuntimeVersion = latestRuntimeVersion;
        LatestSecuritySdkVersion = latestSecuritySdkVersion;
        LatestSecurityRuntimeVersion = latestSecurityRuntimeVersion;
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
        if (!string.IsNullOrWhiteSpace(LatestSecuritySdkVersion))
        {
            IsLatestSecuritySdkInstalled = Items.Any(i => i.Type == "sdk" && string.Equals(i.Version, LatestSecuritySdkVersion, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(LatestSecurityRuntimeVersion))
        {
            IsLatestSecurityRuntimeInstalled = Items.Any(i => i.Type == "runtime" && string.Equals(i.Version, LatestSecurityRuntimeVersion, StringComparison.OrdinalIgnoreCase));
        }
        ChannelDownloadUrl = $"https://dotnet.microsoft.com/download/dotnet/{Channel}"; // Generic channel landing page
        LifecycleState = ComputeLifecycleState();
        LatestRelevantIsSecurity = latestRelevantIsSecurity;
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
