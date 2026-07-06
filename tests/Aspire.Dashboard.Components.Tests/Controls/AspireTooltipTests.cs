// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspireTooltipTests : DashboardTestContext
{
    [Fact]
    public void Render_ProvidesTooltipRoleAndAccessibleLabel()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);

        var cut = RenderComponent<AspireTooltip>(builder =>
        {
            builder.Add(p => p.Anchor, "target-button");
            builder.Add(p => p.Text, "Open console logs");
        });

        var tooltip = cut.FindComponent<FluentTooltip>();

        Assert.Equal("target-button", tooltip.Instance.Anchor);
        Assert.Equal("Open console logs", tooltip.Instance.AriaLabel);
        Assert.False(tooltip.Instance.UseTooltipService);
        Assert.NotNull(tooltip.Instance.AdditionalAttributes);
        Assert.True(tooltip.Instance.AdditionalAttributes.TryGetValue("role", out var role));
        Assert.Equal("tooltip", role);
        Assert.True(tooltip.Instance.AdditionalAttributes.TryGetValue("aria-label", out var ariaLabel));
        Assert.Equal("Open console logs", ariaLabel);
        Assert.Equal("Open console logs", cut.Find("fluent-tooltip").TextContent);

        cut.SetParametersAndRender(builder =>
        {
            builder.Add(p => p.Anchor, "target-button");
            builder.Add(p => p.Text, "View details");
        });

        tooltip = cut.FindComponent<FluentTooltip>();
        Assert.Equal("View details", tooltip.Instance.AriaLabel);
        Assert.True(tooltip.Instance.AdditionalAttributes!.TryGetValue("aria-label", out ariaLabel));
        Assert.Equal("View details", ariaLabel);
        Assert.Equal("View details", cut.Find("fluent-tooltip").TextContent);
    }
}
