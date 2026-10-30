// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Legacy.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

[UseCulture("en-US")]
public class LegacyNavMenuTests : DashboardTestContext
{
    [Theory]
    [InlineData("/", "/legacy")]
    [InlineData("/?resource=api", "/legacy?resource=api")]
    [InlineData("/traces", "/legacy/traces")]
    [InlineData("/traces/detail/abc", "/legacy/traces/detail/abc")]
    public void ToLegacyUrl_PrefixesRouteBelowLegacy(string url, string expectedUrl)
    {
        Assert.Equal(expectedUrl, LegacyNavUrls.ToLegacyUrl(url));
    }

    [Fact]
    public void DesktopNavMenu_LinksToLegacyPages()
    {
        SetupServices("/legacy/traces");

        var cut = Render<LegacyDesktopNavMenu>();

        Assert.Equal(
            ["/legacy", "/legacy/consolelogs", "/legacy/structuredlogs", "/legacy/traces", "/legacy/metrics"],
            cut.FindAll("[href]").Select(link => link.GetAttribute("href")).Distinct());
    }

    [Theory]
    [InlineData("/legacy/traces", "Traces")]
    [InlineData("/legacy/traces/detail/abc", "Traces")]
    [InlineData("/legacy/consolelogs/resource/api", "Console logs")]
    [InlineData("/legacy", "Resources")]
    [InlineData("/legacy/", "Resources")]
    [InlineData("/legacy?view=Graph", "Resources")]
    [InlineData("/legacy/resources", "Resources")]
    public void MobileNavMenu_MarksCurrentLegacyPage(string currentUrl, string expectedText)
    {
        SetupServices(currentUrl);
        FluentUISetupHelpers.SetupFluentDivider(this);

        var cut = Render<LegacyMobileNavMenu>(builder =>
        {
            builder.Add(p => p.IsNavMenuOpen, true);
            builder.Add(p => p.HasResourceTerminals, false);
            builder.Add(p => p.CloseNavMenu, () => { });
            builder.Add(p => p.LaunchHelpAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchAIAgentsAsync, () => Task.CompletedTask);
            builder.Add(p => p.IsAgentHelpEnabled, false);
            builder.Add(p => p.LaunchNotificationsAsync, () => Task.CompletedTask);
            builder.Add(p => p.LaunchSettingsAsync, () => Task.CompletedTask);
        });

        var currentItem = Assert.Single(cut.FindAll("""fluent-menu-item[aria-current="page"]"""));
        Assert.Equal(expectedText, currentItem.GetAttribute("title"));
        Assert.True(currentItem.ClassList.Contains("mobile-nav-menu-item-active"));
    }

    private void SetupServices(string currentUrl)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: true));
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);

        Services.GetRequiredService<NavigationManager>().NavigateTo(currentUrl);
    }
}
