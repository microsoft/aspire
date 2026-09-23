// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class AspirePropertyDetailsAccordionItemTests : DashboardTestContext
{
    [Fact]
    public void Render_AddsHeaderBadgeMarkersAndContent()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);

        var cut = RenderComponent<AspirePropertyDetailsAccordionItem>(builder => builder
            .Add(component => component.Header, "Properties")
            .Add(component => component.Count, 3)
            .Add(component => component.Expanded, true)
            .AddChildContent("<span class=\"section-content\">Content</span>"));

        var accordionItem = cut.FindComponent<FluentAccordionItem>();
        Assert.Equal("Properties", accordionItem.Instance.Header);
        Assert.True(accordionItem.Instance.Expanded);
        Assert.Contains("property-details-accordion-item", cut.Find("fluent-accordion-item").ClassList);

        var badge = cut.FindComponent<FluentBadge>();
        Assert.Equal(BadgeAppearance.Ghost, badge.Instance.Appearance);
        Assert.Equal(BadgeColor.Subtle, badge.Instance.Color);
        var badgeElement = cut.Find("fluent-badge");
        Assert.Contains("property-details-accordion-badge", badgeElement.ClassList);
        Assert.True(badgeElement.HasAttribute("circular"));
        Assert.Equal("3", badgeElement.TextContent.Trim());

        Assert.NotNull(cut.Find("[slot='start']"));
        Assert.Collection(
            cut.FindAll(".accordion-marker"),
            marker => Assert.Equal("marker-expanded", marker.GetAttribute("slot")),
            marker => Assert.Equal("marker-collapsed", marker.GetAttribute("slot")));
        Assert.Equal("Content", cut.Find(".section-content").TextContent);
    }

    [Fact]
    public async Task Expanded_UserChangeSurvivesParentRerender()
    {
        FluentUISetupHelpers.SetupFluentUIComponents(this);

        var cut = RenderComponent<AspirePropertyDetailsAccordionItem>(builder => builder
            .Add(component => component.Header, "Properties")
            .Add(component => component.Expanded, true)
            .AddChildContent("Content"));

        await cut.InvokeAsync(() => cut.FindComponent<FluentAccordionItem>().Instance.ExpandedChanged.InvokeAsync(false));
        cut.Render();

        Assert.False(cut.FindComponent<FluentAccordionItem>().Instance.Expanded);
    }
}
