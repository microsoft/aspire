// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Shared;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DurationFormatterTests
{
    [Theory]
    [InlineData(0, "μs")]
    [InlineData(1, "μs")]
    [InlineData(10, "μs")]
    [InlineData(100, "ms")]
    [InlineData(1_000, "ms")]
    [InlineData(100_000, "ms")]
    [InlineData(1_000_000, "s")]
    [InlineData(1_000_000_000, "s")]
    [InlineData(1_000_000_000_000, "h")]
    [InlineData(1_000_000_000_000_000, "h")]
    [InlineData(1_000_000_000_000_000_000, "h")]
    public void GetUnit(long ticks, string unit)
    {
        Assert.Equal(unit, DurationFormatter.GetUnit(TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void KeepsMicrosecondsTheSame()
    {
        Assert.Equal("1μs", DurationFormatter.FormatDuration(TimeSpan.FromTicks(1 * TimeSpan.TicksPerMicrosecond), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysMaximumOf2UnitsAndRoundsLastOne()
    {
        var input = 10 * TimeSpan.TicksPerDay + 13 * TimeSpan.TicksPerHour + 30 * TimeSpan.TicksPerMinute;
        Assert.Equal("10d 14h", DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void SkipsUnitsThatAreEmpty()
    {
        var input = 2 * TimeSpan.TicksPerDay + 5 * TimeSpan.TicksPerMinute;
        Assert.Equal("2d", DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysMillisecondsInDecimals()
    {
        var input = 2 * TimeSpan.TicksPerMillisecond + 357 * TimeSpan.TicksPerMicrosecond;
        Assert.Equal(2.36m.ToString("0.##ms", CultureInfo.CurrentCulture), DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysSecondsInDecimals()
    {
        var input = 2 * TimeSpan.TicksPerSecond + 357 * TimeSpan.TicksPerMillisecond;
        Assert.Equal(2.36m.ToString("0.##s", CultureInfo.CurrentCulture), DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysMinutesInSplitUnits()
    {
        var input = 2 * TimeSpan.TicksPerMinute + 30 * TimeSpan.TicksPerSecond + 555 * TimeSpan.TicksPerMillisecond;
        Assert.Equal("2m 31s", DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysHoursInSplitUnits()
    {
        var input = 2 * TimeSpan.TicksPerHour + 30 * TimeSpan.TicksPerMinute + 30 * TimeSpan.TicksPerSecond;
        Assert.Equal("2h 31m", DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysLargeFractionalMillisecondAsMilliseconds()
    {
        var input = 9155;
        Assert.Equal(0.92m.ToString("0.##ms", CultureInfo.CurrentCulture), DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysLargeFractionalSecondsAsSeconds()
    {
        var input = 915 * TimeSpan.TicksPerMillisecond;
        Assert.Equal(0.92m.ToString("0.##s", CultureInfo.CurrentCulture), DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysTimesLessThanMicroseconds()
    {
        var input = (double)TimeSpan.TicksPerMicrosecond / 10;
        Assert.Equal(0.1m.ToString("0.##μs", CultureInfo.CurrentCulture), DurationFormatter.FormatDuration(TimeSpan.FromTicks((long)input), CultureInfo.CurrentCulture));
    }

    [Fact]
    public void DisplaysTimesOf0()
    {
        var input = 0;
        Assert.Equal("0μs", DurationFormatter.FormatDuration(TimeSpan.FromTicks(input), CultureInfo.CurrentCulture));
    }

    [Theory]
    [InlineData(60, "1m")]        // 1 minute (no seconds displayed when 0)
    [InlineData(90, "1m 30s")]    // 1 minute 30 seconds
    [InlineData(150, "2m 30s")]   // 2 minutes 30 seconds
    [InlineData(3600, "1h")]      // 1 hour (no minutes when 0)
    [InlineData(3661, "1h 1m")]   // 1 hour 1 minute 1 second (rounds to 1m)
    [InlineData(7200, "2h")]      // 2 hours (no minutes when 0)
    public void FormatDuration_FormatsLongerDurationsWithMultipleUnits(double seconds, string expected)
    {
        // Act
        var result = DurationFormatter.FormatDuration(TimeSpan.FromSeconds(seconds), CultureInfo.InvariantCulture);
        
        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDuration_HandlesVeryLargeDurations()
    {
        // Test that large durations are formatted with appropriate units
        var oneDayInSeconds = 24 * 60 * 60;
        
        // Act
        var result = DurationFormatter.FormatDuration(TimeSpan.FromSeconds(oneDayInSeconds));
        
        // Assert - should show days (no hours when 0)
        Assert.Equal("1d", result);
    }

    [Fact]
    public void FormatDuration_HandlesMixedUnitsCorrectly()
    {
        // Test the example from the original DurationFormatter tests
        var duration = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(30) + TimeSpan.FromMilliseconds(555);
        
        // Act
        var result = DurationFormatter.FormatDuration(duration);
        
        // Assert - should round seconds appropriately
        Assert.Equal("2m 31s", result);
    }

    [Theory]
    [InlineData(0.001, "1.00ms")]   // 1 millisecond with fixed decimals
    [InlineData(0.025, "25.00ms")]  // 25 milliseconds with fixed decimals
    [InlineData(0.1, "0.10s")]      // 0.1 seconds with fixed decimals
    [InlineData(1.0, "1.00s")]      // 1 second with fixed decimals
    [InlineData(1.5, "1.50s")]      // 1.5 seconds with fixed decimals
    [InlineData(0.0, "0.00μs")]     // Zero with fixed decimals
    public void FormatDuration_WithFixedDisplay_AlwaysShowsTwoDecimalPlaces(double seconds, string expected)
    {
        // Act - explicitly use DecimalDurationDisplay.Fixed
        var result = DurationFormatter.FormatDuration(
            TimeSpan.FromSeconds(seconds), 
            CultureInfo.InvariantCulture, 
            DecimalDurationDisplay.Fixed);
        
        // Assert - should always have .00 format
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.001, "1ms")]      // 1 millisecond without trailing zeros
    [InlineData(0.025, "25ms")]     // 25 milliseconds without trailing zeros
    [InlineData(0.1, "0.1s")]       // 0.1 seconds without trailing zeros
    [InlineData(1.0, "1s")]         // 1 second without trailing zeros
    [InlineData(1.5, "1.5s")]       // 1.5 seconds shows one decimal
    [InlineData(0.0, "0μs")]        // Zero without trailing zeros
    [InlineData(1.25, "1.25s")]     // 1.25 seconds shows two decimals
    public void FormatDuration_WithOptionalDisplay_HidesTrailingZeros(double seconds, string expected)
    {
        // Act - explicitly use DecimalDurationDisplay.Optional (also the default)
        var result = DurationFormatter.FormatDuration(
            TimeSpan.FromSeconds(seconds), 
            CultureInfo.InvariantCulture, 
            DecimalDurationDisplay.Optional);
        
        // Assert - should use 0.## format, hiding trailing zeros
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDuration_DefaultBehavior_UsesOptionalDisplay()
    {
        // Verify that the default parameter value is Optional
        var duration = TimeSpan.FromSeconds(1.0);
        
        // Act - call without specifying the parameter
        var defaultResult = DurationFormatter.FormatDuration(duration, CultureInfo.InvariantCulture);
        var explicitOptionalResult = DurationFormatter.FormatDuration(duration, CultureInfo.InvariantCulture, DecimalDurationDisplay.Optional);
        
        // Assert - both should produce the same result
        Assert.Equal(explicitOptionalResult, defaultResult);
        Assert.Equal("1s", defaultResult); // Should not have trailing zeros
    }
}

