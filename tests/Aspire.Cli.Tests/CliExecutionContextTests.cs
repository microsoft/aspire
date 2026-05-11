// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests;

public class CliExecutionContextTests
{
    private static CliExecutionContext CreateContext(string channel = "daily")
    {
        var workingDir = new DirectoryInfo(AppContext.BaseDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));
        return new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log", channel: channel);
    }

    [Fact]
    public void Channel_DefaultsToLocal_WhenNotSpecified()
    {
        var workingDir = new DirectoryInfo(AppContext.BaseDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "cache"));
        var sdksDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "sdks"));
        var logsDir = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "logs"));

        var ctx = new CliExecutionContext(workingDir, hivesDir, cacheDir, sdksDir, logsDir, "test.log");

        Assert.Equal("local", ctx.Channel);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("staging")]
    [InlineData("daily")]
    [InlineData("local")]
    [InlineData("pr-1")]
    [InlineData("pr-16798")]
    public void Channel_Getter_ReturnsExactValuePassedToConstructor(string channel)
    {
        // CliExecutionContext is now a thin holder — the resolved hive label is baked
        // into the AspireCliChannel assembly metadata at build time (CI emits `pr-<N>`
        // directly for PR builds), so the context returns whatever string the caller
        // hands it. Validation of the channel SHAPE lives in IdentityChannelReader.
        var ctx = CreateContext(channel: channel);

        Assert.Equal(channel, ctx.Channel);
    }
}
