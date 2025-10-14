using DotNetUninstall.Models;
using NuGet.Versioning;

namespace DotNetUninstall.Core;

/// <summary>
/// Provides helper methods to classify security status for installed .NET SDK/runtime entries.
/// 
/// Classification rules (ordered by evaluation):
/// 1. If both installed and latest security versions parse semantically and latestSecurityVersion exists:
///    a. installed < latestSecurity  => <see cref="SecurityStatus.Unpatched"/> (missing security fixes)
///    b. installed == latestSecurity => <see cref="SecurityStatus.SecurityPatch"/> (is the security patch itself)
///    c. installed  > latestSecurity => If flag <paramref name="isSecurityPatch"/> true classify as <see cref="SecurityStatus.SecurityPatch"/>, else <see cref="SecurityStatus.Patched"/> (contains all fixes, e.g. newer preview)
/// 2. If no latest security version is known but the installed entry is flagged security => <see cref="SecurityStatus.SecurityPatch"/>.
/// 3. Otherwise => <see cref="SecurityStatus.None"/> (no security relevance detected).
/// 
/// Tooltip text mirrors these states and highlights the latest known security version when appropriate.
/// </summary>
public static class SecurityClassificationHelper
{
    /// <summary>
    /// Classifies security status for an installed version based on the latest security patch version and whether the installed version itself is marked security.
    /// </summary>
    public static (SecurityStatus status, string? tooltip) Classify(string installedVersion, string? latestSecurityVersion, bool isSecurityPatch)
    {
        SecurityStatus status = SecurityStatus.None;
        string? tooltip = null;

        if (!string.IsNullOrWhiteSpace(latestSecurityVersion)
            && NuGetVersion.TryParse(installedVersion, out var instNv)
            && NuGetVersion.TryParse(latestSecurityVersion, out var latestSecNv))
        {
            if (instNv < latestSecNv)
            {
                status = SecurityStatus.Unpatched;
                tooltip = $"Security release {latestSecurityVersion} is available; this version lacks those fixes.";
            }
            else if (instNv == latestSecNv)
            {
                status = SecurityStatus.SecurityPatch;
                tooltip = $"This is the latest security patch ({latestSecurityVersion}).";
            }
            else // inst > latest security (likely preview) -> has all fixes
            {
                status = isSecurityPatch ? SecurityStatus.SecurityPatch : SecurityStatus.Patched;
                tooltip = $"Includes all security fixes up to {latestSecurityVersion}.";
            }
        }
        else if (isSecurityPatch)
        {
            status = SecurityStatus.SecurityPatch;
            tooltip = "Security patch release.";
        }
        return (status, tooltip);
    }
}
