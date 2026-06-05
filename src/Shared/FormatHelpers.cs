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

    public static string FormatDateTime(TimeProvider timeProvider, DateTime value, MillisecondsDisplay millisecondsDisplay = MillisecondsDisplay.None, CultureInfo? cultureInfo = null)
    {
        cultureInfo ??= CultureInfo.CurrentCulture;
        var local = timeProvider.ToLocal(value);
        var timeFormat = GetResolvedTimeFormat(timeProvider);
        var pattern = DateFormatStringsHelpers.GetShortDateLongTimePattern(cultureInfo, timeFormat, millisecondsDisplay);

        return local.ToString(pattern, cultureInfo);
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
        if (local.Date == DateTime.Now.Date)
        {
            // e.g. "08:57:44" (based on user's culture and preferences)
            // Don't include milliseconds as resource server returned time stamp is second precision.
            return FormatTime(timeProvider, local, millisecondsDisplay, cultureInfo);
        }
        else
        {
            // e.g. "9/02/2024 08:57:44" (based on user's culture and preferences)
            return FormatDateTime(timeProvider, local, millisecondsDisplay, cultureInfo);
        }
    }

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

        return string.Concat(text.AsSpan(0, maxLength - Ellipsis.Length), Ellipsis);
    }

    public static string CombineWithSeparator(string separator, params string?[] parts)
    {
        return string.Join(separator, parts.Where(p => !string.IsNullOrEmpty(p)));
    }
}
