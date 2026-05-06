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

        Assert.Equal("pr", ctx.Channel);
        Assert.Equal(16798, ctx.PrNumber);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("pr")]
    public void Channel_Getter_ReturnsExactValuePassedToConstructor(string channel)
    {
        var ctx = CreateContext(channel: channel, prNumber: channel == "pr" ? 1 : null);

        Assert.Equal(channel, ctx.Channel);
    }
}
