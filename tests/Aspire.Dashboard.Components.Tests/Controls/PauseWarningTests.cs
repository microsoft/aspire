// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class PauseWarningTests : DashboardTestContext
{
    [Theory]
    [InlineData(TimeFormat.TwelveHour, 5, "5 minutes ago", "12:54:59 AM")]
    [InlineData(TimeFormat.TwentyFourHour, 5, "5 minutes ago", "0:54:59")]
    [InlineData(TimeFormat.TwelveHour, 65, "1h ago", "12/20/2025 11:54:59 PM")]
    [InlineData(TimeFormat.TwentyFourHour, 65, "1h ago", "12/20/2025 23:54:59")]
    public void Render_ShowsRelativeAgeAndAbsoluteTimeInTooltip(TimeFormat timeFormat, int minutes, string relativeAge, string absoluteTime)
    {
        // Fake timers so the 30s age refresh does not schedule real wall-clock callbacks in unit tests.
        var timeProvider = new TestTimeProvider { Timers = new FakeTimeProvider() };
        timeProvider.SetConfiguredTimeFormat(timeFormat);
        Services.AddLocalization();
        Services.AddFluentUIComponents();
        Services.AddSingleton<BrowserTimeProvider>(timeProvider);
        var pausedAt = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-minutes);

        var cut = Render<PauseWarning>(builder =>
        {
            builder.Add(p => p.PausedAt, pausedAt);
            builder.Add(p => p.CaptureType, "Structured logs");
        });

        var root = cut.Find(".pause-warning");

        Assert.Equal(["class", "data-tooltip"], root.Attributes.Select(attribute => attribute.Name).Order());
        Assert.Equal($"Structured logs capture paused {relativeAge}", cut.Find(".pause-warning-message").TextContent.Trim());
        Assert.Equal("Structured logs capture paused", cut.Find(".pause-warning-title").TextContent.Trim());
        Assert.Equal($"Structured logs capture paused {absoluteTime}", root.GetAttribute("data-tooltip"), ignoreWhiteSpaceDifferences: true);
    }

    [Fact]
    public void Render_RefreshesRelativeAgeWhilePaused()
    {
        var timers = new FakeTimeProvider();
        var timeProvider = new TestTimeProvider { Timers = timers };
        Services.AddLocalization();
        Services.AddFluentUIComponents();
        Services.AddSingleton<BrowserTimeProvider>(timeProvider);

        var cut = Render<PauseWarning>(builder =>
        {
            builder.Add(p => p.PausedAt, timeProvider.GetUtcNow().UtcDateTime);
            builder.Add(p => p.CaptureType, "Structured logs");
        });

        Assert.Equal("Structured logs capture paused now", cut.Find(".pause-warning-message").TextContent.Trim());

        timeProvider.UtcNow += TimeSpan.FromMinutes(1);
        timers.Advance(TimeSpan.FromMinutes(1));

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Structured logs capture paused 1 minute ago", cut.Find(".pause-warning-message").TextContent.Trim());
            Assert.Equal("Structured logs capture paused 12:59:59 AM", cut.Find(".pause-warning").GetAttribute("data-tooltip"), ignoreWhiteSpaceDifferences: true);
        });
    }
}
