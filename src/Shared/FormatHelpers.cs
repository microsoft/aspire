// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Extensions;

namespace Aspire.Dashboard.Utils;

public enum MillisecondsDisplay
{
    None,
    Truncated,
    Full
}

public enum TimeFormat
{
    System,
    TwelveHour,
    TwentyFourHour
}

public interface ITimeFormatProvider
{
    TimeFormat ResolvedTimeFormat { get; }
}

public interface ITimeZoneNameProvider
{
    /// <summary>
    /// Gets the short name of the local time zone while it is at a UTC offset, such as "PST" at -08:00 or "PDT" at
    /// -07:00, or <see langword="null"/> when no name is known for that offset.
    /// </summary>
    string? GetTimeZoneShortName(TimeSpan utcOffset);
}

internal static class FormatHelpers
{
    // Limit size of very long data that is written in large grids.
    public const int ColumnMaximumTextLength = 250;
    public const int TooltipMaximumTextLength = 1500;
    public const string Ellipsis = "…";

    /// <summary>
    /// Formats a DateTime as a local time string (HH:mm:ss.fff) for console output.
    /// </summary>
    public static string FormatConsoleTime(TimeProvider timeProvider, DateTime value)
    {
        return timeProvider.ToLocal(value)
            .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    public static string FormatTime(TimeProvider timeProvider, DateTime value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null)
    {
        cultureInfo ??= CultureInfo.CurrentCulture;
        var local = timeProvider.ToLocal(value);
        var timeFormat = GetResolvedTimeFormat(timeProvider);
        var pattern = DateFormatStringsHelpers.GetLongTimePattern(cultureInfo, timeFormat, millisecondsDisplay);

        return local.ToString(pattern, cultureInfo);
    }

    public static string FormatTime(TimeProvider timeProvider, DateTimeOffset value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null) =>
        FormatTime(timeProvider, value.UtcDateTime, millisecondsDisplay, cultureInfo);

    public static string FormatDateTime(TimeProvider timeProvider, DateTime value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null)
    {
        cultureInfo ??= CultureInfo.CurrentCulture;
        var local = timeProvider.ToLocal(value);
        var timeFormat = GetResolvedTimeFormat(timeProvider);
        var pattern = DateFormatStringsHelpers.GetShortDateLongTimePattern(cultureInfo, timeFormat, millisecondsDisplay);

        return local.ToString(pattern, cultureInfo);
    }

    public static string FormatDateTime(TimeProvider timeProvider, DateTimeOffset value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null) =>
        FormatDateTime(timeProvider, value.UtcDateTime, millisecondsDisplay, cultureInfo);

    public static string FormatUtcDateTime(TimeProvider timeProvider, DateTime value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null)
    {
        cultureInfo ??= CultureInfo.CurrentCulture;
        var timeFormat = GetResolvedTimeFormat(timeProvider);
        var pattern = DateFormatStringsHelpers.GetShortDateLongTimePattern(cultureInfo, timeFormat, millisecondsDisplay);
        return $"{value.ToUniversalTime().ToString(pattern, cultureInfo)} UTC";
    }

    /// <summary>
    /// Formats the exact time behind a relative time ("Started 7m ago") for its tooltip, with an abbreviated month, no
    /// seconds and the time zone, for example "Nov 11, 2026, 12:41 PM PST" (en-US) or "11 nov. 2026, 12:41 UTC+1" (fr-FR).
    /// </summary>
    /// <remarks>
    /// The relative time is already on screen, so the tooltip only says when it was, without repeating it. Seconds are
    /// noise next to an age rounded to minutes, and the time zone makes the time unambiguous when compared with others.
    /// </remarks>
    public static string FormatTooltipDateTime(TimeProvider timeProvider, DateTime value, CultureInfo? cultureInfo = null)
    {
        cultureInfo ??= CultureInfo.CurrentCulture;
        var local = timeProvider.ToLocal(value);
        var pattern = DateFormatStringsHelpers.GetMediumDateShortTimePattern(cultureInfo, GetResolvedTimeFormat(timeProvider));

        return $"{local.ToString(pattern, cultureInfo)} {GetTimeZoneShortName(timeProvider, GetUtcOffset(timeProvider, value))}";
    }

    private static TimeSpan GetUtcOffset(TimeProvider timeProvider, DateTime value)
    {
        // Read the offset from the UTC instant: the local time is ambiguous in the hour repeated when daylight saving time
        // ends, for example 1:30 AM on Nov 1, 2026 in America/Los_Angeles is both 08:30Z (PDT) and 09:30Z (PST).
        if (value.Kind == DateTimeKind.Utc)
        {
            return timeProvider.LocalTimeZone.GetUtcOffset(value);
        }

        // A Local value is already in the provider's zone (see ToLocal), so its instant is lost and the repeated hour reads
        // as standard time. TimeZoneInfo reads a Local DateTime in the machine's zone, so pass it as Unspecified to read it
        // in the provider's zone.
        return timeProvider.LocalTimeZone.GetUtcOffset(DateTime.SpecifyKind(value, DateTimeKind.Unspecified));
    }

    public static string FormatTooltipDateTime(TimeProvider timeProvider, DateTimeOffset value, CultureInfo? cultureInfo = null) =>
        FormatTooltipDateTime(timeProvider, value.UtcDateTime, cultureInfo);

    private static string GetTimeZoneShortName(TimeProvider timeProvider, TimeSpan offset)
    {
        if (timeProvider is ITimeZoneNameProvider { } nameProvider && nameProvider.GetTimeZoneShortName(offset) is { Length: > 0 } name)
        {
            return name;
        }

        // .NET has no time zone abbreviations ("PST"), only long names, so without the browser's own name fall back to
        // the offset, the way browsers name zones without a well-known abbreviation ("GMT+1").
        if (offset == TimeSpan.Zero)
        {
            return "UTC";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var duration = offset.Duration();
        return duration.Minutes == 0
            ? string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{duration.Hours}")
            : string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{duration.Hours}:{duration.Minutes:00}");
    }

    private static TimeFormat GetResolvedTimeFormat(TimeProvider timeProvider)
    {
        if (timeProvider is ITimeFormatProvider tfp)
        {
            return tfp.ResolvedTimeFormat;
        }

        return TimeFormat.System;
    }

    public static string FormatTimeWithOptionalDate(TimeProvider timeProvider, DateTime value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null)
    {
        var local = timeProvider.ToLocal(value);

        // If the date is today then only return time, otherwise return entire date time text.
        if (local.Date == timeProvider.GetLocalNow().Date)
        {
            // e.g. "08:57:44" (based on user's culture and preferences)
            // Don't include milliseconds as resource server returned time stamp is second precision.
            return FormatTime(timeProvider, value, millisecondsDisplay, cultureInfo);
        }
        else
        {
            // e.g. "9/02/2024 08:57:44" (based on user's culture and preferences)
            return FormatDateTime(timeProvider, value, millisecondsDisplay, cultureInfo);
        }
    }

    public static string FormatTimeWithOptionalDate(TimeProvider timeProvider, DateTimeOffset value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null) =>
        FormatTimeWithOptionalDate(timeProvider, value.UtcDateTime, millisecondsDisplay, cultureInfo);

    public static string FormatNumberWithOptionalDecimalPlaces(double value, int maxDecimalPlaces, CultureInfo? provider = null)
    {
        var formatString = maxDecimalPlaces switch
        {
            1 => "##,0.#",
            2 => "##,0.##",
            3 => "##,0.###",
            4 => "##,0.####",
            5 => "##,0.#####",
            6 => "##,0.######",
            _ => throw new ArgumentException("Unexpected value.", nameof(maxDecimalPlaces))
        };
        return value.ToString(formatString, provider ?? CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Truncates text to at most <paramref name="maxLength"/> UTF-16 code units, including the ellipsis,
    /// without splitting surrogate pairs.
    /// </summary>
    public static string TruncateText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.Length <= maxLength)
        {
            return text;
        }

        var retainedLength = maxLength - Ellipsis.Length;
        if (retainedLength > 0 && char.IsHighSurrogate(text[retainedLength - 1]) && char.IsLowSurrogate(text[retainedLength]))
        {
            retainedLength--;
        }

        return string.Concat(text.AsSpan(0, retainedLength), Ellipsis);
    }

    public static string CombineWithSeparator(string separator, params string?[] parts)
    {
        return string.Join(separator, parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    public static string FormatFileSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB"
        };
    }
}
