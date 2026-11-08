// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
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
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class ResourceSetupHelpers
{
    public static void ToggleResourceList<T>(IRenderedComponent<T> component) where T : class, IComponent
    {
        var layout = component.Instance as ResourcesLayout ?? component.FindComponent<ResourcesLayout>().Instance;
        var visible = !component.Find(".resources-layout").ClassList.Contains("pane-full-canvas");
        component.InvokeAsync(() => layout.SetResourceListVisible(visible)).GetAwaiter().GetResult();
    }

    // The kinds of items the pane lists are switched in the filter's "Show" section. Tests switch them through the
    // same callbacks the filter's checkboxes use, without opening the popover.
    public static IReadOnlyList<ResourcePaneFilter.FilterKind> GetFilterKinds<T>(IRenderedComponent<T> component) where T : class, IComponent =>
        component.FindComponent<ResourcePaneFilter>().Instance.Kinds!();

    public static void ToggleFilterKind<T>(IRenderedComponent<T> component, string label) where T : class, IComponent
    {
        var kind = GetFilterKinds(component).Single(k => k.Label == label);
        component.InvokeAsync(kind.ToggleAsync).GetAwaiter().GetResult();
    }

    public static void SetupResourceOverview(BunitContext context, ViewportInformation viewport, IDashboardClient dashboardClient,
        Action<IServiceCollection>? configureServices = null)
    {
        context.Services.AddSingleton<IVolumePathLauncher, VolumePathLauncher>();
        context.Services.AddSingleton<IInstrumentUnitResolver, TestInstrumentUnitResolver>();
        context.Services.AddSingleton<ConsoleLogsManager>();
        SetupResourcesLayout(context, viewport, dashboardClient, configureServices);
        context.JSInterop.Setup<string>("Blazor._internal.PageTitle.getAndRemoveExistingTitle", _ => true).SetResult(string.Empty);
    }

    public static void SetupResourcesLayout(BunitContext context, ViewportInformation viewport, IDashboardClient dashboardClient,
        Action<IServiceCollection>? configureServices = null)
    {
        SetupResourcesPage(context, viewport, dashboardClient, configureServices: configureServices);
        context.Services.AddLogging();
        FluentUISetupHelpers.SetupFluentTextField(context);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.TextInput.attachImmediateEvent", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Tabs.ObserveTabsChanged", _ => true).SetVoidResult();
        FluentUISetupHelpers.SetupFluentButton(context);
        var module = context.JSInterop.SetupModule(invocation => invocation.Arguments[0] is string path && path.Contains(nameof(ResourcesLayout), StringComparison.Ordinal));
        module.SetupVoid("registerPaneResizer", _ => true).SetVoidResult();
        module.SetupVoid("unregisterPaneResizer", _ => true).SetVoidResult();
        module.Setup<bool>("usesCommandSelectionModifier", _ => true).SetResult(false);
        module.SetupVoid("setTabListAriaLabel", _ => true).SetVoidResult();
        module.Setup<bool>("isResourceTabFocused", _ => true).SetResult(true);
        module.SetupVoid("registerResourceOrdering", _ => true).SetVoidResult();
        module.SetupVoid("unregisterResourceOrdering", _ => true).SetVoidResult();
        module.SetupVoid("registerResourceContextMenu", _ => true).SetVoidResult();
        module.SetupVoid("unregisterResourceContextMenu", _ => true).SetVoidResult();
        module.SetupVoid("registerPaneActivityVisibility", _ => true).SetVoidResult();
        module.SetupVoid("unregisterPaneActivityVisibility", _ => true).SetVoidResult();
        module.SetupVoid("setResourcePickerOpen", _ => true).SetVoidResult();
        module.SetupVoid("unregisterResourcePicker", _ => true).SetVoidResult();
        module.SetupVoid("restoreResourceContextMenuFocus", _ => true).SetVoidResult();
        module.SetupVoid("startPaneSelectionReveal", _ => true).SetVoidResult();
        module.SetupVoid("focusPaneSelectionReveal", _ => true).SetVoidResult();
        module.SetupVoid("cancelPaneSelectionReveal", _ => true).SetVoidResult();
        module.SetupVoid("registerTabOverflow", _ => true).SetVoidResult();
        module.SetupVoid("unregisterTabOverflow", _ => true).SetVoidResult();
    }

    public static void SetupResourceDetails(BunitContext context)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(context);
        context.Services.AddSingleton<IInstrumentUnitResolver, TestInstrumentUnitResolver>();
        context.Services.AddSingleton<IconResolver>();

        FluentUISetupHelpers.SetupFluentDivider(context);
        FluentUISetupHelpers.SetupFluentSearch(context);
        FluentUISetupHelpers.SetupFluentAnchor(context);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(context);
        FluentUISetupHelpers.SetupFluentDataGrid(context);
        FluentUISetupHelpers.SetupFluentKeyCode(context);
        FluentUISetupHelpers.SetupFluentToolbar(context);
        FluentUISetupHelpers.SetupFluentMenu(context);

        context.JSInterop.SetupVoid("scrollToTop", _ => true);
        context.JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();

        FluentUISetupHelpers.SetupFluentUIComponents(context);
    }

    public static void SetupResourcesPage(
        BunitContext context,
        ViewportInformation viewport,
        IDashboardClient? dashboardClient = null,
        ILocalStorage? localStorage = null,
        ISessionStorage? sessionStorage = null,
        IDialogService? dialogService = null,
        Action<IServiceCollection>? configureServices = null)
    {
        FluentUISetupHelpers.SetupFluentDivider(context);
        FluentUISetupHelpers.SetupFluentInputLabel(context);
        FluentUISetupHelpers.SetupFluentDataGrid(context);
        FluentUISetupHelpers.SetupFluentSearch(context);
        FluentUISetupHelpers.SetupFluentKeyCode(context);
        FluentUISetupHelpers.SetupFluentCheckbox(context);
        FluentUISetupHelpers.SetupFluentAnchoredRegion(context);
        FluentUISetupHelpers.SetupFluentToolbar(context);
        FluentUISetupHelpers.SetupFluentTab(context);
        FluentUISetupHelpers.SetupFluentOverflow(context);
        FluentUISetupHelpers.SetupFluentMenu(context);

        FluentUISetupHelpers.AddCommonDashboardServices(context, localStorage: localStorage, sessionStorage: sessionStorage);
        context.JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();
        context.Services.AddSingleton<IconResolver>();
        context.Services.AddSingleton<ILogger<StructuredLogs>>(NullLogger<StructuredLogs>.Instance);
        context.Services.AddTransient<StructuredLogsViewModel>();
        context.Services.AddScoped<DashboardCommandExecutor, DashboardCommandExecutor>();
        context.Services.AddSingleton<IDashboardClient>(dashboardClient ?? new TestDashboardClient(isEnabled: true, initialResources: [], resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>));

        FluentUISetupHelpers.SetupFluentUIComponents(context);

        if (dialogService is not null)
        {
            context.Services.AddSingleton(dialogService);
        }

        configureServices?.Invoke(context.Services);

        var dimensionManager = context.Services.GetRequiredService<DimensionManager>();
        dimensionManager.InvokeOnViewportInformationChanged(viewport);
    }
}
