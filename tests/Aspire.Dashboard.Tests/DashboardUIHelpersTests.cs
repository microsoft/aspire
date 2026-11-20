// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Tests;

public class DashboardUIHelpersTests
{
    [Theory]
    [InlineData(-5, "now", "a moment ago", "now")]
    [InlineData(59, "now", "a moment ago", "now")]
    [InlineData(60, "1 minute ago", "1 minute ago", "1m ago")]
    [InlineData(119, "1 minute ago", "1 minute ago", "1m ago")]
    [InlineData(120, "2 minutes ago", "2 minutes ago", "2m ago")]
    [InlineData(2730, "45 minutes ago", "45 minutes ago", "45m ago")]
    [InlineData(3600, "1h ago", "1h ago", "1h ago")]
    [InlineData(86399, "23h ago", "23h ago", "23h ago")]
    [InlineData(86400, "1 day ago", "1 day ago", "1d ago")]
    [InlineData(172799, "1 day ago", "1 day ago", "1d ago")]
    [InlineData(172800, "2 days ago", "2 days ago", "2d ago")]
    [UseCulture("en-US")]
    public void FormatTimeAgo_PreservesRelativeTimeStyles(int seconds, string pauseAge, string runAge, string shortAge)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var loc = services.GetRequiredService<IStringLocalizer<LayoutResources>>();
        var age = TimeSpan.FromSeconds(seconds);

        Assert.Equal(pauseAge, DashboardUIHelpers.FormatTimeAgo(age, loc, useShortFormat: false,
            lessThanMinuteText: loc[nameof(LayoutResources.ResourceStartedAgeNow)]));
        Assert.Equal(runAge, DashboardUIHelpers.FormatTimeAgo(age, loc, useShortFormat: false,
            lessThanMinuteText: loc[nameof(LayoutResources.DashboardRunSelectMomentAgo)]));
        Assert.Equal(shortAge, DashboardUIHelpers.FormatTimeAgo(age, loc, useShortFormat: true,
            lessThanMinuteText: loc[nameof(LayoutResources.ResourceStartedAgeNow)]));
    }

    [Theory]
    [InlineData("redis://:password@localhost:6379", "redis://localhost:6379")]
    [InlineData("https://user:p%40ssword@[::1]:7443/a%2Fb?token=secret#access_token=secret", "https://[::1]:7443/a%2Fb?token=MASK")]
    [InlineData("HTTPS://User:password@Host/a@b?token=secret&key=secret", "HTTPS://Host/a@b?token=MASK&key=MASK")]
    [InlineData("https://host/path#access_token=secret?other=secret", "https://host/path")]
    [InlineData("https://host/path?token=secret#access_token=secret", "https://host/path?token=MASK")]
    [InlineData("tcp://host:6379/path%23fragment?token=secret", "tcp://host:6379/path%23fragment?token=MASK")]
    [InlineData("https://host/a@b?token=secret@host", "https://host/a@b?token=MASK")]
    [InlineData("https://host/path", "https://host/path")]
    public void MaskUrlForDisplay_RemovesCredentialsAndFragmentsAndMasksQueryValues(string url, string expected)
    {
        Assert.Equal(expected.Replace("MASK", new string('\u25cf', 8)), DashboardUIHelpers.MaskUrlForDisplay(url));
    }

    [Theory]
    [InlineData(200_000, 200_000)]
    [InlineData(600_000, 200_000)]
    public void GetVirtualizedItemCount_LimitsLargeResultSets(int totalItemCount, int expected)
    {
        Assert.Equal(expected, DashboardUIHelpers.GetVirtualizedItemCount(totalItemCount));
    }

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

    [Fact]
    public void GetMaskingText_ReturnsClassedMarkupAndPlainText()
    {
        var mask = DashboardUIHelpers.GetMaskingText(3);

        Assert.Equal("<span class=\"masked-content\">&#x25cf;&#x25cf;&#x25cf;</span>", mask.MarkupString.Value);
        Assert.Equal("●●●", mask.Text);
    }
}
