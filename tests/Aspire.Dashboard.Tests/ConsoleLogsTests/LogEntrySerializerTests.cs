// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Shared.ConsoleLogs;
using Xunit;

namespace Aspire.Dashboard.Tests.ConsoleLogsTests;

public class LogEntrySerializerTests
{
    private static string SerializeToCsv(IList<LogEntry> entries)
    {
        using var stream = new MemoryStream();
        LogEntrySerializer.WriteLogEntriesToCsvStream(entries, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static LogEntry CreateEntry(string content, string? resourcePrefix = null)
    {
        var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);
        return logParser.CreateLogEntry(content, isErrorOutput: false, resourcePrefix);
    }

    [Fact]
    public void WriteLogEntriesToCsvStream_WritesHeaderRow()
    {
        // Arrange & Act
        var csv = SerializeToCsv([]);

        // Assert
        Assert.Equal("Resource,Timestamp,Message\r\n", csv);
    }

    [Fact]
    public void WriteLogEntriesToCsvStream_WritesResourceTimestampAndMessage()
    {
        // Arrange
        var entry = CreateEntry("2024-08-19T06:12:01.000Z Hello world", resourcePrefix: "myapp");

        // Act
        var csv = SerializeToCsv([entry]);

        // Assert - timestamp is parsed out of the message and rendered in its own column.
        Assert.Equal(
            "Resource,Timestamp,Message\r\n" +
            "myapp,2024-08-19T06:12:01.0000000Z,Hello world\r\n",
            csv);
    }

    [Fact]
    public void WriteLogEntriesToCsvStream_EmptyResourceAndTimestamp_WritesEmptyFields()
    {
        // Arrange - no resource prefix and no timestamp in the content.
        var entry = CreateEntry("Hello world");

        // Act
        var csv = SerializeToCsv([entry]);

        // Assert
        Assert.Equal(
            "Resource,Timestamp,Message\r\n" +
            ",,Hello world\r\n",
            csv);
    }

    [Theory]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("plain", "plain")]
    public void WriteLogEntriesToCsvStream_EscapesSpecialCharacters(string message, string expectedField)
    {
        // Arrange
        var entry = CreateEntry(message, resourcePrefix: "res");

        // Act
        var csv = SerializeToCsv([entry]);

        // Assert
        Assert.Equal(
            "Resource,Timestamp,Message\r\n" +
            $"res,,{expectedField}\r\n",
            csv);
    }

    [Fact]
    public void WriteLogEntriesToCsvStream_SkipsPauseEntries()
    {
        // Arrange
        var entries = new List<LogEntry>
        {
            CreateEntry("Line one", resourcePrefix: "res"),
            LogEntry.CreatePause("res", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            CreateEntry("Line two", resourcePrefix: "res")
        };

        // Act
        var csv = SerializeToCsv(entries);

        // Assert - the pause entry is not serialized.
        Assert.Equal(
            "Resource,Timestamp,Message\r\n" +
            "res,,Line one\r\n" +
            "res,,Line two\r\n",
            csv);
    }
}
