// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Shared.ConsoleLogs;
using Xunit;

namespace Aspire.Dashboard.Tests.ConsoleLogsTests;

public class LogEntryTests
{
    private static LogEntry CreateEntry(string content)
    {
        var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);
        return logParser.CreateLogEntry(content, isErrorOutput: false, resourcePrefix: null);
    }

    // Reads the private memoization field so we can assert the caching contract that has no public surface.
    private static string? GetCachedStrippedLogContent(LogEntry entry)
    {
        var field = typeof(LogEntry).GetField("_strippedLogContent", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (string?)field!.GetValue(entry);
    }

    [Fact]
    public void GetStrippedLogContent_MemoizesResult()
    {
        // Arrange
        var entry = CreateEntry("2024-08-19T06:12:01.000Z Hello world");
        Assert.Null(GetCachedStrippedLogContent(entry));

        // Act
        var content = entry.GetStrippedLogContent();

        // Assert - the cache is populated for reuse by later reads (e.g. filtering).
        Assert.Equal("Hello world", content);
        Assert.Equal(content, GetCachedStrippedLogContent(entry));
    }

    [Fact]
    public void GetStrippedLogContentUncached_DoesNotPopulateCache()
    {
        // Arrange
        var entry = CreateEntry("2024-08-19T06:12:01.000Z Hello world");
        Assert.Null(GetCachedStrippedLogContent(entry));

        // Act
        var content = entry.GetStrippedLogContentUncached();

        // Assert - same value as the caching path, but the cache is left untouched.
        Assert.Equal("Hello world", content);
        Assert.Null(GetCachedStrippedLogContent(entry));
    }

    [Fact]
    public void GetStrippedLogContentUncached_ReusesCacheWhenAlreadyPopulated()
    {
        // Arrange - populate the cache via the caching path first.
        var entry = CreateEntry("2024-08-19T06:12:01.000Z Hello world");
        var cached = entry.GetStrippedLogContent();

        // Act
        var uncached = entry.GetStrippedLogContentUncached();

        // Assert - the already-cached instance is reused rather than recomputed.
        Assert.Same(cached, uncached);
    }
}
