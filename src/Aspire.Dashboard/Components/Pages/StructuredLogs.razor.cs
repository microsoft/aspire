// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Assistant.Prompts;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Pages;

public partial class StructuredLogs : IComponentWithTelemetry, IPageWithSessionAndUrlState<StructuredLogs.StructuredLogsPageViewModel, StructuredLogs.StructuredLogsPageState>
{
    private const string ResourceColumn = nameof(ResourceColumn);
    private const string LogLevelColumn = nameof(LogLevelColumn);
    private const string TimestampColumn = nameof(TimestampColumn);
    private const string MessageColumn = nameof(MessageColumn);
    private const string TraceColumn = nameof(TraceColumn);
    private const string ActionsColumn = nameof(ActionsColumn);

    private SelectViewModel<ResourceTypeDetails> _allResource = default!;

    private TotalItemsFooter _totalItemsFooter = default!;
    private ExplainErrorsButton? _explainErrorsButton;
    private int _totalItemsCount;
    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private List<SelectViewModel<LogLevel?>> _logLevels = default!;
    private Subscription? _resourcesSubscription;
    private Subscription? _logsSubscription;
    private bool _resourceChanged;
    private string? _elementIdBeforeDetailsViewOpened;
    private AspirePageContentLayout? _contentLayout;
    private string _filter = string.Empty;
    private FluentDataGrid<OtlpLogEntry>? _dataGrid;
    private GridColumnManager _manager = null!;
    private IList<GridColumn> _gridColumns = null!;

    private ColumnResizeLabels _resizeLabels = ColumnResizeLabels.Default;
    private ColumnSortLabels _sortLabels = ColumnSortLabels.Default;
    private AIContext? _aiContext;

    public string BasePath => DashboardUrls.StructuredLogsBasePath;
    public string SessionStorageKey => BrowserStorageKeys.StructuredLogsPageState;
    public StructuredLogsPageViewModel PageViewModel { get; set; } = null!;

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required StructuredLogsViewModel ViewModel { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required ILogger<StructuredLogs> Logger { get; init; }

    [Inject]
    public required ITelemetryErrorRecorder ErrorRecorder { get; init; }

    [Inject]
    public required DimensionManager DimensionManager { get; set; }

    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    [Inject]
    public required IMessageService MessageService { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Parameter]
    public string? ResourceName { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string? TraceId { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string? SpanId { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "logLevel")]
    public string? LogLevelText { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "filters")]
    public string? SerializedFilters { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public long? LogEntryId { get; set; }

    public StructureLogsDetailsViewModel? SelectedLogEntry { get; set; }

    private async ValueTask<GridItemsProviderResult<OtlpLogEntry>> GetData(GridItemsProviderRequest<OtlpLogEntry> request)
    {
        ViewModel.StartIndex = request.StartIndex;
        ViewModel.Count = request.Count ?? DashboardUIHelpers.DefaultDataGridResultCount;

        var logs = ViewModel.GetLogs();

        if (logs.IsFull && !TelemetryRepository.HasDisplayedMaxLogLimitMessage)
        {
            TelemetryRepository.MaxLogLimitMessage = await DashboardUIHelpers.DisplayMaxLimitMessageAsync(
                MessageService,
                Loc[nameof(Dashboard.Resources.StructuredLogs.MessageExceededLimitTitle)],
                string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.StructuredLogs.MessageExceededLimitBody)], DashboardOptions.Value.TelemetryLimits.MaxLogCount),
                () => TelemetryRepository.MaxLogLimitMessage = null);

            TelemetryRepository.HasDisplayedMaxLogLimitMessage = true;
        }
        else if (!logs.IsFull && TelemetryRepository.MaxLogLimitMessage is { } message)
        {
            // Telemetry could have been cleared from the dashboard. Automatically remove full message on data update.
            message.Close();
        }

        // Updating the total item count as a field doesn't work because it isn't updated with the grid.
        // The workaround is to explicitly update and refresh the control.
        _totalItemsCount = logs.TotalItemCount;
        _totalItemsFooter?.UpdateDisplayedCount(_totalItemsCount);

        _explainErrorsButton?.UpdateHasErrors(ViewModel.HasErrors());
        _aiContext?.ContextHasChanged();

        TelemetryRepository.MarkViewedErrorLogs(ViewModel.ResourceKey);

        return GridItemsProviderResult.From(logs.Items, logs.TotalItemCount);
    }

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _aiContext = CreateAIContext();

        (_resizeLabels, _sortLabels) = DashboardUIHelpers.CreateGridLabels(ControlsStringsLoc);

        _gridColumns = [
            new GridColumn(Name: ResourceColumn, DesktopWidth: "2fr", MobileWidth: "1fr"),
            new GridColumn(Name: LogLevelColumn, DesktopWidth: "1fr"),
            new GridColumn(Name: TimestampColumn, DesktopWidth: "1.5fr"),
            new GridColumn(Name: MessageColumn, DesktopWidth: "5fr", "2.5fr"),
            new GridColumn(Name: TraceColumn, DesktopWidth: "1fr"),
            new GridColumn(Name: ActionsColumn, DesktopWidth: "1fr", MobileWidth: "0.8fr")
        ];

        if (!string.IsNullOrEmpty(TraceId))
        {
            ViewModel.AddFilter(new FieldTelemetryFilter
            {
                Field = KnownStructuredLogFields.TraceIdField, Condition = FilterCondition.Equals, Value = TraceId
            });
        }
        if (!string.IsNullOrEmpty(SpanId))
        {
            ViewModel.AddFilter(new FieldTelemetryFilter
            {
                Field = KnownStructuredLogFields.SpanIdField, Condition = FilterCondition.Equals, Value = SpanId
            });
        }

        _allResource = new()
        {
            Id = null,
            Name = ControlsStringsLoc[nameof(Dashboard.Resources.ControlsStrings.LabelAll)]
        };

        _logLevels = new List<SelectViewModel<LogLevel?>>
        {
            new SelectViewModel<LogLevel?> { Id = null, Name = ControlsStringsLoc[nameof(Dashboard.Resources.ControlsStrings.LabelAll)] },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Trace, Name = "Trace" },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Debug, Name = "Debug" },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Information, Name = "Information" },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Warning, Name = "Warning" },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Error, Name = "Error" },
            new SelectViewModel<LogLevel?> { Id = LogLevel.Critical, Name = "Critical" },
        };

        PageViewModel = new StructuredLogsPageViewModel
        {
            SelectedLogLevel = _logLevels[0],
            SelectedResource = _allResource
        };

        UpdateResources();
        _resourcesSubscription = TelemetryRepository.OnNewResources(() => InvokeAsync(() =>
        {
            UpdateResources();
            StateHasChanged();
        }));
    }

    private AIContext CreateAIContext()
    {
        return AIContextProvider.AddNew(nameof(StructuredLogs), c =>
        {
            c.BuildIceBreakers = (builder, context) =>
            {
                var application = _resources?.SingleOrDefault(a => a.ResourceKey == PageViewModel.SelectedResource.Id?.GetResourceKey());
                if (application != null)
                {
                    builder.StructuredLogs(context, application, ViewModel.GetLogs, ViewModel.HasErrors(), () => ViewModel.GetErrorLogs(int.MaxValue));
                }
                else
                {
                    builder.StructuredLogs(context, ViewModel.GetLogs, ViewModel.HasErrors(), () => ViewModel.GetErrorLogs(int.MaxValue));
                }
            };
        });
    }

    protected override async Task OnParametersSetAsync()
    {
        if (await this.InitializeViewModelAsync())
        {
            return;
        }

        UpdateSubscription();
        UpdateTelemetryProperties();

        _aiContext?.ContextHasChanged();

        if (LogEntryId is not null)
        {
            var logEntryId = TelemetryRepository.GetLog(LogEntryId.Value);
            if (logEntryId != null)
            {
                await OnShowPropertiesAsync(logEntryId, buttonId: null);
            }

            // Navigate to remove ?logEntryId=xxx in the URL.
            NavigationManager.NavigateTo(DashboardUrls.StructuredLogsUrl(), new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    private void UpdateResources()
    {
        _resources = TelemetryRepository.GetResources();
        _resourceViewModels = ResourcesSelectHelpers.CreateResources(_resources);
        _resourceViewModels.Insert(0, _allResource);
    }

    private Task HandleSelectedResourceChangedAsync()
    {
        _resourceChanged = true;

        return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);
    }

    private async Task HandleSelectedLogLevelChangedAsync()
    {
        _resourceChanged = true;

        await ClearSelectedLogEntryAsync();
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);
    }

    private void UpdateSubscription()
    {
        // Subscribe to updates.
        if (_logsSubscription is null || _logsSubscription.ResourceKey != PageViewModel.SelectedResource.Id?.GetResourceKey())
        {
            _logsSubscription?.Dispose();
            _logsSubscription = TelemetryRepository.OnNewLogs(PageViewModel.SelectedResource.Id?.GetResourceKey(), SubscriptionType.Read, async () =>
            {
                ViewModel.ClearData();
                await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
            });
        }
    }

    private async Task OnShowPropertiesAsync(OtlpLogEntry entry, string? buttonId)
    {
        _elementIdBeforeDetailsViewOpened = buttonId;

        if (SelectedLogEntry?.LogEntry.InternalId == entry.InternalId)
        {
            await ClearSelectedLogEntryAsync();
        }
        else
        {
            var logEntryViewModel = new StructureLogsDetailsViewModel
            {
                LogEntry = entry
            };

            SelectedLogEntry = logEntryViewModel;
        }
    }

    private async Task ClearSelectedLogEntryAsync(bool causedByUserAction = false)
    {
        SelectedLogEntry = null;

        if (_elementIdBeforeDetailsViewOpened is not null && causedByUserAction)
        {
            await JS.InvokeVoidAsync("focusElement", _elementIdBeforeDetailsViewOpened);
        }

        _elementIdBeforeDetailsViewOpened = null;
    }

    private async Task ExplainErrorsAsync()
    {
        await AIContextProvider.LaunchAssistantSidebarAsync(
            promptContext => PromptContextsBuilder.ErrorStructuredLogs(
                promptContext,
                AIPromptsLoc[nameof(AIPrompts.PromptErrorsStructuredLogs)],
                () => ViewModel.GetErrorLogs(count: int.MaxValue)));
    }

    private async Task OpenFilterAsync(FieldTelemetryFilter? entry)
    {
        if (_contentLayout is not null)
        {
            await _contentLayout.CloseMobileToolbarAsync();
        }

        var title = entry is not null ? FilterLoc[nameof(StructuredFiltering.DialogTitleEditFilter)] : FilterLoc[nameof(StructuredFiltering.DialogTitleAddFilter)];
        var parameters = new DialogParameters
        {
            OnDialogResult = DialogService.CreateDialogCallback(this, HandleFilterDialog),
            Title = title,
            Alignment = HorizontalAlignment.Right,
            PrimaryAction = null,
            SecondaryAction = null,
            Width = ViewportInformation.IsDesktop ? "450px" : "100%"
        };
        var data = new FilterDialogViewModel
        {
            Filter = entry,
            PropertyKeys = TelemetryRepository.GetLogPropertyKeys(PageViewModel.SelectedResource.Id?.GetResourceKey()),
            KnownKeys = KnownStructuredLogFields.AllFields,
            GetFieldValues = TelemetryRepository.GetLogsFieldValues
        };

        await DialogService.ShowPanelAsync<FilterDialog>(data, parameters);
    }

    private async Task HandleFilterDialog(DialogResult result)
    {
        if (result.Data is FilterDialogResult filterResult && filterResult.Filter is FieldTelemetryFilter filter)
        {
            if (filterResult.Delete)
            {
                ViewModel.RemoveFilter(filter);
            }
            else if (filterResult.Add)
            {
                ViewModel.AddFilter(filter);
                await ClearSelectedLogEntryAsync();
            }
            else if (filterResult.Enable)
            {
                filter.Enabled = true;
            }
            else if (filterResult.Disable)
            {
                filter.Enabled = false;
            }
        }

        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private async Task HandleAfterFilterBindAsync()
    {
        ViewModel.FilterText = _filter;
        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);

        if (string.IsNullOrEmpty(_filter))
        {
            return;
        }

        await ClearSelectedLogEntryAsync();
    }

    private string GetResourceName(OtlpResourceView app) => OtlpHelpers.GetResourceName(app.Resource, _resources);

    private string GetRowClass(OtlpLogEntry entry)
    {
        if (entry.InternalId == SelectedLogEntry?.LogEntry.InternalId)
        {
            return "selected-row";
        }
        else
        {
            return $"log-row-{entry.Severity.ToString().ToLowerInvariant()}";
        }
    }

    private List<MenuButtonItem> GetFilterMenuItems()
    {
        return this.GetFilterMenuItems(
            ViewModel.Filters,
            ViewModel.ClearFilters,
            OpenFilterAsync,
            FilterLoc,
            DialogsLoc,
            _contentLayout);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Check to see whether max item count should be set on every render.
        // This is required because the data grid's virtualize component can be recreated on data change.
        if (_dataGrid != null && FluentDataGridHelper<OtlpLogEntry>.TrySetMaxItemCount(_dataGrid, 10_000))
        {
            StateHasChanged();
        }

        if (_resourceChanged)
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            _resourceChanged = false;
        }
        if (firstRender)
        {
            await JS.InvokeVoidAsync("initializeContinuousScroll");
            DimensionManager.OnViewportInformationChanged += OnBrowserResize;
        }
    }

    private void OnBrowserResize(object? o, EventArgs args)
    {
        InvokeAsync(async () =>
        {
            await JS.InvokeVoidAsync("resetContinuousScrollPosition");
            await JS.InvokeVoidAsync("initializeContinuousScroll");
        });
    }

    private string? PauseText => PauseManager.AreStructuredLogsPaused(out var startTime)
        ? string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.StructuredLogs.PauseInProgressText)],
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, startTime.Value, MillisecondsDisplay.Truncated))
        : null;

    public void Dispose()
    {
        _aiContext?.Dispose();
        _resourcesSubscription?.Dispose();
        _logsSubscription?.Dispose();
        DimensionManager.OnViewportInformationChanged -= OnBrowserResize;
        TelemetryContext.Dispose();
    }

    public string GetUrlFromSerializableViewModel(StructuredLogsPageState serializable)
    {
        var filters = (serializable.Filters.Count > 0) ? TelemetryFilterFormatter.SerializeFiltersToString(serializable.Filters) : null;

        var url = DashboardUrls.StructuredLogsUrl(
            resource: serializable.SelectedResource,
            logLevel: serializable.LogLevelText,
            filters: filters);

        return url;
    }

    public StructuredLogsPageState ConvertViewModelToSerializable()
    {
        return new StructuredLogsPageState
        {
            LogLevelText = PageViewModel.SelectedLogLevel.Id?.ToString().ToLowerInvariant(),
            SelectedResource = PageViewModel.SelectedResource.Id is not null ? PageViewModel.SelectedResource.Name : null,
            Filters = ViewModel.Filters
        };
    }

    public async Task UpdateViewModelFromQueryAsync(StructuredLogsPageViewModel viewModel)
    {
        viewModel.SelectedResource = _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: true, _allResource);
        ViewModel.ResourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();

        if (LogLevelText is not null && Enum.TryParse<LogLevel>(LogLevelText, ignoreCase: true, out var logLevel))
        {
            PageViewModel.SelectedLogLevel = _logLevels.SingleOrDefault(e => e.Id == logLevel) ?? _logLevels[0];
        }
        else
        {
            PageViewModel.SelectedLogLevel = _logLevels[0];
        }

        ViewModel.LogLevel = PageViewModel.SelectedLogLevel.Id;

        if (SerializedFilters is not null)
        {
            var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(SerializedFilters);

            if (filters.Count > 0)
            {
                ViewModel.ClearFilters();
                foreach (var filter in filters)
                {
                    ViewModel.AddFilter(filter);
                }
            }
        }

        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
    }

    private bool IsGenAILogEntry(OtlpLogEntry logEntry)
    {
        if (string.IsNullOrEmpty(logEntry.SpanId) || string.IsNullOrEmpty(logEntry.TraceId))
        {
            return false;
        }

        if (GenAIHelpers.HasGenAIAttribute(logEntry.Attributes))
        {
            // GenAI telemetry is on the log entry.
            return true;
        }

        return ViewModel.HasGenAISpan(logEntry.TraceId, logEntry.SpanId);
    }

    private async Task LaunchGenAIVisualizerAsync(OtlpLogEntry logEntry)
    {
        var available = await TraceLinkHelpers.WaitForSpanToBeAvailableAsync(
            logEntry.TraceId,
            logEntry.SpanId,
            TelemetryRepository.GetSpan,
            DialogService,
            InvokeAsync,
            DialogsLoc,
            CancellationToken.None).ConfigureAwait(false);

        if (available)
        {
            var span = TelemetryRepository.GetSpan(logEntry.TraceId, logEntry.SpanId)!;

            await GenAIVisualizerDialog.OpenDialogAsync(
                DialogService,
                span,
                logEntry.InternalId,
                TelemetryRepository,
                ErrorRecorder,
                _resources,
                () =>
                {
                    var trace = TelemetryRepository.GetTrace(logEntry.TraceId);
                    if (trace is null)
                    {
                        return [];
                    }

                    var genAISpans = new List<OtlpSpan>();
                    foreach (var s in trace.Spans)
                    {
                        if (GenAIHelpers.HasGenAIAttribute(s.Attributes))
                        {
                            genAISpans.Add(s);
                        }
                    }

                    return genAISpans;
                });
        }
    }

    private Task ClearStructureLogs(ResourceKey? key)
    {
        TelemetryRepository.ClearStructuredLogs(key);
        return Task.CompletedTask;
    }

    public class StructuredLogsPageViewModel
    {
        public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
        public SelectViewModel<LogLevel?> SelectedLogLevel { get; set; } = default!;
    }

    public class StructuredLogsPageState
    {
        public string? SelectedResource { get; set; }
        public string? LogLevelText { get; set; }
        public required IReadOnlyCollection<FieldTelemetryFilter> Filters { get; set; }
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.StructuredLogs);

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.StructuredLogsSelectedLogLevel, new AspireTelemetryProperty(PageViewModel.SelectedLogLevel.Id?.ToString() ?? string.Empty, AspireTelemetryPropertyType.UserSetting)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.StructuredLogsFilterCount, new AspireTelemetryProperty(ViewModel.Filters.Count.ToString(CultureInfo.InvariantCulture), AspireTelemetryPropertyType.Metric))
        ], Logger);
    }
}
