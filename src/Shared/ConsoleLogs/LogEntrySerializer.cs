// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Shared.ConsoleLogs;

/// <summary>
/// Provides methods for serializing log entries to text.
/// </summary>
internal static class LogEntrySerializer
{
    /// <summary>
    /// Writes a collection of log entries to a stream, stripping ANSI control sequences.
    /// </summary>
    /// <param name="entries">The log entries to serialize.</param>
    /// <param name="stream">The stream to write to.</param>
    public static void WriteLogEntriesToStream(IList<LogEntry> entries, Stream stream)
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);

        foreach (var entry in entries)
        {
            if (entry.Type is LogEntryType.Pause)
            {
                continue;
            }

            if (entry.RawContent is not null)
            {
                writer.WriteLine(AnsiParser.StripControlSequences(entry.RawContent));
            }
            else
            {
                writer.WriteLine();
            }
        }

        writer.Flush();
    }

    /// <summary>
    /// Writes a collection of log entries to a stream as CSV with Resource, Timestamp and Message columns.
    /// ANSI control sequences and the inline timestamp are stripped from the message.
    /// </summary>
    /// <param name="entries">The log entries to serialize.</param>
    /// <param name="stream">The stream to write to.</param>
    public static void WriteLogEntriesToCsvStream(IList<LogEntry> entries, Stream stream)
    {
        using var writer = new StreamWriter(stream, leaveOpen: true);

        writer.Write("Resource,Timestamp,Message");
        writer.Write("\r\n");

        foreach (var entry in entries)
        {
            if (entry.Type is LogEntryType.Pause)
            {
                continue;
            }

            var timestamp = entry.Timestamp is { } t
                ? t.ToString("o", CultureInfo.InvariantCulture)
                : string.Empty;

            writer.Write(EscapeCsvField(entry.ResourcePrefix ?? string.Empty));
            writer.Write(',');
            writer.Write(EscapeCsvField(timestamp));
            writer.Write(',');
            writer.Write(EscapeCsvField(entry.GetStrippedLogContent() ?? string.Empty));
            // CSV (RFC 4180) records are separated by CRLF.
            writer.Write("\r\n");
        }

        writer.Flush();
    }

    /// <summary>
    /// Escapes a CSV field per RFC 4180: fields containing a comma, double quote or line break are
    /// wrapped in double quotes, and any embedded double quotes are doubled.
    /// </summary>
    private static string EscapeCsvField(string value)
    {
        if (value.IndexOfAny(s_csvSpecialChars) < 0)
        {
            return value;
        }

        return string.Concat("\"", value.Replace("\"", "\"\""), "\"");
    }

    private static readonly char[] s_csvSpecialChars = [',', '"', '\r', '\n'];
}
