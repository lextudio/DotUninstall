using System.Collections.ObjectModel;

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
        Items = new ObservableCollection<DotnetInstallEntry>(items);
        ReleaseType = releaseType;
        SupportPhase = supportPhase;
        EolDate = eolDate;
        LifecycleState = ComputeLifecycleState();
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
