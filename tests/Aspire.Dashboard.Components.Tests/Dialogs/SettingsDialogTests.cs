// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class SettingsDialogTests : DashboardTestContext
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Render_TelemetryNoticeFollowsConfiguredEnablement(bool enabled)
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);
        Services.AddSingleton(new DashboardTelemetryConfiguration { ReportedTelemetryEnabled = enabled });

        var cut = Render<SettingsDialog>();

        Assert.Equal(enabled ? 1 : 0, cut.FindAll("a[href='https://aka.ms/aspire/dashboard-microsoft-collected-telemetry']").Count);
    }

    [Fact]
    public async Task Render_ThemeAndTimeFormatIconsUseLocalizedResources()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);

        var cut = Render<SettingsDialog>();

        Assert.Equal(GetExpectedIconLabels(), cut.FindAll(".icon-toggle").Select(e => e.GetAttribute("aria-label")));
    }

    [Fact]
    public async Task ViewPreferences_LoadStoredValuesAndApplyAndStoreChanges()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        var stored = new Dictionary<string, object?>();
        var localStorage = new TestLocalStorage
        {
            OnGetUnprotectedAsync = key => key switch
            {
                BrowserStorageKeys.ResourceListDensity => (true, "Compact"),
                BrowserStorageKeys.GroupOverview => (true, "Resources"),
                BrowserStorageKeys.RecordingsStyle => (true, "Menu"),
                _ => (false, null)
            },
            OnSetUnprotectedAsync = (key, value) => stored[key] = value
        };
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager, localStorage: localStorage);
        FluentUISetupHelpers.SetupFluentList(this);
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();

        var cut = Render<SettingsDialog>();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".icon-toggle-current")));
        Assert.Equal(GetExpectedIconLabels(), cut.FindAll(".icon-toggle").Select(e => e.GetAttribute("aria-label")));
        Assert.Equal(["System", "Compact", "Cards", "System", "New dashboard"], cut.FindAll(".icon-toggle[aria-pressed='true']").Select(e => e.GetAttribute("aria-label")));
        Assert.Equal(GetExpectedIconLabels(), cut.FindAll(".icon-toggle-option-text").Select(e => e.TextContent));

        await cut.InvokeAsync(() => cut.Find(".icon-toggle[aria-label='Dark']").Click());
        await cut.InvokeAsync(() => cut.Find(".icon-toggle[aria-label='Comfortable']").Click());
        await cut.InvokeAsync(() => cut.Find(".icon-toggle[aria-label='Home']").Click());

        Assert.Equal(ResourceListDensity.Comfortable, preferences.Density);
        Assert.Equal(GroupOverview.Summary, preferences.GroupOverview);
        Assert.Equal(ThemeManager.ThemeSettingDark, themeManager.SelectedTheme);
        Assert.Equal("Comfortable", stored[BrowserStorageKeys.ResourceListDensity]);
        Assert.Equal("Summary", stored[BrowserStorageKeys.GroupOverview]);
        Assert.Equal(RecordingsStyle.Menu, preferences.RecordingsStyle);
        Assert.Equal(["Dark", "Comfortable", "Home", "System", "New dashboard"], cut.FindAll(".icon-toggle[aria-pressed='true']").Select(e => e.GetAttribute("aria-label")));
    }

    [Theory]
    [InlineData("modern")]
    [InlineData("legacy")]
    public async Task SwitchDashboardExperience_UsesExperienceRoot(string experience)
    {
        var expectedExperience = experience == "legacy" && DashboardExperienceComponents.IsLegacyAvailable ? "legacy" : "modern";
        var expectedPath = expectedExperience == "legacy" ? "/legacy" : "/";
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);
        JSInterop.SetupVoid("setDashboardExperience", expectedExperience, expectedPath).SetVoidResult();
        var cut = Render<SettingsDialog>();

        await cut.InvokeAsync(() => cut.Instance.SwitchDashboardExperienceAsync(experience));

        var invocation = Assert.Single(JSInterop.Invocations["setDashboardExperience"]);
        Assert.Equal(expectedExperience, invocation.Arguments[0]);
        Assert.Equal(expectedPath, invocation.Arguments[1]);
    }

    private static IReadOnlyList<string> GetExpectedIconLabels()
    {
        var labels = new List<string>
        {
            "System", "Light", "Dark",
            "Spacious", "Comfortable", "Compact",
            "Cards", "Home", "Graph",
            "System", "12-hour", "24-hour",
            "New dashboard"
        };
        if (DashboardExperienceComponents.IsLegacyAvailable)
        {
            labels.Add("Previous dashboard");
        }
        return labels;
    }
}
