// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class VersionHelperTests
{
    [Theory]
    [InlineData("local")]
    [InlineData("pr-123")]
    [InlineData("run-123")]
    public void IsLocalBuildChannel_WithLocalBuildChannels_ReturnsTrue(string channelName)
    {
        Assert.True(VersionHelper.IsLocalBuildChannel(channelName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("daily")]
    [InlineData("stable")]
    public void IsLocalBuildChannel_WithNonLocalBuildChannels_ReturnsFalse(string? channelName)
    {
        Assert.False(VersionHelper.IsLocalBuildChannel(channelName));
    }

    [Fact]
    public void TryGetCurrentCliVersionMatch_WithPrHivesAndNoChannel_ReturnsCurrentCliVersion()
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var candidates = new[]
        {
            "99.0.0",
            cliVersion,
        };

        var result = VersionHelper.TryGetCurrentCliVersionMatch(
            candidates,
            version => version,
            out var match,
            channelName: null,
            hasPrHives: true);

        Assert.True(result);
        Assert.Equal(cliVersion, match);
    }

    [Fact]
    public void TryGetCurrentCliVersionMatch_WithLocalChannelAndNoPrHives_ReturnsCurrentCliVersion()
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();
        var candidates = new[]
        {
            "99.0.0",
            cliVersion,
        };

        var result = VersionHelper.TryGetCurrentCliVersionMatch(
            candidates,
            version => version,
            out var match,
            channelName: "local",
            hasPrHives: false);

        Assert.True(result);
        Assert.Equal(cliVersion, match);
    }
}
