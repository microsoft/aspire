// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
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
    public void TerminalsNavigation_IsConditionalAndLastPageLink(bool enabled, bool hasTerminals)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentKeyCode(this);
        FluentUISetupHelpers.SetupFluentMenu(this);
        FluentUISetupHelpers.SetupFluentAnchor(this);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: enabled));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/terminals/resource/shell");
        var cut = Render<DesktopNavMenu>(builder => builder.Add(p => p.HasResourceTerminals, hasTerminals));
        var expected = new List<string>();
        if (enabled)
        {
            expected.AddRange(["/", "/consolelogs"]);
        }
        expected.AddRange(["/structuredlogs", "/traces", "/metrics"]);
        if (enabled && hasTerminals)
        {
            expected.Add("/terminals");
        }
        Assert.Equal(expected, cut.FindComponents<FluentAppBarItem>().Select(i => i.Instance.Href));
        Assert.Equal(Resources.Layout.NavMenuStructuredLogsTab,
            cut.FindComponents<FluentAppBarItem>().Single(i => i.Instance.Href == "/structuredlogs").Instance.Text);
        if (enabled && hasTerminals)
        {
            Assert.Equal(Resources.Layout.NavMenuTerminalsTab, cut.FindComponents<FluentAppBarItem>()[^1].Instance.Text);
        }
    }
}
