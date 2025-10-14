using DotNetUninstall.Models;
using NUnit.Framework;

namespace DotNetUninstall.Tests;

[TestFixture]
public class SecurityStatusTests
{
    // Helper to build minimal entry
    private static DotnetInstallEntry Entry(string version, string type = "sdk")
        => new(type, type, version, "x64", canUninstall: true, Reason: null);

    [Test]
    public void NewerSecurityPatchMarksOlderAsUnpatched()
    {
        // Simulate metadata: latest security SDK 8.0.21
        var metaLatestSecurity = "8.0.21";
        var installed = Entry("8.0.20");

        // Emulate logic fragment from MainViewModel (condensed)
        var status = Compute(installed.Version, metaLatestSecurity, isSecurity:false);
        Assert.That(status, Is.EqualTo(SecurityStatus.Unpatched));
    }

    [Test]
    public void ExactSecurityPatchIsSecurityPatch()
    {
        var latest = "8.0.21";
        var installed = Entry("8.0.21");
        var status = Compute(installed.Version, latest, isSecurity:true);
        Assert.That(status, Is.EqualTo(SecurityStatus.SecurityPatch));
    }

    [Test]
    public void NewerPreviewBeyondSecurityIsPatched()
    {
        var latestSecurity = "8.0.21";
        var installed = Entry("8.0.22-preview.1");
        var status = Compute(installed.Version, latestSecurity, isSecurity:false);
        Assert.That(status, Is.EqualTo(SecurityStatus.Patched));
    }

    private static SecurityStatus Compute(string installedVersion, string? latestSecurity, bool isSecurity)
        => SecurityClassificationHelper.Classify(installedVersion, latestSecurity, isSecurity).status;
}
