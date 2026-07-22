// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.BrowserStorage;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.ServiceClient;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Tests;
using Aspire.Tests.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal static class FluentUISetupHelpers
{
    private static readonly Version s_fluentUIVersion = typeof(FluentMain).Assembly.GetName().Version!;

    private static string GetFluentFile(string filePath)
    {
        return $"{filePath}?v={s_fluentUIVersion}";
    }

    public static void SetupFluentDialogProvider(TestContext context)
    {
        var dialogProviderModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Dialog/FluentDialogProvider.razor.js"));
        dialogProviderModule.SetupModule("getActiveElement", _ => true);
    }

    public static void SetupFluentMenu(TestContext context)
    {
        var menuModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Menu/FluentMenu.razor.js"));
        menuModule.SetupVoid("initialize", _ => true);
        menuModule.SetupVoid("dispose", _ => true);
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
        var dataGridModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/DataGrid/FluentDataGrid.razor.js"));
        dataGridModule.SetupVoid("enableColumnResizing", _ => true);

        var gridReference = dataGridModule.SetupModule("init", _ => true);
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
        context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/List/ListComponentBase.razor.js"));
    }

    public static void SetupFluentTab(TestContext context)
    {
        var tabModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Tabs/FluentTab.razor.js"));
        tabModule.SetupVoid("TabEditable_Changed", _ => true);
    }

    public static void SetupFluentCheckbox(TestContext context)
    {
        var checkboxModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Checkbox/FluentCheckbox.razor.js"));
        checkboxModule.SetupVoid("setFluentCheckBoxIndeterminate", _ => true);
        checkboxModule.SetupVoid("stop", _ => true);
    }

    public static void SetupFluentTextField(TestContext context)
    {
        var textboxModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/TextField/FluentTextField.razor.js"));
        textboxModule.SetupVoid("setControlAttribute", _ => true);
        textboxModule.SetupVoid("ensureCurrentValueMatch", _ => true);
    }

    public static void SetupFluentButton(TestContext context)
    {
        var buttonModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Button/FluentButton.razor.js"));
        buttonModule.SetupVoid("updateProxy", _ => true);
    }

    public static void SetupFluentInputFile(TestContext context)
    {
        var inputFileModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/InputFile/FluentInputFile.razor.js"));
        inputFileModule.SetupVoid("attachClickHandler", _ => true);
        inputFileModule.SetupVoid("detachClickHandler", _ => true);
        var dropZoneReference = inputFileModule.SetupModule("initializeFileDropZone", _ => true);
        dropZoneReference.SetupVoid("dispose", _ => true);
    }

    public static void SetupFluentCombobox(TestContext context)
    {
        var comboboxModule = context.JSInterop.SetupModule(GetFluentFile("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/List/FluentCombobox.razor.js"));
        comboboxModule.SetupVoid("setControlAttribute", _ => true);
    }

    public static void ConfigureTelemetryRepository(
        TestContext context,
        bool readOnly,
        Action<ITelemetryRepositoryWriter> seed)
    {
        context.Services.AddSingleton(new TelemetryRepositoryConfiguration(readOnly, seed));
    }

    public static void AddCommonDashboardServices(
        TestContext context,
        ILocalStorage? localStorage = null,
        ISessionStorage? sessionStorage = null,
        ThemeManager? themeManager = null,
        IMessageService? messageService = null,
        BrowserTimeProvider? browserTimeProvider = null,
        IDashboardRunStore? dashboardRunStore = null)
    {
        context.Services.AddLocalization();
        context.Services.AddSingleton<BrowserTimeProvider>(browserTimeProvider ?? new TestTimeProvider());
        context.Services.AddSingleton(_ => TemporaryWorkspace.Create(
            global::Xunit.TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException("An active test output helper is required.")));
        context.Services.AddSingleton<SqliteTelemetryRepository>(services =>
        {
            var databasePath = Path.Combine(services.GetRequiredService<TemporaryWorkspace>().Path, "dashboard.db");
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            var options = services.GetRequiredService<IOptions<DashboardOptions>>();
            var pauseManager = services.GetRequiredService<PauseManager>();
            var outgoingPeerResolvers = services.GetServices<IOutgoingPeerResolver>();
            var configuration = services.GetService<TelemetryRepositoryConfiguration>();

            if (configuration is not null)
            {
                using var writer = new SqliteTelemetryRepository(databasePath, loggerFactory, options, new PauseManager(), outgoingPeerResolvers);
                configuration.Seed(writer);
            }

            return new SqliteTelemetryRepository(
                databasePath,
                loggerFactory,
                options,
                pauseManager,
                outgoingPeerResolvers,
                readOnly: configuration?.ReadOnly == true);
        });
        context.Services.AddSingleton<ITelemetryRepository>(services => services.GetRequiredService<SqliteTelemetryRepository>());
        context.Services.AddSingleton<ITelemetryRepositoryWriter>(services => services.GetRequiredService<SqliteTelemetryRepository>());
        context.Services.AddSingleton<PauseManager>();
        context.Services.AddSingleton<IDialogService, DialogService>();
        context.Services.AddSingleton<ILocalStorage>(localStorage ?? new TestLocalStorage());
        context.Services.AddSingleton<ISessionStorage>(sessionStorage ?? new TestSessionStorage());
        context.Services.AddSingleton<IDashboardRunStore>(dashboardRunStore ?? new TestDashboardRunStore());
        context.Services.AddSingleton<IDashboardRunSelection, TestDashboardRunSelection>();
        context.Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        context.Services.AddSingleton<IResourceRepository>(services => services.GetRequiredService<IDashboardClient>());
        context.Services.AddSingleton<IRepositoryFactory, TestRepositoryFactory>();
        context.Services.AddScoped<DashboardDataSource>();
        context.Services.AddSingleton<ShortcutManager>();
        context.Services.AddSingleton<LibraryConfiguration>();
        context.Services.AddSingleton<IKeyCodeService, KeyCodeService>();
        context.Services.AddSingleton<IMessageService>(messageService ?? new MessageService());
        context.Services.AddSingleton<DashboardTelemetryService>();
        context.Services.AddSingleton<IDashboardTelemetrySender, TestDashboardTelemetrySender>();
        context.Services.AddSingleton<ComponentTelemetryContextProvider>();
        context.Services.AddSingleton<ITelemetryErrorRecorder, TestTelemetryErrorRecorder>();
        context.Services.AddSingleton<ThemeManager>(themeManager ?? new ThemeManager(new TestThemeResolver()));
        context.Services.AddSingleton<GlobalState>();
        context.Services.AddSingleton<DimensionManager>();
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton<INotificationService, NotificationService>();
        context.Services.AddScoped<DashboardDialogService>();
        context.Services.AddScoped<ResourceMenuBuilder>();
        context.Services.AddScoped<StructuredLogMenuBuilder>();
        context.Services.AddScoped<SpanMenuBuilder>();
        context.Services.AddScoped<TraceMenuBuilder>();
        context.Services.AddSingleton<IOptions<DashboardOptions>>(Options.Create(new DashboardOptions()));
    }

    internal sealed class TestDashboardRunStore(
        IReadOnlyList<DashboardRunDescriptor>? runs = null,
        bool supportsRunSelection = true) : IDashboardRunStore
    {
        private readonly IReadOnlyList<DashboardRunDescriptor> _runs = runs ??
            [
                new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: string.Empty,
                IsCurrent: true)
            ];

        public int GetRunsCallCount { get; private set; }

        public IReadOnlyList<DashboardRunDescriptor> GetRuns()
        {
            GetRunsCallCount++;
            return _runs;
        }

        public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => null;

        public bool SupportsRunSelection => supportsRunSelection;
    }

    internal sealed class TestDashboardRunSelection(IDashboardRunStore runStore) : IDashboardRunSelection
    {
        public DashboardRunDescriptor SelectedRun { get; private set; } = runStore.GetRuns().Single(run => run.IsCurrent);

        public string? SelectedRunId { get; private set; }

        public void SelectRun(string? runId)
        {
            var runs = runStore.GetRuns();
            SelectedRun = runs.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal))
                ?? runs.Single(run => run.IsCurrent);
            SelectedRunId = SelectedRun.IsCurrent ? null : SelectedRun.RunId;
        }
    }

    private sealed class TestRepositoryFactory(
        ITelemetryRepository telemetryRepository,
        IDashboardClient dashboardClient) : IRepositoryFactory
    {
        public ITelemetryRepository CreateTelemetryRepository(DashboardSqliteDatabase database) => telemetryRepository;
        public IResourceRepository CreateResourceRepository(DashboardSqliteDatabase database) => dashboardClient;
    }

    public static void SetupFluentUIComponents(TestContext context)
    {
        context.Services.AddFluentUIComponents();

        // Setting a provider ID on menu service is required to simulate <FluentMenuProvider> on the page.
        // This makes FluentMenu render without error.
        var menuService = context.Services.GetRequiredService<IMenuService>();
        menuService.ProviderId = "Test";
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

    private sealed record TelemetryRepositoryConfiguration(bool ReadOnly, Action<ITelemetryRepositoryWriter> Seed);
}
