// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Legacy.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class LegacySettingsDialogTests : DashboardTestContext
{
    [Fact]
    public async Task DashboardExperience_UsesRadioOptionsWithoutIcons()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);

        var cut = Render<LegacySettingsDialog>();

        Assert.Empty(cut.FindAll(".icon-toggle"));
        Assert.Collection(
            cut.FindAll(".legacy-dashboard-experience-select fluent-radio[role='radio']"),
            radio =>
            {
                Assert.Equal("modern", radio.GetAttribute("value"));
                Assert.Equal("false", radio.GetAttribute("aria-checked"));
            },
            radio =>
            {
                Assert.Equal("legacy", radio.GetAttribute("value"));
                Assert.Equal("true", radio.GetAttribute("aria-checked"));
            });
        Assert.Contains("New dashboard", cut.Find(".legacy-dashboard-experience-select").ParentElement!.TextContent);
        Assert.Contains("Previous dashboard", cut.Find(".legacy-dashboard-experience-select").ParentElement!.TextContent);
    }
}
