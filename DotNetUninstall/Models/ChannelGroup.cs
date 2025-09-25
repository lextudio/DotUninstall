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
    public string? EolDisplay => EolDate.HasValue ? $"EOL {EolDate:yyyy-MM-dd}" : null;
    public string LifecycleState { get; }  // eol | expiring | supported
    public bool IsExpiringSoon => LifecycleState == "expiring";
    public bool IsEol => LifecycleState == "eol";

    public ChannelGroup(string channel, IEnumerable<DotnetInstallEntry> items, string? releaseType, string? supportPhase, DateTime? eolDate)
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
