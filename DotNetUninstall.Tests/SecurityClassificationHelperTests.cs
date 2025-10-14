using DotNetUninstall.Models;
using DotNetUninstall.Presentation;
using NUnit.Framework;

namespace DotNetUninstall.Tests;

[TestFixture]
public class SecurityClassificationHelperTests
{
    [Test]
    public void NoLatestSecurityAndNotSecurityPatch_ReturnsNone()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("8.0.1", null, false);
        Assert.That(status, Is.EqualTo(SecurityStatus.None));
        Assert.That(tip, Is.Null);
    }

    [Test]
    public void NoLatestSecurityButIsSecurityPatch_ReturnsSecurityPatch()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("8.0.1", null, true);
        Assert.That(status, Is.EqualTo(SecurityStatus.SecurityPatch));
        Assert.That(tip, Is.Not.Null);
    }

    [Test]
    public void InstalledOlderThanLatestSecurity_ReturnsUnpatched()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("8.0.20", "8.0.21", false);
        Assert.That(status, Is.EqualTo(SecurityStatus.Unpatched));
        StringAssert.Contains("8.0.21", tip);
    }

    [Test]
    public void InstalledEqualsLatestSecurity_ReturnsSecurityPatch()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("8.0.21", "8.0.21", true);
        Assert.That(status, Is.EqualTo(SecurityStatus.SecurityPatch));
    }

    [Test]
    public void InstalledGreaterThanLatestSecurityPreview_ReturnsPatched()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("8.0.22-preview.1", "8.0.21", false);
        Assert.That(status, Is.EqualTo(SecurityStatus.Patched));
    }

    [Test]
    public void InvalidVersionParsingFallsBack()
    {
        var (status, tip) = SecurityClassificationHelper.Classify("not-a-version", "8.0.21", false);
        // Parsing fails -> remains None
        Assert.That(status, Is.EqualTo(SecurityStatus.None));
    }

    [Test]
    public void InstalledGreaterThanLatestSecurity_GAReleaseBeyondPatch_TreatedAsPatched()
    {
        var (status, _) = SecurityClassificationHelper.Classify("8.0.22", "8.0.21", false);
        Assert.That(status, Is.EqualTo(SecurityStatus.Patched));
    }

    [Test]
    public void InstalledEqualsLatestSecurity_NonSecurityFlagStillSecurityPatch()
    {
        // Even if isSecurityPatch flag is false but versions equal, classification should be SecurityPatch
        var (status, _) = SecurityClassificationHelper.Classify("8.0.21", "8.0.21", false);
        Assert.That(status, Is.EqualTo(SecurityStatus.SecurityPatch));
    }

    [Test]
    public void NoLatestSecurity_IsSecurityPatchFlagTrue_ClassifiesSecurityPatch()
    {
        var (status, _) = SecurityClassificationHelper.Classify("9.0.0", null, true);
        Assert.That(status, Is.EqualTo(SecurityStatus.SecurityPatch));
    }

    [Test]
    public void NoLatestSecurity_IsSecurityPatchFlagFalse_ClassifiesNone()
    {
        var (status, _) = SecurityClassificationHelper.Classify("9.0.0", null, false);
        Assert.That(status, Is.EqualTo(SecurityStatus.None));
    }
}
