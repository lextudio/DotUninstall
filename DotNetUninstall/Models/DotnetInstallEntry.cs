namespace DotNetUninstall.Models;

public partial record DotnetInstallEntry(
    string Type,          // sdk | runtime
    string Id,            // identifier category (sdk/runtime)
    string Version,       // version string
    string Architecture,  // arm64/x64/etc.
    bool CanUninstall,
    string? Reason        // null if uninstallable, else reason message
);

// Extended metadata (populated after initial discovery via Microsoft release metadata service)
public partial record DotnetInstallEntry
{
    public string? Channel { get; init; }          // e.g. "8.0", "9.0"
    public string? SupportPhase { get; init; }     // lts | sts | preview | eol | ga | unknown
    public bool IsPreview { get; init; }
    public bool IsOutOfSupport { get; init; }
    public string? ReleaseType { get; init; }       // lts | sts
    public string? PreviewKind { get; init; }       // preview | rc | ga
    public int? PreviewNumber { get; init; }         // e.g. 3 for -preview.3 or -rc.3
    public bool IsSecurityUpdate { get; init; }
    public DateTime? EolDate { get; init; }         // official end of life (channel) if provided
    public bool IsGa => PreviewKind == "ga";        // convenience flag
    public string? PreviewKindDisplay => PreviewKind switch
    {
        "preview" => "Preview",
        "rc" => "RC",
        "ga" => "GA",
        _ => PreviewKind
    };
    // Internal uninstall command (not shown in UI) when using embedded logic instead of external tool
    public string? UninstallCommand { get; init; }
    public string? DisplayName { get; init; }
    public string? SubType { get; init; }  // more granular runtime flavor
}
