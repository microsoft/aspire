// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components;
using Aspire.Dashboard.Components.Controls.Chart;
using Aspire.Dashboard.Otlp.Model;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class SparklinePathTests
{
    [Fact]
    public void Build_NoValues_ReturnsEmptyPaths()
    {
        var (line, area) = SparklinePath.Build([null, null, null], width: 100, height: 32);

        Assert.Equal(string.Empty, line);
        Assert.Equal(string.Empty, area);
    }

    [Fact]
    public void Build_FlatZeroValues_DrawsLineOnBaseline()
    {
        var (line, area) = SparklinePath.Build([0, 0, 0], width: 100, height: 32);

        Assert.Equal("M0,30C16.67,30 33.33,30 50,30C66.67,30 83.33,30 100,30", line);
        Assert.Equal(line + "L100,30L0,30Z", area);
    }

    [Fact]
    public void Build_FlatNonZeroValues_DrawsLineAtTop()
    {
        // Zero-based: a constant 5 is the maximum of a 0..5 range, so it draws at the top inset
        // rather than on the baseline where it would look like zero.
        var (line, area) = SparklinePath.Build([5, 5, 5], width: 100, height: 32);

        Assert.Equal("M0,2C16.67,2 33.33,2 50,2C66.67,2 83.33,2 100,2", line);
        Assert.Equal(line + "L100,30L0,30Z", area);
    }

    [Fact]
    public void Build_ScalesFromZero()
    {
        // The range is 0..20, so 10 maps halfway (16) and 20 to the top inset (2).
        var (line, _) = SparklinePath.Build([10, 20], width: 100, height: 32);

        Assert.Equal("M0,16C33.33,11.33 66.67,6.67 100,2", line);
    }

    [Fact]
    public void Build_NegativeValues_AreaClosesAtZeroLine()
    {
        // The range is -10..10, so the zero line is in the middle (16).
        var (line, area) = SparklinePath.Build([-10, 10], width: 100, height: 32);

        Assert.Equal("M0,30C33.33,20.67 66.67,11.33 100,2", line);
        Assert.Equal(line + "L100,16L0,16Z", area);
    }

    [Fact]
    public void Build_GapInValues_StartsNewSegment()
    {
        var (line, area) = SparklinePath.Build([1, 2, null, 2, 1], width: 100, height: 32);

        Assert.Equal("M0,16C8.33,11.33 16.67,6.67 25,2M75,2C83.33,6.67 91.67,11.33 100,16", line);
        Assert.Equal(
            "M0,16C8.33,11.33 16.67,6.67 25,2L25,30L0,30Z" +
            "M75,2C83.33,6.67 91.67,11.33 100,16L100,30L75,30Z",
            area);
    }

    [Fact]
    public void BuildBars_NullAndZeroValues_DrawNoBar()
    {
        // Four slots of 25 with 70% wide bars centered in each. 5 is half of the 0..10 range.
        var bars = SparklinePath.BuildBars([0, 5, null, 10], width: 100, height: 32);

        Assert.Equal("M28.75,30V16H46.25V30ZM78.75,30V2H96.25V30Z", bars);
    }

    [Fact]
    public void BuildBars_TinyValue_KeepsMinimumHeight()
    {
        var bars = SparklinePath.BuildBars([1, 1000], width: 100, height: 32);

        Assert.Equal("M7.5,30V28.5H42.5V30ZM57.5,30V2H92.5V30Z", bars);
    }

    [Fact]
    public void BuildBars_NoValues_ReturnsEmptyPath()
    {
        Assert.Equal(string.Empty, SparklinePath.BuildBars([null, null], width: 100, height: 32));
    }

    [Fact]
    public void CalculateCounterIncreases_Cumulative_DiffsLastTotalAndHandlesResets()
    {
        // 15 -> 3 is a reset (process restart): the total restarted from zero, so the increase is 3.
        // The last total is carried across the empty interval, so 3 -> 8 lands on the point after it.
        var increases = ChartDataCalculator.CalculateCounterIncreases([null, 10, 15, 15, 3, null, 8, 9], OtlpAggregationTemporality.Cumulative);

        Assert.Equal([null, null, 5, 0, 3, null, 5, 1], increases);
    }

    [Fact]
    public void CalculateCounterIncreases_Delta_ReturnsValues()
    {
        var increases = ChartDataCalculator.CalculateCounterIncreases([1, null, 2], OtlpAggregationTemporality.Delta);

        Assert.Equal([1, null, 2], increases);
    }

    [Fact]
    public void Build_StepChange_DoesNotOvershoot()
    {
        // Monotone interpolation keeps every control point within the data's vertical range,
        // so a step from 0 to 10 never draws a bump above the top or below the bottom.
        var (line, _) = SparklinePath.Build([0, 0, 0, 10, 10, 10], width: 100, height: 32);

        var coordinates = line
            .Split(['M', 'C', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(point => double.Parse(point.Split(',')[1], CultureInfo.InvariantCulture))
            .ToList();

        Assert.All(coordinates, y => Assert.InRange(y, 2, 30));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.0042, "0.004")]
    [InlineData(999.5, "999.5")]
    [InlineData(9999, "9,999")]
    [InlineData(12345, "12.3K")]
    [InlineData(125300000, "125.3M")]
    [InlineData(-2500000, "-2.5M")]
    [InlineData(7.2e9, "7.2B")]
    [InlineData(3e12, "3T")]
    public void FormatCompactValue_FormatsWithSuffix(double value, string expected)
    {
        Assert.Equal(expected, LiveSparklinesBase.FormatCompactValue(value, CultureInfo.InvariantCulture));
    }
}
