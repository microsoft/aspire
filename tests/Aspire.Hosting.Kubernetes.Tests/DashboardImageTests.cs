// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Kubernetes.Tests;

public class DashboardImageTests
{
    [Theory]
    // Informational version (SemVer with prerelease + build metadata) is preferred and reduced to major.minor.
    [InlineData("13.5.0-preview.1.25111.1+ad18db0213e9db8209bca0feb83fc801f34634f5", "13.5.0.0", "13.5")]
    [InlineData("13.5.0", "13.5.0.0", "13.5")]
    [InlineData("14.0.0-dev", "14.0.0.0", "14.0")]
    // Falls back to the 4-part assembly version when the informational version is missing.
    [InlineData(null, "13.5.0.0", "13.5")]
    [InlineData("", "9.2.1.0", "9.2")]
    // Falls back to the assembly version when the informational version is not parseable.
    [InlineData("not-a-version", "10.3.0.0", "10.3")]
    public void ResolveTag_ReturnsMajorMinor(string? informationalVersion, string? assemblyVersion, string expected)
    {
        Assert.Equal(expected, DashboardImage.ResolveTag(informationalVersion, assemblyVersion));
    }

    [Theory]
    // When no version is available at all, preserve the historical ":latest" behavior rather than emitting an invalid tag.
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("garbage", "also-garbage")]
    public void ResolveTag_WithoutParseableVersion_FallsBackToLatest(string? informationalVersion, string? assemblyVersion)
    {
        Assert.Equal("latest", DashboardImage.ResolveTag(informationalVersion, assemblyVersion));
    }

    [Fact]
    public void ResolveTag_FromRunningAssembly_MatchesPinnedImageName()
    {
        // The running assembly always carries a version, so a real tag (never "latest") is produced.
        var tag = DashboardImage.ResolveTag();

        Assert.NotEqual("latest", tag);
        Assert.Matches(@"^\d+\.\d+$", tag);
    }
}
