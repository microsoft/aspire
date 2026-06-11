// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;

namespace Aspire.Shared.ConsoleLogs;

internal sealed class LogParser
{
    private readonly ConsoleColor _defaultBackgroundColor;
    private readonly bool _encodeForHtml;
    private AnsiParser.ParserState? _residualState;

    public LogParser(ConsoleColor defaultBackgroundColor, bool encodeForHtml = false)
    {
        _defaultBackgroundColor = defaultBackgroundColor;
        _encodeForHtml = encodeForHtml;
    }

    public LogEntry CreateLogEntry(string rawText, bool isErrorOutput, string? resourcePrefix)
    {
        // Several steps to do here:
        //
        // 1. Parse the content to look for the timestamp
        // 2. HTML Encode the raw text for security purposes (when encodeForHtml is true)
        // 3. Parse the content to look for ANSI Control Sequences and color them if possible (when encodeForHtml is true)
        // 4. Parse the content to look for URLs and make them links if possible (when encodeForHtml is true)
        // 5. Create the LogEntry

        var content = rawText;

        // 1. Parse the content to look for the timestamp
        DateTime? timestamp = null;

        if (TimestampParser.TryParseConsoleTimestamp(content, out var timestampParseResult))
        {
            content = timestampParseResult.Value.ModifiedText;
            timestamp = timestampParseResult.Value.Timestamp.UtcDateTime;
        }

        if (_encodeForHtml)
        {
            Func<string, string> callback = (s) =>
            {
                // This callback is run on text that isn't transformed into a clickable URL.

                // 3a. HTML Encode the raw text for security purposes
                var updatedText = WebUtility.HtmlEncode(s);

                // 3b. Parse the content to look for ANSI Control Sequences and color them if possible
                var conversionResult = AnsiParser.ConvertToHtml(updatedText, _residualState, _defaultBackgroundColor);
                updatedText = conversionResult.ConvertedText;
                _residualState = conversionResult.ResidualState;

                return updatedText ?? string.Empty;
            };

            // 3. Parse the content to look for URLs and make them links if possible
            if (UrlParser.TryParse(content, callback, out var modifiedText))
            {
                content = modifiedText;
            }
            else
            {
                content = callback(content);
            }
        }

        // 5. Create the LogEntry
        var logEntry = LogEntry.Create(timestamp, content, rawText, isErrorOutput, resourcePrefix);

        return logEntry;
    }
}
