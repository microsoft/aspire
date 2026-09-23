// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireProgressRingTests : DashboardTestContext
{
    [Fact]
    public void Render_WithoutValue_IsIndeterminate()
    {
        var cut = RenderComponent<AspireProgressRing>(builder => builder
            .Add(p => p.AriaLabel, "Loading")
            .Add(p => p.Width, "20px"));

        var progressRing = cut.Find("[role='progressbar']");
        Assert.Equal("Loading", progressRing.GetAttribute("aria-label"));
        Assert.Contains("indeterminate", progressRing.ClassList);
        Assert.Contains("--aspire-progress-ring-size: 20px", progressRing.GetAttribute("style"));
        Assert.False(progressRing.HasAttribute("aria-valuemin"));
        Assert.False(progressRing.HasAttribute("aria-valuemax"));
        Assert.False(progressRing.HasAttribute("aria-valuenow"));
        Assert.Single(cut.FindAll("circle.progress-value"));
        Assert.Empty(cut.FindAll("circle.progress-track"));
    }

    [Fact]
    public void Render_WithValue_IsDeterminate()
    {
        var cut = RenderComponent<AspireProgressRing>(builder => builder
            .Add(p => p.Min, 10)
            .Add(p => p.Max, 30)
            .Add(p => p.Value, 20)
            .Add(p => p.AriaLabel, "Duration"));

        var progressRing = cut.Find("[role='progressbar']");
        Assert.Contains("determinate", progressRing.ClassList);
        Assert.Equal("10", progressRing.GetAttribute("aria-valuemin"));
        Assert.Equal("30", progressRing.GetAttribute("aria-valuemax"));
        Assert.Equal("20", progressRing.GetAttribute("aria-valuenow"));
        Assert.Equal("Duration", progressRing.GetAttribute("aria-label"));
        Assert.Contains("--aspire-progress-ring-dash-offset: 21.991148575128552", progressRing.GetAttribute("style"));
        Assert.Single(cut.FindAll("circle.progress-track"));
        Assert.Single(cut.FindAll("circle.progress-value"));
    }

    [Theory]
    [InlineData(-1, "0")]
    [InlineData(101, "100")]
    public void Render_ValueOutsideRange_ClampsValue(double value, string expectedValue)
    {
        var cut = RenderComponent<AspireProgressRing>(builder => builder
            .Add(p => p.AriaLabel, "Duration")
            .Add(p => p.Value, value));

        Assert.Equal(expectedValue, cut.Find("[role='progressbar']").GetAttribute("aria-valuenow"));
    }
}
