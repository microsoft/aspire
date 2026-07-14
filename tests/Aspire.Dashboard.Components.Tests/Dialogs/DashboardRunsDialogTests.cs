// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

public class DashboardRunsDialogTests : DashboardTestContext
{
    [Fact]
    public async Task SelectHistoricalRun_OnlyUpdatesPendingSelection()
    {
        var historicalRun = new DashboardRunDescriptor(
            RunId: "historical",
            StartedAtUtc: new DateTimeOffset(2025, 1, 2, 12, 30, 0, TimeSpan.Zero),
            EndedAtUtc: new DateTimeOffset(2025, 1, 2, 13, 30, 0, TimeSpan.Zero),
            CleanShutdown: true,
            ApplicationName: "TestApp",
            DatabasePath: string.Empty,
            IsCurrent: false);
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore(
        [
            new(
                RunId: "current",
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true),
            historicalRun
        ]);
        string? storedRunId = null;
        var sessionStorage = new TestSessionStorage
        {
            OnSetAsync = (key, value) =>
            {
                Assert.Equal(BrowserStorageKeys.SelectedDashboardRunId, key);
                storedRunId = Assert.IsType<string>(value);
            }
        };

        FluentUISetupHelpers.AddCommonDashboardServices(this, sessionStorage: sessionStorage, dashboardRunStore: runStore);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);

        var content = new DashboardRunsDialogViewModel { SelectedRun = runStore.GetRuns().Single(run => run.IsCurrent) };
        var cut = RenderComponent<DashboardRunsDialog>(builder => builder.Add(component => component.Content, content));
        var select = Assert.Single(cut.FindComponents<FluentSelect<DashboardRunDescriptor>>());

        Assert.Equal("TestApp runs:", select.Instance.Label);
        Assert.Equal("Current", select.Instance.OptionText(runStore.GetRuns().Single(run => run.IsCurrent)));
        var localStartedAt = TimeZoneInfo.ConvertTime(historicalRun.StartedAtUtc, Services.GetRequiredService<BrowserTimeProvider>().LocalTimeZone);
        Assert.Equal($"Started {localStartedAt.ToString("g", CultureInfo.CurrentCulture)}", select.Instance.OptionText(historicalRun));

        await cut.InvokeAsync(() => select.Instance.SelectedOptionChanged.InvokeAsync(historicalRun));

        Assert.Same(historicalRun, content.SelectedRun);
        Assert.Null(storedRunId);
        var navigationManager = Services.GetRequiredService<NavigationManager>();
        Assert.Empty(new Uri(navigationManager.Uri).Query);
    }
}