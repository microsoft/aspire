// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

[UseCulture("en-US")]
public class ResourceNameDisplayTests : DashboardTestContext
{
    [Theory]
    [InlineData("/resources", "Persistent", true, false)]
    [InlineData("/resources", "Session", false, false)]
    [InlineData("/resources", null, false, false)]
    [InlineData("/resources", "Unknown", false, false)]
    [InlineData("/legacy/resources", "Persistent", false, true)]
    [InlineData("/legacy/resources", "Session", false, false)]
    [InlineData("/legacy/resources", null, false, false)]
    public void Render_OnlyModernPersistentContainerGetsInfinityBadge(string location, string? lifetime, bool expectedBadge, bool expectedPin)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<IconResolver>();
        Services.GetRequiredService<NavigationManager>().NavigateTo(location);
        var resource = ModelTestHelpers.CreateResource("cache", properties: lifetime is null ? [] : new()
        {
            [KnownProperties.Container.Lifetime] = new(KnownProperties.Container.Lifetime, Value.ForString(lifetime), false, null, 0, null, false)
        });
        var cut = Render<ResourceNameDisplay>(p => p
            .Add(p => p.Resource, resource)
            .Add(p => p.FormatName, r => r.DisplayName)
            .Add(p => p.FilterText, "")
            .Add(p => p.ShowHiddenIndicator, false));

        Assert.Equal("cache", cut.Find(".resource-name-text").TextContent);
        Assert.Single(cut.FindAll(".resource-icon"));
        Assert.Equal(expectedBadge ? 1 : 0, cut.FindAll(".modern-resource-icon .persistent-container-badge").Count);
        Assert.Equal(expectedPin ? 1 : 0, cut.FindAll(".persistent-container-icon").Count);
        if (expectedBadge)
        {
            var badge = cut.Find(".persistent-container-badge");
            const string description = "This container is persistent and won't be stopped when the app host is shut down.";
            Assert.Equal("img", badge.GetAttribute("role"));
            Assert.Equal(description, badge.GetAttribute("aria-label"));
            Assert.Equal(description, badge.GetAttribute("data-tooltip"));
            Assert.Equal("true", badge.QuerySelector("svg")!.GetAttribute("aria-hidden"));
        }
        if (location.StartsWith("/legacy", StringComparison.Ordinal))
        {
            Assert.Empty(cut.FindAll(".modern-resource-icon"));
        }
    }
}
