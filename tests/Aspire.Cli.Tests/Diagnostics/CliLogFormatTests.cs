// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Diagnostics;

public class CliLogFormatTests
{
    [Fact]
    public void TryParseFileLogLine_ValidLine_ReturnsLogEntry()
    {
        var parsed = CliLogFormat.TryParseFileLogLine(
            "[2026-05-15 17:07:30.501] [INFO] [AppHost] apphost.ts(5,22): error TS1109: Expression expected.",
            out var entry);

        Assert.True(parsed);
        Assert.Equal(CliLogFormat.FileLevelTokens.Information, entry.Level);
        Assert.Equal(CliLogFormat.Categories.AppHost, entry.Category);
        Assert.Equal("apphost.ts(5,22): error TS1109: Expression expected.", entry.Message);
    }

    [Fact]
    public void TryParseFileLogLine_MessageContainsLogDelimiters_PreservesFullMessage()
    {
        var parsed = CliLogFormat.TryParseFileLogLine(
            "[2026-05-15 17:07:30.501] [FAIL] [GuestAppHostProject] before ] [INFO] [Other] after",
            out var entry);

        Assert.True(parsed);
        Assert.Equal(CliLogFormat.FileLevelTokens.Error, entry.Level);
        Assert.Equal(CliLogFormat.Categories.GuestAppHostProject, entry.Category);
        Assert.Equal("before ] [INFO] [Other] after", entry.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("System.InvalidOperationException: The TypeScript (Node.js) apphost failed.")]
    [InlineData("[2026-05-15 17:07:30.501]")]
    [InlineData("[2026-05-15 17:07:30.501] [INFO]")]
    [InlineData("[2026-05-15 17:07:30.501] [INFO] [AppHost]")]
    public void TryParseFileLogLine_MalformedLine_ReturnsFalse(string line)
    {
        var parsed = CliLogFormat.TryParseFileLogLine(line, out var entry);

        Assert.False(parsed);
        Assert.Equal(default, entry);
    }

    [Theory]
    [InlineData(CliLogFormat.FileLevelTokens.Trace, LogLevel.Trace)]
    [InlineData(CliLogFormat.FileLevelTokens.Debug, LogLevel.Debug)]
    [InlineData(CliLogFormat.FileLevelTokens.Information, LogLevel.Information)]
    [InlineData(CliLogFormat.FileLevelTokens.Warning, LogLevel.Warning)]
    [InlineData(CliLogFormat.FileLevelTokens.Error, LogLevel.Error)]
    [InlineData(CliLogFormat.FileLevelTokens.Critical, LogLevel.Critical)]
    public void TryGetLogLevelFromFileToken_KnownToken_ReturnsLogLevel(string token, LogLevel expectedLogLevel)
    {
        var parsed = CliLogFormat.TryGetLogLevelFromFileToken(token, out var logLevel);

        Assert.True(parsed);
        Assert.Equal(expectedLogLevel, logLevel);
    }

    [Fact]
    public void TryGetLogLevelFromFileToken_UnknownToken_ReturnsFalse()
    {
        var parsed = CliLogFormat.TryGetLogLevelFromFileToken("NOPE", out var logLevel);

        Assert.False(parsed);
        Assert.Equal(LogLevel.None, logLevel);
    }

    [Fact]
    public void GetDetachedAppHostCategory_PrefixesOriginalCategory()
    {
        var category = CliLogFormat.GetDetachedAppHostCategory(CliLogFormat.Categories.GuestAppHostProject);

        Assert.Equal("DetachedAppHost/GuestAppHostProject", category);
    }
}
