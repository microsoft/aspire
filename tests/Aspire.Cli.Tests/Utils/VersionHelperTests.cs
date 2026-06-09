// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class VersionHelperTests
{
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

    [Theory]
    [InlineData("daily")]
    [InlineData("staging")]
    [InlineData("stable")]
    public void TryGetCurrentCliVersionMatch_WithNamedChannel_ReturnsCurrentCliVersion(string channelName)
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
            channelName: channelName,
            hasPrHives: false);

        Assert.True(result);
        Assert.Equal(cliVersion, match);
    }

    [Fact]
    public void TryGetCurrentCliVersionMatch_WithNamedChannelAndNoExactMatch_ReturnsFalse()
    {
        var candidates = new[]
        {
            "99.0.0",
            "98.0.0",
        };

        var result = VersionHelper.TryGetCurrentCliVersionMatch(
            candidates,
            version => version,
            out var match,
            channelName: "daily",
            hasPrHives: false);

        Assert.False(result);
        Assert.Null(match);
    }

    [Fact]
    public void TryGetCurrentCliVersionMatch_WithNoChannelAndNoPrHives_ReturnsFalse()
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
            hasPrHives: false);

        Assert.False(result);
        Assert.Null(match);
    }

    [Theory]
    [InlineData("pr-16820", true)]
    [InlineData("run-25422767716", true)]
    [InlineData("local", true)]
    [InlineData("LOCAL", true)]
    [InlineData("stable", false)]
    [InlineData("daily", false)]
    [InlineData("staging", false)]
    [InlineData(null, false)]
    public void IsLocalBuildChannel_RecognizesAllLocalChannelForms(string? channelName, bool expected)
    {
        Assert.Equal(expected, VersionHelper.IsLocalBuildChannel(channelName));
    }
}
