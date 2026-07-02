// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Shared.ConsoleLogs;

[DebuggerDisplay("LineNumber = {LineNumber}, Timestamp = {Timestamp}, ResourcePrefix = {ResourcePrefix}, Content = {Content}, Type = {Type}")]
#if ASPIRE_DASHBOARD
public sealed class LogEntry
#else
internal sealed class LogEntry
#endif
{
    public string? Content { get; private set; }

    /// <summary>
    /// The text content of the log entry. This is the same as <see cref="Content"/>, but without embedded links or other transformations and including the timestamp.
    /// </summary>
    public string? RawContent { get; private set; }

    private string? _strippedRawContent;
    private string? _strippedLogContent;

    /// <summary>
    /// <see cref="RawContent"/> with ANSI control sequences removed. This is the plain text the user
    /// actually sees rendered (including the timestamp). Use this for matching against user-entered
    /// text - <see cref="Content"/> contains HTML markup added during ANSI conversion and
    /// <see cref="RawContent"/> still contains the raw escape sequences, so matching against either
    /// produces false negatives when the term spans markup or a color boundary (for example, the
    /// default .NET console emits the level prefix as <c>info\x1b[..m:</c>, so a search for
    /// <c>info:</c> would never match the raw content).
    /// </summary>
    /// <remarks>
    /// The stripped value is cached because filtering runs over the entire log buffer on each update.
    /// </remarks>
    public string? GetStrippedRawContent()
    {
        if (RawContent is null)
        {
            return null;
        }

        return _strippedRawContent ??= AnsiParser.StripControlSequences(RawContent);
    }

    /// <summary>
    /// <see cref="RawContent"/> with the parsed timestamp and ANSI control sequences removed. This
    /// represents the rendered log message text without optional row adornments such as timestamps,
    /// resource prefixes, or stderr badges.
    /// </summary>
    /// <remarks>
    /// The stripped value is cached because filtering runs over the entire log buffer on each update.
    /// </remarks>
    public string? GetStrippedLogContent()
    {
        if (RawContent is null)
        {
            return null;
        }

        _strippedLogContent ??= TimestampParser.TryParseConsoleTimestamp(RawContent, out var timestampParseResult)
            ? AnsiParser.StripControlSequences(timestampParseResult.Value.ModifiedText)
            : GetStrippedRawContent();

        return _strippedLogContent;
    }

    public DateTime? Timestamp { get; private set; }
    public LogEntryType Type { get; private set; } = LogEntryType.Default;
    public int LineNumber { get; set; }
    public LogPauseViewModel? Pause { get; private set; }
    public string? ResourcePrefix { get; set; }

    public static LogEntry CreatePause(string resourcePrefix, DateTime startTimestamp, DateTime? endTimestamp = null)
    {
        return new LogEntry
        {
            Timestamp = startTimestamp,
            Type = LogEntryType.Pause,
            LineNumber = 0,
            Pause = new LogPauseViewModel
            {
                ResourcePrefix = resourcePrefix,
                StartTime = startTimestamp,
                EndTime = endTimestamp
            },
            ResourcePrefix = resourcePrefix
        };
    }

    public static LogEntry Create(DateTime? timestamp, string logMessage, bool isErrorMessage)
    {
        return Create(timestamp, logMessage, logMessage, isErrorMessage, resourcePrefix: null);
    }

    public static LogEntry Create(DateTime? timestamp, string logMessage, string rawLogContent, bool isErrorMessage, string? resourcePrefix)
    {
        return new LogEntry
        {
            Timestamp = timestamp,
            Content = logMessage,
            RawContent = rawLogContent,
            ResourcePrefix = resourcePrefix,
            Type = isErrorMessage ? LogEntryType.Error : LogEntryType.Default
        };
    }
}

/// <summary>
/// Represents the stable identity fields used to deduplicate overlapping log entries.
/// </summary>
/// <remarks>
/// Keep this key aligned with the fields that come from the underlying log source. Display-only
/// fields such as line number and resource prefix can differ depending on whether the entry came
/// from a terminal snapshot, a follow stream, or an in-memory logger replay, so they are excluded.
/// </remarks>
internal readonly record struct LogEntryKey(DateTime? Timestamp, string? Content, string? RawContent, LogEntryType Type)
{
    /// <summary>
    /// Creates a deduplication key for the specified log entry.
    /// </summary>
    public static LogEntryKey Create(LogEntry logEntry)
    {
        return new(logEntry.Timestamp, logEntry.Content, logEntry.RawContent, logEntry.Type);
    }
}

#if ASPIRE_DASHBOARD
public enum LogEntryType
#else
internal enum LogEntryType
#endif
{
    Default,
    Error,
    Pause
}
