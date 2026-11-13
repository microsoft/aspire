// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model;

/// <summary>
/// This time provider is used to provide the time zone information from the browser to the server.
/// It is a different type because we want to log setting the time zone, and we want a distinct type
/// to register with DI:
/// - BrowserTimeProvider must be scoped to the user's session.
/// - The built-in TimeProvider registration must be singleton for the system time (used by auth).
/// </summary>
public class BrowserTimeProvider : TimeProvider, ITimeFormatProvider, ITimeZoneNameProvider
{
    private readonly ILogger _logger;
    private TimeZoneInfo? _browserLocalTimeZone;
    private IReadOnlyList<BrowserTimeZoneShortName> _timeZoneShortNames = [];
    private TimeFormat? _browserTimeFormat;

    public TimeFormat ConfiguredTimeFormat { get; set; } = TimeFormat.System;

    public BrowserTimeProvider(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger(typeof(BrowserTimeProvider));
    }

    public override TimeZoneInfo LocalTimeZone
    {
        get => _browserLocalTimeZone ?? base.LocalTimeZone;
    }

    /// <summary>
    /// Sets the browser's time zone, with the short names the browser gives it at the offsets it sampled, such as "PST"
    /// at -08:00 and "PDT" at -07:00. .NET only has long time zone names, so the short names have to come from the browser.
    /// </summary>
    public void SetBrowserTimeZone(string? timeZone, IReadOnlyList<BrowserTimeZoneShortName>? shortNames)
    {
        if (string.IsNullOrEmpty(timeZone) || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var timeZoneInfo))
        {
            _logger.LogWarning("Couldn't find time zone '{TimeZone}'. Defaulting to UTC.", timeZone);
            timeZoneInfo = TimeZoneInfo.Utc;

            // The names describe the browser's zone, not UTC, so they would mislabel times shown in UTC.
            shortNames = null;
        }

        _timeZoneShortNames = shortNames ?? [];

        _logger.LogDebug("Browser time zone set to '{TimeZone}' with UTC offset {UtcOffset}.", timeZoneInfo.Id, timeZoneInfo.BaseUtcOffset);
        _browserLocalTimeZone = timeZoneInfo;
    }

    public string? GetTimeZoneShortName(TimeSpan utcOffset)
    {
        // Without the browser's zone, LocalTimeZone is the server's, which the browser's names don't describe.
        if (_browserLocalTimeZone is null)
        {
            return null;
        }

        // Match on the offset rather than on whether daylight saving time applies: the browser only samples two dates, and
        // a zone can be at other offsets in between, such as Africa/Casablanca at UTC+0 during Ramadan while it is UTC+1 in
        // both January and July. A time at an offset the browser didn't name falls back to the offset ("UTC").
        foreach (var shortName in _timeZoneShortNames)
        {
            if (shortName.OffsetMinutes == (int)utcOffset.TotalMinutes)
            {
                return shortName.Name;
            }
        }

        return null;
    }

    public TimeFormat ResolvedTimeFormat
    {
        get
        {
            if (ConfiguredTimeFormat == TimeFormat.System)
            {
                return _browserTimeFormat ?? TimeFormat.System;
            }

            return ConfiguredTimeFormat;
        }
    }

    public void SetBrowserTimeFormat(TimeFormat timeFormat)
    {
        _browserTimeFormat = timeFormat;
    }

    public void SetConfiguredTimeFormat(TimeFormat timeFormat)
    {
        ConfiguredTimeFormat = timeFormat;
    }

    /// <summary>
    /// Determines whether the specified time format uses a 24-hour clock.
    /// </summary>
    internal static bool Is24HourFormat(TimeFormat timeFormat)
    {
        return timeFormat switch
        {
            TimeFormat.TwentyFourHour => true,
            TimeFormat.TwelveHour => false,
            _ => DateTimeFormatInfo.CurrentInfo.LongTimePattern.StartsWith('H')
        };
    }
}
