// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests;

public class CliExecutionContextTests
{
    private static CliExecutionContext CreateContext(string channel = "daily", int? prNumber = null)
    {
        var workingDir = new DirectoryInfo(AppContext.BaseDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));
        return new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log", channel: channel, prNumber: prNumber);
    }

    [Fact]
    public void Channel_DefaultsToDaily_WhenNotSpecified()
    {
        var workingDir = new DirectoryInfo(AppContext.BaseDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));

        var ctx = new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log");

        Assert.Equal("daily", ctx.Channel);
        Assert.Null(ctx.PrNumber);
    }

    [Fact]
    public void Channel_AndPrNumber_AreReadable_WhenConstructedWithNullPrNumber()
    {
        var ctx = CreateContext(channel: "stable", prNumber: null);

        Assert.Equal("stable", ctx.Channel);
        Assert.Null(ctx.PrNumber);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    public void PrNumber_IsNull_ForNonPrChannels(string channel)
    {
        var ctx = CreateContext(channel: channel, prNumber: null);

        Assert.Equal(channel, ctx.Channel);
        Assert.Null(ctx.PrNumber);
    }

    [Fact]
    public void PrNumber_IsSet_WhenChannelIsPr()
    {
        var ctx = CreateContext(channel: "pr", prNumber: 16798);

        // Option-(a) Channel resolution: `pr` + PrNumber → `pr-<N>` (the per-PR hive label
        // PackagingService creates). The raw build-time value is exposed separately via
        // IdentityChannel.
        Assert.Equal("pr-16798", ctx.Channel);
        Assert.Equal("pr", ctx.IdentityChannel);
        Assert.Equal(16798, ctx.PrNumber);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    public void Channel_Getter_ReturnsExactValuePassedToConstructor(string channel)
    {
        // For non-PR channels, Channel is the constructor value verbatim. The pr → pr-<N>
        // transformation is covered separately by Channel_PrChannelWithPrNumber_ReturnsPrDashN.
        var ctx = CreateContext(channel: channel, prNumber: null);

        Assert.Equal(channel, ctx.Channel);
    }

    // Spec: option-(a) Channel resolution. CliExecutionContext.Channel returns `pr-<N>`
    // when constructed with channel="pr" AND prNumber.HasValue. Anything else returns
    // the constructor-provided channel verbatim. The raw build-time value is preserved
    // on IdentityChannel for callers that need the build taxonomy.

    [Fact]
    public void Channel_PrChannelWithPrNumber_ReturnsPrDashN()
    {
        var ctx = CreateContext(channel: "pr", prNumber: 12345);

        Assert.Equal("pr-12345", ctx.Channel);
        Assert.Equal("pr", ctx.IdentityChannel);
        Assert.Equal(12345, ctx.PrNumber);
    }

    [Fact]
    public void Channel_PrChannelWithoutPrNumber_ReturnsPr()
    {
        // Degraded but consistent: there is no <N> to resolve, so the raw `pr` value
        // is returned. Reseed sites then propagate `pr` downstream — the alternative
        // (throwing or returning empty) would break bootstrap on PR builds where
        // ParsePrNumber happened to fail.
        var ctx = CreateContext(channel: "pr", prNumber: null);

        Assert.Equal("pr", ctx.Channel);
        Assert.Equal("pr", ctx.IdentityChannel);
        Assert.Null(ctx.PrNumber);
    }

    [Fact]
    public void Channel_StableChannelWithPrNumber_ReturnsStable()
    {
        // PrNumber is ignored for non-pr channels — the trigger for the pr-<N> shape is
        // the IdentityChannel value, not the presence of PrNumber.
        var ctx = CreateContext(channel: "stable", prNumber: 99);

        Assert.Equal("stable", ctx.Channel);
        Assert.Equal("stable", ctx.IdentityChannel);
        Assert.Equal(99, ctx.PrNumber);
    }

    [Fact]
    public void Channel_DailyWithoutPrNumber_ReturnsDaily()
    {
        var ctx = CreateContext(channel: "daily", prNumber: null);

        Assert.Equal("daily", ctx.Channel);
        Assert.Equal("daily", ctx.IdentityChannel);
        Assert.Null(ctx.PrNumber);
    }
}
