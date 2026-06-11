// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
}
