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
using Aspire.Dashboard.Utils;
using Aspire.Tests.Utils;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    public static async Task ConfigureTelemetryRepository(
        TestContext context,
        bool readOnly,
        Func<ITelemetryRepositoryWriter, Task> seed)
    {
        context.Services.AddSingleton(new TelemetryRepositoryConfiguration(readOnly));

        var databasePath = Path.Combine(context.Services.GetRequiredService<TemporaryWorkspace>().Path, "dashboard.db");
        var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
        var options = context.Services.GetRequiredService<IOptions<DashboardOptions>>();
        var outgoingPeerResolvers = context.Services.GetServices<IOutgoingPeerResolver>();

        using var database = new DashboardSqliteDatabase(databasePath, pooling: false);
        await database.InitializeSchemaAsync(CancellationToken.None);
        using var writer = new SqliteTelemetryRepository(
            database,
            loggerFactory,
            options,
            new PauseManager(),
            context.Services.GetRequiredService<TimeProvider>(),
            outgoingPeerResolvers);
        await seed(writer);
    }

    public static void AddCommonDashboardServices(
        TestContext context,
        ILocalStorage? localStorage = null,
        ISessionStorage? sessionStorage = null,
        ThemeManager? themeManager = null,
        BrowserTimeProvider? browserTimeProvider = null,
        IDashboardRunStore? dashboardRunStore = null)
    {
        context.Services.AddFluentUIComponents();
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.observeAttributeChange", _ => true);
        context.JSInterop.SetupVoid("Microsoft.FluentUI.Blazor.Utilities.Attributes.copyToShadow", _ => true);
        context.JSInterop.SetupVoid("Blazor.theme.setThemeMode", _ => true);
        var tooltipModule = context.JSInterop.SetupModule("./_content/Microsoft.FluentUI.AspNetCore.Components/Components/Tooltip/FluentTooltip.razor.js");
        tooltipModule.SetupVoid("Microsoft.FluentUI.Blazor.Tooltip.FluentTooltipInitialize", _ => true);
        context.Services.AddLocalization();
        context.Services.AddSingleton<BrowserTimeProvider>(browserTimeProvider ?? new TestTimeProvider());
        context.Services.AddSingleton(TimeProvider.System);
        context.Services.AddSingleton(_ => TemporaryWorkspace.Create(
            global::Xunit.TestContext.Current.TestOutputHelper ?? throw new InvalidOperationException("An active test output helper is required.")));
        context.Services.AddSingleton(services =>
        {
            var databasePath = Path.Combine(services.GetRequiredService<TemporaryWorkspace>().Path, "dashboard.db");
            var configuration = services.GetService<TelemetryRepositoryConfiguration>();
            var database = new DashboardSqliteDatabase(databasePath, readOnly: configuration?.ReadOnly == true, pooling: false);
            if (!database.IsReadOnly)
            {
                database.InitializeSchemaAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            return database;
        });
        context.Services.AddSingleton<SqliteTelemetryRepository>(services =>
        {
            var loggerFactory = services.GetRequiredService<ILoggerFactory>();
            var options = services.GetRequiredService<IOptions<DashboardOptions>>();
            var pauseManager = services.GetRequiredService<PauseManager>();
            var outgoingPeerResolvers = services.GetServices<IOutgoingPeerResolver>();

            return new SqliteTelemetryRepository(
                services.GetRequiredService<DashboardSqliteDatabase>(),
                loggerFactory,
                options,
                pauseManager,
                services.GetRequiredService<TimeProvider>(),
                outgoingPeerResolvers);
        });
        context.Services.AddSingleton<ITelemetryRepository>(services => services.GetRequiredService<SqliteTelemetryRepository>());
        context.Services.AddSingleton<ITelemetryRepositoryWriter>(services => services.GetRequiredService<SqliteTelemetryRepository>());
        context.Services.AddSingleton<PauseManager>();
        context.Services.AddScoped<IDialogService, DialogService>();
        context.Services.AddSingleton<ILocalStorage>(localStorage ?? new TestLocalStorage());
        context.Services.AddSingleton<ISessionStorage>(sessionStorage ?? new TestSessionStorage());
        context.Services.AddSingleton<IDashboardRunStore>(services => dashboardRunStore ?? new TestDashboardRunStore(
            databasePath: Path.Combine(services.GetRequiredService<TemporaryWorkspace>().Path, "dashboard.db")));
        context.Services.AddSingleton<IDashboardRunSelection, TestDashboardRunSelection>();
        context.Services.AddSingleton<IDashboardClient, TestDashboardClient>();
        context.Services.AddSingleton<IResourceRepository>(services => services.GetRequiredService<IDashboardClient>());
        context.Services.AddSingleton<IRepositoryFactory, TestRepositoryFactory>();
        context.Services.AddSingleton<DashboardDataSourcePool>();
        context.Services.AddScoped<DashboardDataSource>();
        context.Services.AddSingleton<ShortcutManager>();
        context.Services.AddSingleton<LibraryConfiguration>();
        context.Services.AddSingleton<IKeyCodeService, KeyCodeService>();
        context.Services.AddSingleton<DashboardTelemetryService>();
        context.Services.AddSingleton<DashboardActivitySource>();
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

    internal sealed class TestDashboardRunStore(
        IEnumerable<DashboardRunDescriptor>? runs = null,
        bool supportsRunSelection = true,
        string? databasePath = null) : IDashboardRunStore
    {
        private readonly IReadOnlyList<DashboardRunDescriptor> _runs = (runs ??
            [
                new(
                RunId: "current",
                SchemaVersion: DashboardRunStore.SchemaVersion,
                StartedAtUtc: DateTimeOffset.UnixEpoch,
                EndedAtUtc: null,
                CleanShutdown: false,
                ApplicationName: "TestApp",
                DatabasePath: databasePath ?? string.Empty,
                IsCurrent: true)
            ]).ToArray();

        public int GetRunsCallCount { get; private set; }

        public Action<DashboardRunDescriptor, bool>? OnSetRunPinned { get; set; }

        public IReadOnlyList<DashboardRunDescriptor> GetRuns()
        {
            GetRunsCallCount++;
            return _runs;
        }

        public DashboardRunDescriptor GetCurrentRun() => _runs.Single(run => run.IsCurrent);

        public DashboardRunDescriptor? GetRunById(string runId) =>
            _runs.SingleOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));

        public void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
        {
            OnSetRunPinned?.Invoke(run, isPinned);
            _runs.Single(candidate => string.Equals(candidate.RunId, run.RunId, StringComparison.Ordinal)).IsPinned = isPinned;
        }

        public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run) => null;

        public void PublishRun()
        {
        }

        public void PruneExpiredRuns()
        {
        }

        public bool SupportsRunSelection => supportsRunSelection;
    }

    internal sealed class TestDashboardRunSelection(IDashboardRunStore runStore) : IDashboardRunSelection
    {
        public DashboardRunDescriptor SelectedRun { get; private set; } = runStore.GetCurrentRun();

        public string? SelectedRunId { get; private set; }

        public Action<string?>? OnSelectRun { get; set; }

        public void SelectRun(string? runId)
        {
            OnSelectRun?.Invoke(runId);
            SelectedRun = runId is not null ? runStore.GetRunById(runId) ?? runStore.GetCurrentRun() : runStore.GetCurrentRun();
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

    public static void SetupFluentUIComponents(TestContext context, bool setupAspireMenuButtonModule = true)
    {
        context.Services.AddFluentUIComponents();

        if (setupAspireMenuButtonModule)
        {
            SetupAspireMenuButtonModule(context);
        }
    }

    /// <summary>
    /// Registers the JS module <see cref="AspireMenuButton"/> imports when its menu is first opened.
    /// Tests that click a menu button need this even when they configure FluentUI piecemeal.
    /// </summary>
    public static void SetupAspireMenuButtonModule(TestContext context)
    {
        var menuButtonModule = context.JSInterop.SetupModule("./Components/Controls/AspireMenuButton.razor.js");
        menuButtonModule.SetupVoid("prepareForFluentMenuInitialization", _ => true).SetVoidResult();
        menuButtonModule.SetupVoid("waitForFluentMenuInitialization", _ => true).SetVoidResult();
        menuButtonModule.SetupVoid("cancelFluentMenuInitialization", _ => true).SetVoidResult();
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

    public static IRenderedFragment RenderMessageBarProviderWithPage<TPage>(TestContext context, ViewportInformation viewport)
        where TPage : IComponent
    {
        // Keep the provider and page in the same render tree, matching MainLayout so message bars
        // raised while rendering the page are delivered to the provider under test.
        return context.Render(builder =>
        {
            builder.OpenComponent<FluentMessageBarProvider>(0);
            builder.AddComponentParameter(1, nameof(FluentMessageBarProvider.Section), DashboardUIHelpers.MessageBarSection);
            builder.CloseComponent();
            builder.OpenComponent<CascadingValue<ViewportInformation>>(2);
            builder.AddComponentParameter(3, nameof(CascadingValue<ViewportInformation>.Value), viewport);
            builder.AddComponentParameter(4, nameof(CascadingValue<ViewportInformation>.ChildContent), (RenderFragment)(contentBuilder =>
            {
                contentBuilder.OpenComponent<TPage>(5);
                contentBuilder.CloseComponent();
            }));
            builder.CloseComponent();
        });
    }

    private sealed record TelemetryRepositoryConfiguration(bool ReadOnly);
}
