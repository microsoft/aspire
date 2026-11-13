// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Dashboard.Utils;

internal static partial class DateFormatStringsHelpers
{
    // There are an unbound number of CultureInfo instances so we don't want to use it as the key.
    // Someone could have also customized their culture so we don't want to use the name as the key.
    // This struct contains required information from the culture that is used in cached format strings.
    private readonly record struct CultureDetailsKey(string LongTimePattern, string ShortDatePattern, string NumberDecimalSeparator, TimeFormat timeFormat);
    private sealed record MillisecondFormatStrings(CachedTimeFormatStrings LongTimePattern, CachedTimeFormatStrings ShortDateLongTimePattern);
    private sealed record CachedTimeFormatStrings(string None, string TruncatedMilliseconds, string FullMilliseconds);

    // Cache of format strings for each culture and 12h/24h override combination. Each entry is lazily initialized on demand.
    private static readonly ConcurrentDictionary<CultureDetailsKey, MillisecondFormatStrings> s_formatStrings = new();

    // Colon and dot are the only time separators used by registered cultures. Regex checks for both.
    [GeneratedRegex(@"(:ss|\.ss|:s|\.s)")]
    private static partial Regex MatchSecondsInTimeFormatPattern();

    // Matches 24-hour format specifiers: HH or H (but not h).
    [GeneratedRegex(@"HH?")]
    private static partial Regex MatchHourIn24HourTimeFormatPattern();

    // Matches 12-hour format specifiers: hh or h (but not H).
    [GeneratedRegex(@"hh?")]
    private static partial Regex MatchHourIn12HourTimeFormatPattern();

    // Matches AM/PM designator with optional leading whitespace.
    [GeneratedRegex(@"\s*tt")]
    private static partial Regex MatchAmPmDesignator();

    // Matches the full month name specifier, MMMM, to abbreviate it to MMM.
    [GeneratedRegex("M{4}")]
    private static partial Regex MatchFullMonthName();

    // Matches the day of the week with the separator that follows or precedes it, for example "dddd, " in
    // "dddd, MMMM d, yyyy" (en-US) or " dddd" in "yyyy年M月d日 dddd" (ja-JP).
    [GeneratedRegex(@"dddd,?\s*|\s*dddd")]
    private static partial Regex MatchDayOfWeek();

    private readonly record struct MediumDateKey(string LongDatePattern, string ShortTimePattern, TimeFormat TimeFormat, bool SpaceSeparated);
    private static readonly ConcurrentDictionary<MediumDateKey, string> s_mediumDateShortTimePatterns = new();

    private static MillisecondFormatStrings GetDateFormatStrings(CultureInfo cultureInfo, TimeFormat timeFormat)
    {
        var key = new CultureDetailsKey(cultureInfo.DateTimeFormat.LongTimePattern, cultureInfo.DateTimeFormat.ShortDatePattern, cultureInfo.NumberFormat.NumberDecimalSeparator, timeFormat);

        return s_formatStrings.GetOrAdd(key, static k =>
        {
            var (none, truncated, full) = GetTimePatterns(k);
            return new MillisecondFormatStrings(
                new CachedTimeFormatStrings(none, truncated, full),
                new CachedTimeFormatStrings(
                    k.ShortDatePattern + " " + none,
                    k.ShortDatePattern + " " + truncated,
                    k.ShortDatePattern + " " + full));
        });

        static (string None, string Truncated, string Full) GetTimePatterns(CultureDetailsKey key)
        {
            // From https://learn.microsoft.com/dotnet/standard/base-types/how-to-display-milliseconds-in-date-and-time-values

            // Intentionally use fff here instead of FFF so output has a consistent length.
            return (
                FormatPattern(key, millisecondFormat: null),
                FormatPattern(key, "fff"),
                FormatPattern(key, "FFFFFFF"));
        }

        static string FormatPattern(CultureDetailsKey key, string? millisecondFormat)
        {
            // Gets the long time pattern, which is something like "h:mm:ss tt" (en-US), "H:mm:ss" (ja-JP), "HH:mm:ss" (fr-FR).
            var longTimePattern = key.LongTimePattern;

            longTimePattern = ApplyTimeFormat(longTimePattern, key.timeFormat);

            if (millisecondFormat is null)
            {
                return longTimePattern;
            }

            return MatchSecondsInTimeFormatPattern().Replace(longTimePattern, $"$1'{key.NumberDecimalSeparator}'{millisecondFormat}");
        }
    }

    /// <summary>
    /// Gets a pattern with an abbreviated month and no seconds, such as "MMM d, yyyy, h:mm tt" (en-US),
    /// "d MMM yyyy, HH:mm" (fr-FR) or "yyyy年M月d日 H:mm" (ja-JP). It mirrors the browser's medium date and short time
    /// styles (https://developer.mozilla.org/docs/Web/JavaScript/Reference/Global_Objects/Intl/DateTimeFormat/DateTimeFormat#datestyle),
    /// which .NET doesn't have as a standard format.
    /// </summary>
    internal static string GetMediumDateShortTimePattern(CultureInfo cultureInfo, TimeFormat timeFormat)
    {
        // Browsers separate the date and time with a comma, "Nov 11, 2026, 12:41 PM", except in Chinese, Japanese and
        // Korean, whose dates end with a character rather than a number: "2026年11月11日 12:41".
        var spaceSeparated = cultureInfo.TwoLetterISOLanguageName is "zh" or "ja" or "ko";
        var key = new MediumDateKey(cultureInfo.DateTimeFormat.LongDatePattern, cultureInfo.DateTimeFormat.ShortTimePattern, timeFormat, spaceSeparated);

        return s_mediumDateShortTimePatterns.GetOrAdd(key, static k =>
        {
            // The long date pattern without the day of the week and with an abbreviated month, for example
            // "dddd, MMMM d, yyyy" -> "MMM d, yyyy" (en-US) or "dddd d MMMM yyyy" -> "d MMM yyyy" (fr-FR).
            var datePattern = MatchFullMonthName().Replace(MatchDayOfWeek().Replace(k.LongDatePattern, ""), "MMM");
            var timePattern = ApplyTimeFormat(k.ShortTimePattern, k.TimeFormat);

            return datePattern + (k.SpaceSeparated ? " " : ", ") + timePattern;
        });
    }

    // Applies the 12h/24h override, if any, to a culture's time pattern.
    private static string ApplyTimeFormat(string timePattern, TimeFormat timeFormat) => timeFormat switch
    {
        TimeFormat.TwelveHour => ConvertTo12Hour(timePattern),
        TimeFormat.TwentyFourHour => ConvertTo24Hour(timePattern),
        _ => timePattern // System: use culture's pattern as-is
    };

    private static string ConvertTo12Hour(string pattern)
    {
        // Replace H/HH with h (24h -> 12h).
        pattern = MatchHourIn24HourTimeFormatPattern().Replace(pattern, "h");

        // Add AM/PM designator if not already present.
        if (!pattern.Contains("tt"))
        {
            pattern += " tt";
        }

        return pattern;
    }

    private static string ConvertTo24Hour(string pattern)
    {
        // Replace h/hh with H (12h -> 24h).
        pattern = MatchHourIn12HourTimeFormatPattern().Replace(pattern, "H");

        // Remove AM/PM designator. It leads the time in some cultures, such as "tt h:mm" (ko-KR), so trim both ends.
        pattern = MatchAmPmDesignator().Replace(pattern, "").Trim();

        return pattern;
    }

    internal static string GetLongTimePattern(CultureInfo cultureInfo, TimeFormat timeFormat, MillisecondsDisplay millisecondsDisplay)
        => GetPattern(GetDateFormatStrings(cultureInfo, timeFormat).LongTimePattern, millisecondsDisplay);

    internal static string GetShortDateLongTimePattern(CultureInfo cultureInfo, TimeFormat timeFormat, MillisecondsDisplay millisecondsDisplay)
        => GetPattern(GetDateFormatStrings(cultureInfo, timeFormat).ShortDateLongTimePattern, millisecondsDisplay);

    private static string GetPattern(CachedTimeFormatStrings patterns, MillisecondsDisplay millisecondsDisplay)
    {
        return millisecondsDisplay switch
        {
            MillisecondsDisplay.None => patterns.None,
            MillisecondsDisplay.Truncated => patterns.TruncatedMilliseconds,
            MillisecondsDisplay.Full => patterns.FullMilliseconds,
            _ => throw new ArgumentException($"Unexpected {nameof(MillisecondsDisplay)} value: {millisecondsDisplay}.", nameof(millisecondsDisplay))
        };
    }
}
