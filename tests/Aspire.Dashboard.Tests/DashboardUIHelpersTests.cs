// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class DashboardUIHelpersTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 1000)]
    [InlineData(1.5, 1)]
    [InlineData(999.9, 999)]
    [InlineData(-1000, -1000)]
    [InlineData(-1.5, -1)]
    public void SafeConvertToMilliseconds_NormalValues_ReturnsExpectedValue(double milliseconds, int expected)
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(milliseconds);

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SafeConvertToMilliseconds_ValueExceedsIntMaxValue_ReturnsIntMaxValue()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds((double)int.MaxValue + 1000);

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(int.MaxValue, result);
    }

    [Fact]
    public void SafeConvertToMilliseconds_ValueEqualsIntMaxValue_ReturnsIntMaxValue()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(int.MaxValue);

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(int.MaxValue, result);
    }

    [Fact]
    public void SafeConvertToMilliseconds_ValueBelowIntMinValue_ReturnsIntMinValue()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds((double)int.MinValue - 1000);

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(int.MinValue, result);
    }

    [Fact]
    public void SafeConvertToMilliseconds_VeryLargeDuration_ReturnsIntMaxValue()
    {
        // Arrange - Create a duration that would overflow int when converted to milliseconds
        // TimeSpan.MaxValue is about 10,675,199 days or roughly 922 trillion milliseconds
        var duration = TimeSpan.MaxValue;

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(int.MaxValue, result);
    }

    [Fact]
    public void SafeConvertToMilliseconds_VerySmallDuration_ReturnsIntMinValue()
    {
        // Arrange
        var duration = TimeSpan.MinValue;

        // Act
        var result = DashboardUIHelpers.SafeConvertToMilliseconds(duration);

        // Assert
        Assert.Equal(int.MinValue, result);
    }
}
