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
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class FluentUISetupHelpers
{
    private static readonly Version s_fluentUIVersion = typeof(FluentButton).Assembly.GetName().Version!;

    private static string GetFluentFile(string filePath)
    {
        return $"{filePath}?v={s_fluentUIVersion}";
    }

    public static void SetupFluentDialogProvider(TestContext context)
    {
        var dialogProviderModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Dialog/FluentDialogProvider.razor.js"));
        dialogProviderModule.SetupModule("getActiveElement", _ => true);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Dialog.Show", _ => true);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Dialog.Hide", _ => true);
    }

    public static void SetupFluentMenu(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Menu.Initialize", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Menu.OpenMenu", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Menu.CloseMenu", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Menu.Dispose", _ => true).SetVoidResult();
    }

    public static void SetupFluentOverflow(TestContext context)
    {
        var overflowModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Overflow/FluentOverflow.razor.js"));
        overflowModule.SetupVoid("fluentOverflowInitialize", _ => true);
        overflowModule.SetupVoid("fluentOverflowDispose", _ => true);
    }

    public static void SetupFluentAnchor(TestContext context)
    {
        context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Anchor/FluentAnchor.razor.js"));
    }

    public static void SetupFluentAnchoredRegion(TestContext context)
    {
        var module = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/AnchoredRegion/FluentAnchoredRegion.razor.js"));
        module.SetupVoid("goToNextFocusableElement", _ => true);
        module.SetupVoid("initializeKeyboardNavigation", _ => true);
        module.SetupVoid("removeKeyboardNavigation", _ => true);
    }

    public static void SetupFluentDivider(TestContext context)
    {
        var dividerModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Divider/FluentDivider.razor.js"));
        dividerModule.SetupVoid("setDividerAriaOrientation");
    }

    public static void SetupFluentDataGrid(TestContext context)
    {
        var dataGridModule = context.JSInterop.SetupModule("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/DataGrid/FluentDataGrid.razor.js");
        dataGridModule.SetupVoid("Microsoft.FluentUI.Blazor.DataGrid.EnableColumnResizing", _ => true);

        var gridReference = dataGridModule.SetupModule("Microsoft.FluentUI.Blazor.DataGrid.Initialize", _ => true);
        gridReference.SetupVoid("stop", _ => true);
    }

    public static void SetupFluentSearch(TestContext context)
    {
        var searchModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Search/FluentSearch.razor.js"));
        searchModule.SetupVoid("addAriaHidden", _ => true);
    }

    public static void SetupFluentKeyCode(TestContext context)
    {
        var keycodeModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/KeyCode/FluentKeyCode.razor.js"));
        keycodeModule.Setup<string>("RegisterKeyCode", _ => true);
    }

    public static void SetupFluentToolbar(TestContext context)
    {
        var toolbarModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Toolbar/FluentToolbar.razor.js"));
        toolbarModule.SetupVoid("removePreventArrowKeyNavigation", _ => true);
    }

    public static void SetupFluentInputLabel(TestContext context)
    {
        var inputLabelModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Label/FluentInputLabel.razor.js"));
        inputLabelModule.SetupVoid("setInputAriaLabel", _ => true);
    }

    public static void SetupFluentList(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Select.Initialize", _ => true);
    }

    public static void SetupFluentTab(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Components.Tabs.ObserveTabsChanged", _ => true);
    }

    public static void SetupFluentCheckbox(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true);
        var checkboxModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Checkbox/FluentCheckbox.razor.js"));
        checkboxModule.SetupVoid("setFluentCheckBoxIndeterminate", _ => true);
        checkboxModule.SetupVoid("stop", _ => true);
    }

    public static void SetupFluentTextField(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.applyShadowStyle", _ => true);
    }

    public static void SetupFluentButton(TestContext context)
    {
        var buttonModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Button/FluentButton.razor.js"));
        buttonModule.SetupVoid("updateProxy", _ => true);
    }

    public static void SetupFluentInputFile(TestContext context)
    {
        var inputFileModule = context.JSInterop.SetupModule("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/InputFile/FluentInputFile.razor.js");
        inputFileModule.SetupVoid("Microsoft.FluentUI.Blazor.InputFile.AttachClickHandler", _ => true);
        var dropZoneReference = inputFileModule.SetupModule("Microsoft.FluentUI.Blazor.InputFile.InitializeFileDropZone", _ => true);
        dropZoneReference.SetupVoid("dispose", _ => true);
    }

    public static void SetupFluentCombobox(TestContext context)
    {
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.copyToShadow", _ => true);
    }

    public static void AddCommonDashboardServices(
        TestContext context,
        ILocalStorage? localStorage = null,
        ISessionStorage? sessionStorage = null,
        ThemeManager? themeManager = null,
        BrowserTimeProvider? browserTimeProvider = null)
    {
        context.Services.AddFluentUIComponents();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.copyToShadow", _ => true);
        var tooltipModule = context.JSInterop.SetupModule("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Tooltip/FluentTooltip.razor.js");
        tooltipModule.SetupVoid("Microsoft.FluentUI.Blazor.Tooltip.FluentTooltipInitialize", _ => true);
        context.Services.AddLocalization();
        context.Services.AddSingleton<BrowserTimeProvider>(browserTimeProvider ?? new TestTimeProvider());
        context.Services.AddSingleton<TelemetryRepository>();
        context.Services.AddSingleton<PauseManager>();
        context.Services.AddSingleton<ILocalStorage>(localStorage ?? new TestLocalStorage());
        context.Services.AddSingleton<ISessionStorage>(sessionStorage ?? new TestSessionStorage());
        context.Services.AddSingleton<ShortcutManager>();
        context.Services.AddSingleton<DashboardTelemetryService>();
        context.Services.AddSingleton<IDashboardTelemetrySender, TestDashboardTelemetrySender>();
        context.Services.AddSingleton<ComponentTelemetryContextProvider>();
        context.Services.AddSingleton<ITelemetryErrorRecorder, TestTelemetryErrorRecorder>();
        context.Services.AddSingleton<ThemeManager>(themeManager ?? new ThemeManager(new TestThemeResolver()));
        context.Services.AddSingleton<DimensionManager>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<Aspire.Dashboard.Model.INotificationService, Aspire.Dashboard.Model.NotificationService>();
        context.Services.AddScoped<DashboardDialogService>();
        context.Services.AddScoped<DashboardMessageBarService>();
        context.Services.AddScoped<ResourceMenuBuilder>();
        context.Services.AddScoped<StructuredLogMenuBuilder>();
        context.Services.AddScoped<SpanMenuBuilder>();
        context.Services.AddScoped<TraceMenuBuilder>();
        context.Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions()));
    }

    public static void SetupFluentUIComponents(TestContext context)
    {
        context.Services.AddFluentUIComponents();
    }

    public static void SetupDialogInfrastructure(
        TestContext context,
        ThemeManager? themeManager = null,
        ILocalStorage? localStorage = null)
    {
        AddCommonDashboardServices(context, localStorage: localStorage, themeManager: themeManager);
        SetupFluentUIComponents(context);
        SetupFluentDialogProvider(context);
    }

    public static IRenderedFragment RenderDialogProvider(TestContext context)
    {
        return context.Render(builder =>
        {
            builder.OpenComponent<FluentDialogProvider>(0);
            builder.CloseComponent();
        });
    }
}
