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
