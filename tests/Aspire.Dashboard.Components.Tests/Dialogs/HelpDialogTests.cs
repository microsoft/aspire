// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public class HelpDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void SiteWideNavigation_OnlyShowsTerminalShortcutWhenAvailable(bool isEnabled, bool isReadOnly, bool expectTerminalShortcut)
    {
        FluentUISetupHelpers.SetupDialogInfrastructure(this);
        Services.AddSingleton<IDashboardClient>(new TestDashboardClient(isEnabled: isEnabled) { IsReadOnly = isReadOnly });

        var cut = RenderComponent<HelpDialog>();

        var heading = Assert.Single(cut.FindAll("h6"), h => h.TextContent == Resources.Dialogs.HelpDialogCategoryNavigation);
        var navigationShortcuts = cut.Find($"dl[aria-labelledby='{heading.Id}']");
        List<string> expectedDescriptions = [Resources.Dialogs.HelpDialogGoToHelp, Resources.Dialogs.HelpDialogGoToSettings];
        List<string> expectedKeys = ["?", "shift", "s"];
        if (expectTerminalShortcut)
        {
            expectedDescriptions.Add(Resources.Dialogs.HelpDialogToggleTerminalDock);
            expectedKeys.Add("`");
        }

        Assert.Equal(expectedDescriptions, navigationShortcuts.QuerySelectorAll("dt").Select(element => element.TextContent));
        Assert.Equal(expectedKeys, navigationShortcuts.QuerySelectorAll("kbd").Select(element => element.TextContent));
        Assert.Equal(expectTerminalShortcut ? 1 : 0, cut.FindAll("kbd").Count(element => element.TextContent == "`"));
    }
}
