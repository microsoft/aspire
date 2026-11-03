// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Projects;

public class GuestAppHostProjectSkewTests
{
    [Theory]
    [InlineData("13.1.0", "13.1.0", false)]
    [InlineData("13.1.0-preview.1.26218.1", "13.1.0-preview.1.26218.1", false)]
    // Build metadata (everything after '+') is SemVer-spec ignored for precedence.
    [InlineData("13.1.0-preview.1.26218.1+abc", "13.1.0-preview.1.26218.1+def", false)]
    // Issue #16709 reproduction: same M.M.P prerelease tag with different daily build numbers
    // is detected as skew (this was the exact failure case).
    [InlineData("13.1.0-preview.1.26218.1", "13.1.0-preview.1.26227.1", true)]
    [InlineData("13.1.0-preview.1.26227.1", "13.1.0-preview.1.26218.1", false)]
    [InlineData("13.1.0", "13.1.0-preview.1", false)]
    [InlineData("13.1.0-preview.1", "13.1.0", true)]
    [InlineData("13.1.0", "13.2.0", true)]
    [InlineData("13.1.0", "14.0.0", true)]
    [InlineData("13.1.0", "13.1.1", true)]
    [InlineData("13.2.0", "13.1.0", false)]
    [InlineData("14.0.0", "13.1.0", false)]
    [InlineData("13.1.1", "13.1.0", false)]
    [InlineData("13.6.0-pr.19847.g8be64f3a", "13.5.4", false)]
    [InlineData("13.6.0-pr.19847.g8be64f3a", "13.6.0", true)]
    [InlineData("13.5.4", "13.5.4+abc123", false)]
    [InlineData("13.5.4+abc123", "13.5.4", false)]
    public void ShouldWarnAboutCliSdkVersionSkew_WarnsOnlyWhenCliIsOlder(string cli, string sdk, bool expected)
    {
        var result = GuestAppHostProject.ShouldWarnAboutCliSdkVersionSkew(cli, sdk);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("not-a-version", "also-not-a-version-but-different", true)]
    [InlineData("identical", "identical", false)]
    [InlineData("IDENTICAL", "identical", false)]
    [InlineData("not-a-version", "13.5.4", true)]
    [InlineData("13.5.4", "not-a-version", true)]
    public void ShouldWarnAboutCliSdkVersionSkew_FallsBackToStringCompareForUnparseable(string cli, string sdk, bool expected)
    {
        Assert.Equal(expected, GuestAppHostProject.ShouldWarnAboutCliSdkVersionSkew(cli, sdk));
    }

    [Theory]
    [InlineData("13.1.0+build.5", "13.1.0")]
    [InlineData("13.1.0-preview.1+sha.abc123", "13.1.0-preview.1")]
    [InlineData("13.1.0", "13.1.0")]
    public void NormalizeVersion_StripsBuildSuffix(string input, string expected)
    {
        Assert.Equal(expected, GuestAppHostProject.NormalizeVersion(input));
    }
}
