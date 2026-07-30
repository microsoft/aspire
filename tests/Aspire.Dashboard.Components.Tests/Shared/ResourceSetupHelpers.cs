// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Dashboard.Tests.Shared;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class ResourceSetupHelpers
{
    public static void SetupResourceDetails(TestContext context)
    {
        DashboardSetupHelpers.AddCommonDashboardServices(context);
        context.Services.AddSingleton<IInstrumentUnitResolver, TestInstrumentUnitResolver>();

        DashboardSetupHelpers.SetupDivider(context);
        DashboardSetupHelpers.SetupSearch(context);
        DashboardSetupHelpers.SetupAnchor(context);
        DashboardSetupHelpers.SetupAnchoredRegion(context);
        DashboardSetupHelpers.SetupDataGrid(context);
        DashboardSetupHelpers.SetupKeyCode(context);
        DashboardSetupHelpers.SetupToolbar(context);
        DashboardSetupHelpers.SetupMenu(context);

        context.JSInterop.SetupVoid("scrollToTop", _ => true);
    }

    public static void SetupResourcesPage(TestContext context, ViewportInformation viewport, IDashboardClient? dashboardClient = null, ILocalStorage? localStorage = null)
    {
        DashboardSetupHelpers.SetupDivider(context);
        DashboardSetupHelpers.SetupInputLabel(context);
        DashboardSetupHelpers.SetupDataGrid(context);
        DashboardSetupHelpers.SetupSearch(context);
        DashboardSetupHelpers.SetupKeyCode(context);
        DashboardSetupHelpers.SetupCheckbox(context);
        DashboardSetupHelpers.SetupDeckCheckbox(context);
        DashboardSetupHelpers.SetupDeckPopover(context);
        DashboardSetupHelpers.SetupAnchoredRegion(context);
        DashboardSetupHelpers.SetupToolbar(context);
        DashboardSetupHelpers.SetupTab(context);
        DashboardSetupHelpers.SetupOverflow(context);
        DashboardSetupHelpers.SetupMenu(context);

        DashboardSetupHelpers.AddCommonDashboardServices(context, localStorage: localStorage);
        context.Services.AddSingleton<ILogger<StructuredLogs>>(NullLogger<StructuredLogs>.Instance);
        context.Services.AddSingleton<StructuredLogsViewModel>();
        context.Services.AddScoped<DashboardCommandExecutor, DashboardCommandExecutor>();
        context.Services.AddSingleton<IDashboardClient>(dashboardClient ?? new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));

        DashboardSetupHelpers.SetupUIComponents(context);

        var dimensionManager = context.Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);
    }
}
