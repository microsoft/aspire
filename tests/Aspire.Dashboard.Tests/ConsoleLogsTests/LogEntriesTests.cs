// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared.ConsoleLogs;
using Xunit;

namespace Aspire.Dashboard.Tests.ConsoleLogsTests;

public class LogEntriesTests
{
    private static LogEntries CreateLogEntries(int? maximumEntryCount = null, int? baseLineNumber = null)
    {
        var logEntries = new LogEntries(maximumEntryCount: maximumEntryCount ?? int.MaxValue);
        logEntries.BaseLineNumber = baseLineNumber ?? 1;
        return logEntries;
    }

    private static void AddLogLine(LogEntries logEntries, string content, bool isError)
    {
        var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);
        var logEntry = logParser.CreateLogEntry(content, isError, resourcePrefix: null);
        logEntries.InsertSorted(logEntry);
    }

    [Fact]
    public void Clear_AfterEarliestTimestampIndex_Success()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);

        // Insert log with no timestamp.
        var logEntry1 = logParser.CreateLogEntry("Test", isErrorOutput: false, resourcePrefix: null);
        logEntries.InsertSorted(logEntry1);

        // Insert log with timestamp that ensures collections timestamp value is not zero.
        var logEntry2 = logParser.CreateLogEntry("2024-08-19T06:12:01.000Z Test", isErrorOutput: false, resourcePrefix: null);
        logEntries.InsertSorted(logEntry2);

        logEntries.Clear(keepActivePauseEntries: true);
        logEntries.BaseLineNumber = 0;

        // Insert another log entry after clearing.
        var logEntry3 = logParser.CreateLogEntry("2024-08-19T06:12:02.000Z Test", isErrorOutput: false, resourcePrefix: null);
        logEntries.InsertSorted(logEntry3);

        // Assert
        Assert.Single(logEntries.GetEntries());
    }

    [Fact]
    public void Add_PauseAndThenRemove_ActivePauseKept()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "Hello world", isError: false);

        // Completed pause
        logEntries.InsertSorted(LogEntry.CreatePause(
            "resource-name",
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        // Active pause
        var pauseEntry = LogEntry.CreatePause("resource-name", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        logEntries.InsertSorted(pauseEntry);

        var pauseVM = pauseEntry.Pause;
        Assert.NotNull(pauseVM);
        pauseVM.FilteredCount++;

        logEntries.Clear(keepActivePauseEntries: true);

        // Assert
        var entry = Assert.Single(logEntries.GetEntries());
        Assert.Equal(pauseEntry, entry);
        Assert.Equal(0, pauseVM.FilteredCount);
    }

    [Fact]
    public void AddLogLine_Single()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "Hello world", isError: false);

        // Assert
        var entry = Assert.Single(logEntries.GetEntries());
        Assert.Equal("Hello world", entry.Content);
        Assert.Null(entry.Timestamp);
    }

    [Fact]
    public void AddLogLine_MultipleLines()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "Hello world", isError: false);
        AddLogLine(logEntries, "Hello world 2", isError: false);
        AddLogLine(logEntries, "Hello world 3", isError: true);

        // Assert
        Assert.Collection(logEntries.GetEntries(),
            l => Assert.Equal("Hello world", l.Content),
            l => Assert.Equal("Hello world 2", l.Content),
            l => Assert.Equal("Hello world 3", l.Content));
    }

    [Fact]
    public void AddLogLine_MultipleLines_MixDatePrefix()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "Hello world", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:10:01.000Z Hello world 2", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:10:02.000Z Hello world 3", isError: false);
        AddLogLine(logEntries, "Hello world 4", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:10:03.000Z Hello world 5", isError: false);

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l =>
            {
                Assert.Equal("Hello world", l.Content);
                Assert.Equal(1, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 2", l.Content);
                Assert.Equal(2, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 3", l.Content);
                Assert.Equal(3, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 4", l.Content);
                Assert.Equal(4, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 5", l.Content);
                Assert.Equal(5, l.LineNumber);
            });
    }

    [Fact]
    public void AddLogLine_MultipleLines_MixDatePrefix_OutOfOrder()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "Hello world", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:12:00.000Z Hello world 2", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:11:00.000Z Hello world 3", isError: false);
        AddLogLine(logEntries, "Hello world 4", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:13:00.000Z Hello world 5", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:10:00.000Z Hello world 6", isError: false);

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l =>
            {
                Assert.Equal("Hello world", l.Content);
                Assert.Equal(1, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 6", l.Content);
                Assert.Equal(2, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 3", l.Content);
                Assert.Equal(3, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 2", l.Content);
                Assert.Equal(4, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 4", l.Content);
                Assert.Equal(5, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 5", l.Content);
                Assert.Equal(6, l.LineNumber);
            });
    }

    [Fact]
    public void AddLogLine_MultipleLines_SameDate_InOrder()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        // Act
        AddLogLine(logEntries, "2024-08-19T06:10:00.000Z Hello world 1", isError: false);
        AddLogLine(logEntries, "2024-08-19T06:10:00.000Z Hello world 2", isError: false);

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l =>
            {
                Assert.Equal("Hello world 1", l.Content);
                Assert.Equal(1, l.LineNumber);
            },
            l =>
            {
                Assert.Equal("Hello world 2", l.Content);
                Assert.Equal(2, l.LineNumber);
            });
    }

    [Fact]
    public void InsertSorted_OutOfOrderWithSameTimestamp_ReturnInOrder()
    {
        // Arrange
        var logEntries = CreateLogEntries();

        var timestamp = DateTime.UtcNow;

        // Act
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(1), "1", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(3), "3", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(2), "2", isErrorMessage: false));

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l => Assert.Equal("1", l.Content),
            l => Assert.Equal("2", l.Content),
            l => Assert.Equal("3", l.Content));
    }

    [Fact]
    public void InsertSorted_TrimsToMaximumEntryCount_Ordered()
    {
        // Arrange
        var logEntries = CreateLogEntries(maximumEntryCount: 2);

        var timestamp = DateTime.UtcNow;

        // Act
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(1), "1", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(2), "2", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(3), "3", isErrorMessage: false));

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l => Assert.Equal("2", l.Content),
            l => Assert.Equal("3", l.Content));
    }

    [Fact]
    public void InsertSorted_TrimsToMaximumEntryCount_OutOfOrder()
    {
        // Arrange
        var logEntries = CreateLogEntries(maximumEntryCount: 2);

        var timestamp = DateTime.UtcNow;

        // Act
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(1), "1", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(3), "3", isErrorMessage: false));
        logEntries.InsertSorted(LogEntry.Create(timestamp.AddSeconds(2), "2", isErrorMessage: false));

        // Assert
        var entries = logEntries.GetEntries();
        Assert.Collection(entries,
            l => Assert.Equal("2", l.Content),
            l => Assert.Equal("3", l.Content));
    }

    [Fact]
    public void CreateLogEntry_AnsiAndUrl_HasUrlAnchor()
    {
        // Arrange
        var parser = new LogParser(ConsoleColor.Black, encodeForHtml: true);

        // Act
        var entry = parser.CreateLogEntry("\x1b[36mhttps://www.example.com\u001b[0m", isErrorOutput: false, resourcePrefix: null);

        // Assert
        Assert.Equal("<span class=\"ansi-fg-cyan\"></span><a target=\"_blank\" href=\"https://www.example.com\" rel=\"noopener noreferrer nofollow\">https://www.example.com</a>", entry.Content);
    }

    [Theory]
    [InlineData(ConsoleColor.Black, @"<span class=""ansi-fg-green"">info</span>: LoggerName")]
    [InlineData(ConsoleColor.Blue, @"<span class=""ansi-fg-green ansi-bg-black"">info</span>: LoggerName")]
    public void CreateLogEntry_DefaultBackgroundColor_SkipMatchingColor(ConsoleColor defaultBackgroundColor, string output)
    {
        // Arrange
        var parser = new LogParser(defaultBackgroundColor, encodeForHtml: true);

        // Act
        var entry = parser.CreateLogEntry("\u001b[40m\u001b[32minfo\u001b[39m\u001b[22m\u001b[49m: LoggerName", isErrorOutput: false, resourcePrefix: null);

        // Assert
        Assert.Equal(output, entry.Content);
    }
}
