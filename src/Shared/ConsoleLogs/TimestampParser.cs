// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Shared.ConsoleLogs;

internal static partial class TimestampParser
{
    public static bool TryParseConsoleTimestamp(string text, [NotNullWhen(true)] out TimestampParserResult? result)
    {
        // Regex is cached inside the method.
        var match = GenerateRfc3339RegEx().Match(text);

        if (match.Success)
        {
            var span = text.AsSpan();

            ReadOnlySpan<char> content;
            if (match.Index + match.Length >= span.Length)
            {
                content = "";
            }
            else
            {
                content = span[(match.Index + match.Length)..];

                // Trim whitespace added by logging between timestamp and content.
                if (char.IsWhiteSpace(content[0]))
                {
                    content = content.Slice(1);
                }
            }

            result = new(content.ToString(), DateTimeOffset.Parse(match.ValueSpan, CultureInfo.InvariantCulture));
            return true;
        }

        result = default;
        return false;
    }

    // Regular Expression for an RFC3339 timestamp, including RFC3339Nano
    //
    // Example timestamps:
    // 2023-10-02T12:56:35.123456789Z
    // 2023-10-02T13:56:35.123456789+10:00
    // 2023-10-02T13:56:35.123456789-10:00
    // 2023-10-02T13:56:35.123456789Z10:00
    // 2023-10-02T13:56:35.123456Z
    // 2023-10-02T13:56:35Z
    [GeneratedRegex("""
        ^                                           # Starts the string
        (\d{4})                                     # Four digits for the year
        -                                           # Separator for the date
        (0[1-9]|1[0-2])                             # Two digits for the month, restricted to 01-12
        -                                           # Separator for the date
        (0[1-9]|[12][0-9]|3[01])                    # Two digits for the day, restricted to 01-31
        T                                           # Literal, separator between date and time, either a T or a space
        ([01][0-9]|2[0-3])                          # Two digits for the hour, restricted to 00-23
        :                                           # Separator for the time
        ([0-5][0-9])                                # Two digits for the minutes, restricted to 00-59
        :                                           # Separator for the time
        ([0-5][0-9])                                # Two digits for the seconds, restricted to 00-59
        (\.\d{1,9})?                                # A period and up to nine digits for the partial seconds (optional)
        (Z|([Z+-]([01][0-9]|2[0-3]):?([0-5][0-9])))? # Time Zone offset, in the form Z or ZHH:MM or ZHHMM or +HH:MM or +HHMM or -HH:MM or -HHMM (optional)
        """,
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex GenerateRfc3339RegEx();

    public readonly record struct TimestampParserResult(string ModifiedText, DateTimeOffset Timestamp);
}
