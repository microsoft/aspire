// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class PauseWarningTests : DashboardTestContext
{
    [Fact]
    public void FormatPauseAge_UsesRelativeTimeBuckets()
    {
        Services.AddLocalization();
        var loc = Services.GetRequiredService<IStringLocalizer<LayoutResources>>();
        var now = new DateTimeOffset(2025, 1, 10, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("now", PauseWarning.FormatPauseAge(now.AddSeconds(-59).UtcDateTime, now, loc));
        Assert.Equal("now", PauseWarning.FormatPauseAge(now.AddSeconds(5).UtcDateTime, now, loc));
        Assert.Equal("1 minute ago", PauseWarning.FormatPauseAge(now.AddMinutes(-1).UtcDateTime, now, loc));
        Assert.Equal("45 minutes ago", PauseWarning.FormatPauseAge(now.AddMinutes(-45).AddSeconds(-30).UtcDateTime, now, loc));
        Assert.Equal("1h ago", PauseWarning.FormatPauseAge(now.AddMinutes(-60).UtcDateTime, now, loc));
        Assert.Equal("23h ago", PauseWarning.FormatPauseAge(now.AddHours(-23).AddMinutes(-59).UtcDateTime, now, loc));
        Assert.Equal("1 day ago", PauseWarning.FormatPauseAge(now.AddDays(-1).UtcDateTime, now, loc));
        Assert.Equal("2 days ago", PauseWarning.FormatPauseAge(now.AddDays(-2).AddHours(-5).UtcDateTime, now, loc));
    }

    [Fact]
    public void Render_ShowsRelativeAgeAndAbsoluteTimeInTooltip()
    {
        // Fake timers so the 30s age refresh does not schedule real wall-clock callbacks in unit tests.
        var timeProvider = new TestTimeProvider { Timers = new FakeTimeProvider() };
        Services.AddLocalization();
        Services.AddFluentUIComponents();
        Services.AddSingleton<BrowserTimeProvider>(timeProvider);
        var pausedAt = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(-5);

        var cut = Render<PauseWarning>(builder =>
        {
            builder.Add(p => p.PausedAt, pausedAt);
            builder.Add(p => p.CaptureType, "Structured logs");
        });

        var root = cut.Find(".pause-warning");

        Assert.Equal("status", root.GetAttribute("role"));
        Assert.Equal("Structured logs capture paused 5 minutes ago", cut.Find(".pause-warning-message").TextContent.Trim());
        Assert.Equal("Structured logs capture paused", cut.Find(".pause-warning-title").TextContent.Trim());
        Assert.Equal("0", root.GetAttribute("tabindex"));
        Assert.Equal("Structured logs capture paused 12:54:59 AM", root.GetAttribute("data-tooltip"));
        Assert.Equal("above", root.GetAttribute("data-tooltip-positioning"));
        Assert.Equal("Structured logs capture paused 5 minutes ago (12:54:59 AM)", root.GetAttribute("aria-label"));

        var id = root.GetAttribute("id");
        Assert.False(string.IsNullOrEmpty(id));

        var another = Render<PauseWarning>(builder =>
        {
            builder.Add(p => p.PausedAt, pausedAt);
            builder.Add(p => p.CaptureType, "Structured logs");
        });
        Assert.NotEqual(id, another.Find(".pause-warning").GetAttribute("id"));
    }
}
