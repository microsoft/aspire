// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class FormatHelpersTests
{
    [Theory]
    [InlineData("9", 9d)]
    [InlineData("9.9", 9.9d)]
    [InlineData("0.9", 0.9d)]
    [InlineData("12,345,678.9", 12345678.9d)]
    [InlineData("1.234568", 1.23456789d)]
    public void FormatNumberWithOptionalDecimalPlaces_InvariantCulture(string expected, double value)
    {
        Assert.Equal(expected, FormatHelpers.FormatNumberWithOptionalDecimalPlaces(value, maxDecimalPlaces: 6, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("9", 9d)]
    [InlineData("9,9", 9.9d)]
    [InlineData("0,9", 0.9d)]
    [InlineData("12.345.678,9", 12345678.9d)]
    [InlineData("1,234568", 1.23456789d)]
    public void FormatNumberWithOptionalDecimalPlaces_GermanCulture(string expected, double value)
    {
        Assert.Equal(expected, FormatHelpers.FormatNumberWithOptionalDecimalPlaces(value, maxDecimalPlaces: 6, CultureInfo.GetCultureInfo("de-DE")));
    }

    [Theory]
    [InlineData("06/15/2009 13:45:30.000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("06/15/2009 13:45:30.123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("06/15/2009 13:45:30.1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("06/15/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("06/15/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("06/15/2009 1:45:30.000 PM", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("06/15/2009 1:45:30.123 PM", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("06/15/2009 1:45:30.1234567 PM", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("06/15/2009 1:45:30 PM", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("06/15/2009 1:45:30 PM", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("06/15/2009 13:45:30.000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("06/15/2009 13:45:30.123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("06/15/2009 13:45:30.1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("06/15/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("06/15/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    public void FormatDateTime_WithMilliseconds_InvariantCulture(string expected, MillisecondsDisplay includeMilliseconds, string value, TimeFormat timeFormat)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(timeFormat);
        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, includeMilliseconds, cultureInfo: CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("15.06.2009 13:45:30,000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.06.2009 13:45:30,123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.06.2009 13:45:30,1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.06.2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.06.2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.06.2009 1:45:30,000 PM", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15.06.2009 1:45:30,123 PM", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.06.2009 1:45:30,1234567 PM", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.06.2009 1:45:30 PM", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15.06.2009 1:45:30 PM", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.06.2009 13:45:30,000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15.06.2009 13:45:30,123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15.06.2009 13:45:30,1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15.06.2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15.06.2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    public void FormatDateTime_WithMilliseconds_GermanCulture(string expected, MillisecondsDisplay includeMilliseconds, string value, TimeFormat timeFormat)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(timeFormat);
        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, includeMilliseconds, cultureInfo: CultureInfo.GetCultureInfo("de-DE")));
    }

    [Theory]
    [InlineData("15.6.2009 13.45.30,000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.6.2009 13.45.30,123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.6.2009 13.45.30,1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.6.2009 13.45.30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.6.2009 13.45.30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15.6.2009 1.45.30,000 ip.", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15.6.2009 1.45.30,123 ip.", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.6.2009 1.45.30,1234567 ip.", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.6.2009 1.45.30 ip.", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15.6.2009 1.45.30 ip.", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15.6.2009 13.45.30,000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15.6.2009 13.45.30,123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15.6.2009 13.45.30,1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15.6.2009 13.45.30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15.6.2009 13.45.30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    public void FormatDateTime_WithMilliseconds_FinnishCulture(string expected, MillisecondsDisplay includeMilliseconds, string value, TimeFormat timeFormat)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(timeFormat);
        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, includeMilliseconds, cultureInfo: CultureInfo.GetCultureInfo("fi-FI")));
    }

    [Theory]
    [InlineData("15/06/2009 1:45:30.000 pm", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15/06/2009 1:45:30.123 pm", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15/06/2009 1:45:30.1234567 pm", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15/06/2009 1:45:30 pm", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour)]
    [InlineData("15/06/2009 1:45:30 pm", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwelveHour)]
    [InlineData("15/06/2009 13:45:30.000", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15/06/2009 13:45:30.123", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15/06/2009 13:45:30.1234567", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15/06/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour)]
    [InlineData("15/06/2009 13:45:30", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.TwentyFourHour)]
    [InlineData("15/06/2009 1:45:30.000 pm", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15/06/2009 1:45:30.123 pm", MillisecondsDisplay.Truncated, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15/06/2009 1:45:30.1234567 pm", MillisecondsDisplay.Full, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    [InlineData("15/06/2009 1:45:30 pm", MillisecondsDisplay.None, "2009-06-15T13:45:30.0000000Z", TimeFormat.System)]
    [InlineData("15/06/2009 1:45:30 pm", MillisecondsDisplay.None, "2009-06-15T13:45:30.1234567Z", TimeFormat.System)]
    public void FormatDateTime_WithMilliseconds_NewZealandCulture(string expected, MillisecondsDisplay includeMilliseconds, string value, TimeFormat timeFormat)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(timeFormat);
        // macOS formats with uppercase AM/PM, so ignore case
        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, includeMilliseconds, cultureInfo: CultureInfo.GetCultureInfo("en-NZ")), ignoreWhiteSpaceDifferences: true, ignoreCase: true);
    }

    [Theory]
    [InlineData(null, 5, "")]
    [InlineData("", 5, "")]
    [InlineData("abcdef", 5, "abcd" + FormatHelpers.Ellipsis)]
    [InlineData("abcdef", 10, "abcdef")]
    [InlineData("abcdef", 6, "abcdef")]
    [InlineData("abcdef", 1, FormatHelpers.Ellipsis)]
    [InlineData("\U0001F600", 2, "\U0001F600")]
    [InlineData("ab\U0001F600", 4, "ab\U0001F600")]
    [InlineData("ab\U0001F600tail", 4, "ab" + FormatHelpers.Ellipsis)]
    [InlineData("a\U0001F600tail", 4, "a\U0001F600" + FormatHelpers.Ellipsis)]
    [InlineData("ab\U0001F600tail", 3, "ab" + FormatHelpers.Ellipsis)]
    [InlineData("\U0001F600tail", 2, FormatHelpers.Ellipsis)]
    [InlineData("\U0001F600tail", 1, FormatHelpers.Ellipsis)]
    public void TruncateText(string? initialText, int maxLength, string expected)
    {
        Assert.Equal(expected, FormatHelpers.TruncateText(initialText, maxLength: maxLength));
    }

    [Theory]
    [InlineData("Nov 11, 2026, 12:41 PM PST", "2026-11-11T20:41:00.0000000Z", TimeFormat.System)]
    [InlineData("Jul 1, 2026, 12:41 PM PDT", "2026-07-01T19:41:00.0000000Z", TimeFormat.System)]
    [InlineData("Nov 11, 2026, 12:41 PST", "2026-11-11T20:41:00.0000000Z", TimeFormat.TwentyFourHour)]
    // 1:30 AM happens twice on Nov 1, 2026 when daylight saving time ends; the instant says which one it is.
    [InlineData("Nov 1, 2026, 1:30 AM PDT", "2026-11-01T08:30:00.0000000Z", TimeFormat.System)]
    [InlineData("Nov 1, 2026, 1:30 AM PST", "2026-11-01T09:30:00.0000000Z", TimeFormat.System)]
    public void FormatTooltipDateTime_UsesBrowserTimeZoneShortNames(string expected, string value, TimeFormat timeFormat)
    {
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeZone("America/Los_Angeles", s_losAngelesShortNames);
        provider.SetConfiguredTimeFormat(timeFormat);

        var date = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(expected, FormatHelpers.FormatTooltipDateTime(provider, date, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData("en-US", TimeFormat.System, "Nov 11, 2026, 8:41 PM UTC")]
    [InlineData("en-US", TimeFormat.TwentyFourHour, "Nov 11, 2026, 20:41 UTC")]
    [InlineData("fr-FR", TimeFormat.System, "11 nov. 2026, 20:41 UTC")]
    [InlineData("ja-JP", TimeFormat.System, "2026年11月11日 20:41 UTC")]
    public void FormatTooltipDateTime_FollowsCulture(string cultureName, TimeFormat timeFormat, string expected)
    {
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeZone("UTC", shortNames: null);
        provider.SetConfiguredTimeFormat(timeFormat);

        var date = new DateTime(2026, 11, 11, 20, 41, 0, DateTimeKind.Utc);
        Assert.Equal(expected, FormatHelpers.FormatTooltipDateTime(provider, date, CultureInfo.GetCultureInfo(cultureName)), ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData("Asia/Kolkata", "Nov 12, 2026, 2:11 AM UTC+5:30")]
    [InlineData("America/New_York", "Nov 11, 2026, 3:41 PM UTC-5")]
    public void FormatTooltipDateTime_WithoutShortNames_UsesUtcOffset(string timeZone, string expected)
    {
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeZone(timeZone, shortNames: null);

        var date = new DateTime(2026, 11, 11, 20, 41, 0, DateTimeKind.Utc);
        Assert.Equal(expected, FormatHelpers.FormatTooltipDateTime(provider, date, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void FormatTooltipDateTime_OffsetWithoutShortName_UsesUtcOffset()
    {
        // Modeled on Africa/Casablanca, which is UTC+1 in both January and July, the dates the browser samples, but UTC+0
        // during Ramadan. A custom zone keeps the test independent of each operating system's time zone data.
        var ramadan = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(-1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 2, 15),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 22));
        var casablanca = TimeZoneInfo.CreateCustomTimeZone("Test/Casablanca", TimeSpan.FromHours(1), "Test/Casablanca", "Test/Casablanca", "Test/Casablanca Ramadan", [ramadan]);
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeZone(casablanca, [new BrowserTimeZoneShortName { OffsetMinutes = 60, Name = "GMT+1" }]);

        Assert.Equal("Jan 15, 2026, 1:00 PM GMT+1", FormatHelpers.FormatTooltipDateTime(provider, new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc), CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
        Assert.Equal("Mar 1, 2026, 12:00 PM UTC", FormatHelpers.FormatTooltipDateTime(provider, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void FormatTooltipDateTime_UnknownTimeZone_IgnoresShortNames()
    {
        // The dashboard falls back to UTC for a time zone it can't find, so the browser's names would mislabel the time.
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeZone("Not/A_Zone", s_losAngelesShortNames);

        var date = new DateTime(2026, 11, 11, 20, 41, 0, DateTimeKind.Utc);
        Assert.Equal("Nov 11, 2026, 8:41 PM UTC", FormatHelpers.FormatTooltipDateTime(provider, date, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    private static DateTime GetLocalDateTime(string value)
    {
        Assert.True(DateTime.TryParseExact(value, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date));
        Assert.Equal(DateTimeKind.Utc, date.Kind);
        date = DateTime.SpecifyKind(date, DateTimeKind.Local);
        return date;
    }

    [Theory]
    [InlineData("2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour, "1:45:30 PM")]
    [InlineData("2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour, "13:45:30")]
    public void FormatTime_WithTimeFormatPreference(string value, TimeFormat format, string expected)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(format);

        // Use a culture that would normally be opposite
        var culture = format == TimeFormat.TwelveHour ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-US");

        Assert.Equal(expected, FormatHelpers.FormatTime(provider, date, MillisecondsDisplay.None, culture));
    }

    [Theory]
    [InlineData("2009-06-15T13:45:30.0000000Z", TimeFormat.TwelveHour, "6/15/2009 1:45:30 PM")] // en-US date pattern + 12h time
    [InlineData("2009-06-15T13:45:30.0000000Z", TimeFormat.TwentyFourHour, "6/15/2009 13:45:30")] // en-US date pattern + 24h time
    public void FormatDateTime_WithTimeFormatPreference_EnUS(string value, TimeFormat format, string expected)
    {
        var date = GetLocalDateTime(value);
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(format);

        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData("2025-12-20T13:45:30.0000000Z", "1:45:30 PM")]
    [InlineData("2025-12-19T13:45:30.0000000Z", "12/19/2025 1:45:30 PM")]
    public void FormatTimeWithOptionalDate_UsesTimeProviderCurrentDate(string value, string expected)
    {
        var provider = new FixedTimeProvider(new DateTimeOffset(2025, 12, 20, 23, 59, 59, TimeSpan.Zero));
        var date = DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        Assert.Equal(expected, FormatHelpers.FormatTimeWithOptionalDate(provider, date, cultureInfo: CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData("fi-FI", TimeFormat.TwentyFourHour, MillisecondsDisplay.None, "15.6.2009 13.45.30")]
    [InlineData("fi-FI", TimeFormat.TwentyFourHour, MillisecondsDisplay.Truncated, "15.6.2009 13.45.30,123")]
    [InlineData("fi-FI", TimeFormat.TwelveHour, MillisecondsDisplay.None, "15.6.2009 1.45.30 ip.")]
    [InlineData("fi-FI", TimeFormat.TwelveHour, MillisecondsDisplay.Truncated, "15.6.2009 1.45.30,123 ip.")]
    [InlineData("fi-FI", TimeFormat.System, MillisecondsDisplay.None, "15.6.2009 13.45.30")]
    [InlineData("fi-FI", TimeFormat.System, MillisecondsDisplay.Truncated, "15.6.2009 13.45.30,123")]
    [InlineData("de-DE", TimeFormat.TwentyFourHour, MillisecondsDisplay.Truncated, "15.06.2009 13:45:30,123")]
    [InlineData("de-DE", TimeFormat.TwelveHour, MillisecondsDisplay.Truncated, "15.06.2009 1:45:30,123 PM")]
    [InlineData("de-DE", TimeFormat.System, MillisecondsDisplay.Truncated, "15.06.2009 13:45:30,123")]
    [InlineData("en-US", TimeFormat.TwentyFourHour, MillisecondsDisplay.Truncated, "6/15/2009 13:45:30.123")]
    [InlineData("en-US", TimeFormat.TwelveHour, MillisecondsDisplay.Truncated, "6/15/2009 1:45:30.123 PM")]
    [InlineData("en-US", TimeFormat.System, MillisecondsDisplay.Truncated, "6/15/2009 1:45:30.123 PM")]
    public void FormatDateTime_WithTimeFormatPreference_UsesCultureSeparators(string cultureName, TimeFormat format, MillisecondsDisplay includeMilliseconds, string expected)
    {
        var date = GetLocalDateTime("2009-06-15T13:45:30.1234567Z");
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(format);

        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, includeMilliseconds, CultureInfo.GetCultureInfo(cultureName)), ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void FormatTime_TwelveHour_UsesAmPm_EvenIfCultureIs24Hour()
    {
        // en-GB is typically 24-hour.
        var date = GetLocalDateTime("2009-06-15T13:45:30.0000000Z");
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(TimeFormat.TwelveHour);

        // en-GB has AM/PM designators "am"/"pm" even if standard pattern is 24h.
        var result = FormatHelpers.FormatTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("en-GB"));
        Assert.Contains("pm", result.ToLowerInvariant());
        Assert.DoesNotContain("13", result);
    }

    [Fact]
    public void FormatTime_TwentyFourHour_Uses13_EvenIfCultureIs12Hour()
    {
        // en-US is typically 12-hour.
        var date = GetLocalDateTime("2009-06-15T13:45:30.0000000Z");
        var provider = CreateTimeProvider();
        provider.SetConfiguredTimeFormat(TimeFormat.TwentyFourHour);

        var result = FormatHelpers.FormatTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("en-US"));
        Assert.Contains("13", result);
        Assert.DoesNotContain("PM", result);
    }

    [Theory]
    [InlineData(TimeFormat.TwentyFourHour, "13:45:30")]   // Browser reports 24-hour → use 24-hour
    [InlineData(TimeFormat.TwelveHour, "1:45:30 PM")] // Browser reports 12-hour → use 12-hour
    public void FormatTime_SystemFormat_UsesBrowserTimeFormat(TimeFormat browserTimeFormat, string expected)
    {
        var date = GetLocalDateTime("2009-06-15T13:45:30.0000000Z");
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeFormat(browserTimeFormat);
        // TimeFormat stays at System (default)

        Assert.Equal(expected, FormatHelpers.FormatTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Theory]
    [InlineData(TimeFormat.TwentyFourHour, "6/15/2009 13:45:30")]   // Browser reports 24-hour
    [InlineData(TimeFormat.TwelveHour, "6/15/2009 1:45:30 PM")] // Browser reports 12-hour
    public void FormatDateTime_SystemFormat_UsesBrowserTimeFormat(TimeFormat browserTimeFormat, string expected)
    {
        var date = GetLocalDateTime("2009-06-15T13:45:30.0000000Z");
        var provider = CreateTimeProvider();
        provider.SetBrowserTimeFormat(browserTimeFormat);

        Assert.Equal(expected, FormatHelpers.FormatDateTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("en-US")), ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void FormatTime_SystemFormat_NoBrowserFormat_FallsBackToCulture()
    {
        // When browser hasn't reported its format yet, fall back to culture-based formatting.
        var date = GetLocalDateTime("2009-06-15T13:45:30.0000000Z");
        var provider = CreateTimeProvider();
        // Don't call SetBrowserTimeFormat — BrowserTimeFormat stays null

        // de-DE is a 24-hour culture, so the culture pattern should be used.
        var result = FormatHelpers.FormatTime(provider, date, MillisecondsDisplay.None, CultureInfo.GetCultureInfo("de-DE"));
        Assert.Contains("13", result);
    }

    private static readonly BrowserTimeZoneShortName[] s_losAngelesShortNames =
    [
        new() { OffsetMinutes = -480, Name = "PST" },
        new() { OffsetMinutes = -420, Name = "PDT" }
    ];

    private static BrowserTimeProvider CreateTimeProvider()
    {
        return new BrowserTimeProvider(NullLoggerFactory.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
