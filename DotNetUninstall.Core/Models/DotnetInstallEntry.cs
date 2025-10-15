namespace DotNetUninstall.Models;

/// <summary>
/// Represents an installed .NET component (SDK or runtime) discovered on the local machine.
/// The base positional portion captures immutable identity & uninstall characteristics; the partial
/// extension adds enriched metadata sourced from online release metadata and classification helpers.
/// </summary>
/// <param name="Type">Component category: <c>sdk</c> or <c>runtime</c>.</param>
/// <param name="Id">Identifier grouping (mirrors Type for now; reserved for future finer grained ids).</param>
/// <param name="Version">Raw display version string.</param>
/// <param name="Architecture">CPU architecture (x64, arm64, etc.).</param>
/// <param name="CanUninstall">Indicates whether uninstall is supported via the embedded mechanism.</param>
/// <param name="Reason">Non-null when uninstall is blocked, containing explanation.</param>
public partial record DotnetInstallEntry(
    string Type,
    string Id,
    string Version,
    string Architecture,
    bool CanUninstall,
    string? Reason
);

public partial record DotnetInstallEntry
{
    /// <summary>Major.minor channel the entry belongs to (e.g. 8.0, 9.0) if derivable from metadata.</summary>
    public string? Channel { get; init; }
    /// <summary>Lifecycle phase (preview, active, maintenance, eol, go-live etc.) if supplied.</summary>
    public string? SupportPhase { get; init; }
    /// <summary>True if version contains a prerelease label.</summary>
    public bool IsPreview { get; init; }
    /// <summary>True when channel or entry is out of support (derived from metadata).</summary>
    public bool IsOutOfSupport { get; init; }
    /// <summary>Channel release type (lts, sts) when known.</summary>
    public string? ReleaseType { get; init; }
    /// <summary>Normalized prerelease kind (preview | rc | ga).</summary>
    public string? PreviewKind { get; init; }
    /// <summary>Numeric prerelease iteration extracted from version (e.g. 3 for -preview.3).</summary>
    public int? PreviewNumber { get; init; }
    /// <summary>Flag from metadata indicating this particular release was published as a security update.</summary>
    public bool IsSecurityUpdate { get; init; }
    /// <summary>Channel end-of-life date if provided.</summary>
    public DateTime? EolDate { get; init; }
    public bool IsGa => PreviewKind == "ga";
    public string? PreviewKindDisplay => PreviewKind switch
    {
        "preview" => "Preview",
        "rc" => "RC",
        "ga" => "GA",
        _ => PreviewKind
    };
    public string? UninstallCommand { get; init; }
    public string? DisplayName { get; init; }
    public string? SubType { get; init; }
    /// <summary>Computed security classification determined via <see cref="SecurityClassificationHelper"/>.</summary>
    public SecurityStatus SecurityStatus { get; init; } = SecurityStatus.None;
    /// <summary>Convenience flag: entry is itself a security patch release.</summary>
    public bool IsSecurityPatch => SecurityStatus == SecurityStatus.SecurityPatch;

    /// <summary>Tooltip describing reasoning behind security classification (may be null).</summary>
    public string? SecurityTooltip { get; init; }

    /// <summary>Date this specific release was published (from release metadata), in UTC date component.</summary>
    public DateTime? ReleaseDate { get; init; }
    /// <summary>Convenience string (yyyy-MM-dd) for two-segment badge value; null when date unknown.</summary>
    public string? ReleaseDateValue => ReleaseDate?.ToString("yyyy-MM-dd");
    /// <summary>Release notes URL if provided in the metadata.</summary>
    public string? ReleaseNotesUrl { get; init; }

    /// <summary>
    /// Computed stage display string used by UI badges. Simplifies XAML by removing custom ValueContent.
    /// Rules:
    ///  - If <see cref="IsGa"/> or PreviewKind == "ga": returns "GA".
    ///  - Else if PreviewKindDisplay plus optional PreviewNumber => e.g. "Preview 3" or "RC 1".
    ///  - Falls back to PreviewKindDisplay if number absent; returns null when PreviewKind unknown.
    /// </summary>
    public string? StageDisplay
    {
        get
        {
            if (IsGa) return "GA";
            if (string.IsNullOrEmpty(PreviewKindDisplay)) return null;
            if (PreviewNumber is int n and > 0)
            {
                return $"{PreviewKindDisplay} {n}";
            }
            return PreviewKindDisplay;
        }
    }
}

/// <summary>
/// Represents the security posture of an installed version relative to known security releases.
/// </summary>
public enum SecurityStatus
{
    /// <summary>No security relevance detected for the installed version.</summary>
    None = 0,
    /// <summary>The installed version is itself a published security patch.</summary>
    SecurityPatch = 1,
    /// <summary>Installed version includes all fixes from the latest known security patch (e.g. newer preview or GA beyond patch).</summary>
    Patched = 2,
    /// <summary>A newer security patch exists and this installed version should be updated (legacy state; may merge with Unpatched).</summary>
    UpdateNeeded = 3,
    /// <summary>Installed version is older than latest security patch and missing those fixes.</summary>
    Unpatched = 4
}
