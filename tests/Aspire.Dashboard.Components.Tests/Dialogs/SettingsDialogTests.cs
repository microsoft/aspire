// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
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
    public async Task Render_RadioLabelsUseLocalizedResources()
    {
        var themeManager = new ThemeManager(new TestThemeResolver());
        await themeManager.EnsureInitializedAsync();
        FluentUISetupHelpers.AddCommonDashboardServices(this, themeManager: themeManager);
        FluentUISetupHelpers.SetupFluentList(this);

        var cut = Render<SettingsDialog>();

        Assert.Collection(
            cut.FindAll("fluent-radio-group label"),
            label => Assert.Equal("System", label.TextContent),
            label => Assert.Equal("Light", label.TextContent),
            label => Assert.Equal("Dark", label.TextContent),
            label => Assert.Equal("System", label.TextContent),
            label => Assert.Equal("12-hour", label.TextContent),
            label => Assert.Equal("24-hour", label.TextContent));
    }
}