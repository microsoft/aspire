// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Tests.Shared;

// Test setup helpers for the Deck-based dashboard. The many Setup* methods for individual controls
// are retained (as no-ops where the control no longer needs JS interop) so existing test setups keep
// compiling; the ones that still register a colocated JS module (menu, checkbox, popover) do real work.
internal static class DashboardSetupHelpers
{
    public static Bunit.BunitJSModuleInterop SetupDialogProvider(TestContext context)
    {
        // The Deck dialog provider imports a colocated accessibility module (focus trap, scroll lock,
        // focus restore). Register it so bUnit's strict JS interop doesn't reject the import/calls.
        var dialogModule = context.JSInterop.SetupModule("./Components/Dialogs/DeckDialogProvider.razor.js");
        dialogModule.SetupVoid("initialize", _ => true);
        dialogModule.SetupVoid("dispose", _ => true);
        return dialogModule;
    }

    public static void SetupMenu(TestContext context)
    {
        var menuModule = context.JSInterop.SetupModule("./Components/Controls/AspireMenu.razor.js");
        menuModule.SetupVoid("initialize", _ => true);
        menuModule.SetupVoid("dispose", _ => true);
    }

    public static void SetupOverflow(TestContext context)
    {
        _ = context;
    }

    public static void SetupAnchor(TestContext context)
    {
        _ = context;
    }

    public static void SetupAnchoredRegion(TestContext context)
    {
        _ = context;
    }

    public static void SetupDivider(TestContext context)
    {
        _ = context;
    }

    public static void SetupDataGrid(TestContext context)
    {
        _ = context;
    }

    public static void SetupSearch(TestContext context)
    {
        _ = context;
    }

    public static void SetupKeyCode(TestContext context)
    {
        _ = context;
    }

    public static void SetupToolbar(TestContext context)
    {
        _ = context;
    }

    public static void SetupInputLabel(TestContext context)
    {
        _ = context;
    }

    public static void SetupList(TestContext context)
    {
        _ = context;
    }

    public static void SetupTab(TestContext context)
    {
        _ = context;
    }

    public static void SetupCheckbox(TestContext context)
    {
        _ = context;
    }

    public static void SetupDeckCheckbox(TestContext context)
    {
        var checkboxModule = context.JSInterop.SetupModule("./Components/Deck/Checkbox.razor.js");
        checkboxModule.SetupVoid("setIndeterminate", _ => true);
    }

    public static void SetupDeckPopover(TestContext context)
    {
        var popoverModule = context.JSInterop.SetupModule("./Components/Deck/Popover.razor.js");
        popoverModule.SetupVoid("initialize", _ => true);
        popoverModule.SetupVoid("dispose", _ => true);
    }

    public static void SetupTextField(TestContext context)
    {
        _ = context;
    }

    public static void SetupButton(TestContext context)
    {
        _ = context;
    }

    public static void SetupInputFile(TestContext context)
    {
        _ = context;
    }

    public static void SetupCombobox(TestContext context)
    {
        var comboboxModule = context.JSInterop.SetupModule("./Components/Deck/Combobox.razor.js");
        comboboxModule.SetupVoid("initialize", _ => true);
        comboboxModule.SetupVoid("dispose", _ => true);
    }

    public static void SetupUIComponents(TestContext context)
    {
        _ = context;
    }

    public static void AddCommonDashboardServices(
        TestContext context,
        ILocalStorage? localStorage = null,
        ISessionStorage? sessionStorage = null,
        ThemeManager? themeManager = null,
        DashboardMessageService? messageService = null,
        BrowserTimeProvider? browserTimeProvider = null)
    {
        context.Services.AddLocalization();
        context.Services.AddSingleton<BrowserTimeProvider>(browserTimeProvider ?? new TestTimeProvider());
        context.Services.AddSingleton<TelemetryRepository>();
        context.Services.AddSingleton<PauseManager>();
        context.Services.AddScoped<Aspire.Dashboard.Components.Dialogs.DeckDialogService>();
        context.Services.AddSingleton<ILocalStorage>(localStorage ?? new TestLocalStorage());
        context.Services.AddSingleton<ISessionStorage>(sessionStorage ?? new TestSessionStorage());
        context.Services.AddSingleton<ShortcutManager>();
        context.Services.AddSingleton<IDashboardMessageService>(messageService ?? new DashboardMessageService());
        context.Services.AddSingleton<DashboardTelemetryService>();
        context.Services.AddSingleton<IDashboardTelemetrySender, TestDashboardTelemetrySender>();
        context.Services.AddSingleton<ComponentTelemetryContextProvider>();
        context.Services.AddSingleton<ITelemetryErrorRecorder, TestTelemetryErrorRecorder>();
        context.Services.AddSingleton<ThemeManager>(themeManager ?? new ThemeManager(new TestThemeResolver()));
        context.Services.AddSingleton<DimensionManager>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<INotificationService, NotificationService>();
        context.Services.AddSingleton<IDashboardToastService, DashboardToastService>();
        context.Services.AddScoped<DashboardDialogService>();
        context.Services.AddScoped<ResourceMenuBuilder>();
        context.Services.AddScoped<StructuredLogMenuBuilder>();
        context.Services.AddScoped<SpanMenuBuilder>();
        context.Services.AddScoped<TraceMenuBuilder>();
        context.Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions()));

        var splitViewModule = context.JSInterop.SetupModule("./Components/Controls/ResizableSplitView.razor.js");
        splitViewModule.SetupVoid("initializeSplitView", _ => true);
        splitViewModule.SetupVoid("disposeSplitView", _ => true);

        SetupColumnResizer(context);
        SetupUrlsColumnDisplay(context);
    }

    public static void SetupColumnResizer(TestContext context)
    {
        var resizerModule = context.JSInterop.SetupModule("./Components/Controls/Grid/ColumnResizer.razor.js");
        resizerModule.SetupVoid("initialize", _ => true);
        resizerModule.SetupVoid("dispose", _ => true);
    }

    public static void SetupUrlsColumnDisplay(TestContext context)
    {
        var urlsModule = context.JSInterop.SetupModule("./Components/ResourcesGridColumns/UrlsColumnDisplay.razor.js");
        urlsModule.SetupVoid("initialize", _ => true);
        urlsModule.SetupVoid("measure", _ => true);
        urlsModule.SetupVoid("dispose", _ => true);
    }

    public static void SetupDialogInfrastructure(
        TestContext context,
        ThemeManager? themeManager = null,
        ILocalStorage? localStorage = null)
    {
        AddCommonDashboardServices(context, localStorage: localStorage, themeManager: themeManager);
    }

    public static IRenderedFragment RenderDialogProvider(TestContext context)
    {
        SetupDialogProvider(context);
        return RenderDialogProviderCore(context);
    }

    public static IRenderedFragment RenderDialogProviderCore(TestContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<Aspire.Dashboard.Components.Dialogs.DeckDialogProvider>(0);
            builder.CloseComponent();
        });
    }
}
