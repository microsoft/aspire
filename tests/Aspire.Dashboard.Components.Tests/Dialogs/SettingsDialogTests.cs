// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public sealed class SettingsDialogTests : DashboardTestContext
{
    [Fact]
    public async Task Render_RadioLabelsUseLocalizedResources()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);
        JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js")
            .Setup<string>("getTerminalPalette").SetResult("dashboard");

        var cut = RenderComponent<SettingsDialog>();

        Assert.Collection(
            cut.FindAll("fluent-radio-group label"),
            label => Assert.Equal("System", label.TextContent),
            label => Assert.Equal("Light", label.TextContent),
            label => Assert.Equal("Dark", label.TextContent),
            label => Assert.Equal("Follow Dashboard", label.TextContent),
            label => Assert.Equal("Light", label.TextContent),
            label => Assert.Equal("Dark", label.TextContent),
            label => Assert.Equal("System", label.TextContent),
            label => Assert.Equal("12-hour", label.TextContent),
            label => Assert.Equal("24-hour", label.TextContent));
    }

    [Theory]
    [InlineData("dashboard")]
    [InlineData("light")]
    [InlineData("dark")]
    public async Task TerminalPalette_LoadsAndSavesIndependentlyOfTheme(string preference)
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<string>("getTerminalPalette").SetResult(preference);
        var save = module.SetupVoid("setTerminalPalette", "dark");
        save.SetVoidResult();
        var cut = RenderComponent<SettingsDialog>();
        var palette = cut.FindComponents<FluentRadioGroup<string>>()[1];
        cut.WaitForAssertion(() => Assert.Equal(preference, palette.Instance.Value));
        var selectedTheme = themeManager.SelectedTheme;

        await cut.InvokeAsync(() => palette.Instance.ValueChanged.InvokeAsync("dark"));

        Assert.Single(save.Invocations);
        Assert.Equal(selectedTheme, themeManager.SelectedTheme);
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    [Fact]
    public async Task TerminalPalette_SaveFailureRestoresSelectionAndShowsError()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);
        var module = JSInterop.SetupModule("/Components/Controls/TerminalView.razor.js");
        module.Setup<string>("getTerminalPalette").SetResult("dashboard");
        module.SetupVoid("setTerminalPalette", "dark").SetException(new JSException("Storage unavailable"));
        var cut = RenderComponent<SettingsDialog>();
        var palette = cut.FindComponents<FluentRadioGroup<string>>()[1];

        await cut.InvokeAsync(() => palette.Instance.ValueChanged.InvokeAsync("dark"));

        Assert.Equal("dashboard", palette.Instance.Value);
        Assert.Equal(Resources.Dialogs.SettingsDialogTerminalPaletteSaveFailed, cut.Find("[role=alert]").TextContent);
    }
}