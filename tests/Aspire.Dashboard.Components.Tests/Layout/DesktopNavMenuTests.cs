// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public class DesktopNavMenuTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TerminalsNavigation_IsConditional(bool enabled, bool hasTerminals)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: enabled));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/terminals/resource/shell");
        Render<FluentTooltipProvider>();
        var cut = Render<DesktopNavMenu>(builder => builder.Add(p => p.HasResourceTerminals, hasTerminals));
        var expected = new List<string>();
        if (enabled)
        {
            expected.AddRange([DashboardUrls.HomeUrl(), DashboardUrls.ResourceListUrl(), DashboardUrls.TagsUrl(),
                DashboardUrls.ParametersUrl(), DashboardUrls.GraphUrl()]);
            if (hasTerminals)
            {
                expected.Add(DashboardUrls.TerminalsUrl());
            }
            expected.Add(DashboardUrls.ExtensionsUrl());
        }
        else
        {
            expected.AddRange([DashboardUrls.StructuredLogsUrl(), DashboardUrls.TracesUrl(), DashboardUrls.MetricsUrl()]);
        }
        var links = cut.FindAll("nav.main-rail > a");
        Assert.Equal(expected, links.Select(link => link.GetAttribute("href")));
        if (enabled && hasTerminals)
        {
            var terminalLink = Assert.Single(links, link => link.GetAttribute("href") == DashboardUrls.TerminalsUrl());
            Assert.Equal(Resources.Layout.NavMenuTerminalsTab, terminalLink.GetAttribute("aria-label"));
            Assert.Equal("page", terminalLink.GetAttribute("aria-current"));
        }
        else if (!enabled)
        {
            var structuredLogsLink = Assert.Single(links, link => link.GetAttribute("href") == DashboardUrls.StructuredLogsUrl());
            Assert.Equal(Resources.Layout.NavMenuStructuredLogsTab, structuredLogsLink.GetAttribute("aria-label"));
        }
    }
}
