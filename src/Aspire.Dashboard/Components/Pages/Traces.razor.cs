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

public partial class Traces : IComponentWithTelemetry, IPageWithSessionAndUrlState<Traces.TracesPageViewModel, Traces.TracesPageState>
{
    private const string TimestampColumn = nameof(TimestampColumn);
    private const string NameColumn = nameof(NameColumn);
    private const string SpansColumn = nameof(SpansColumn);
    private const string DurationColumn = nameof(DurationColumn);
    private const string ActionsColumn = nameof(ActionsColumn);
    private IList<GridColumn> _gridColumns = null!;
    private SelectViewModel<ResourceTypeDetails> _allResource = null!;

    private TotalItemsFooter _totalItemsFooter = default!;
    private ExplainErrorsButton? _explainErrorsButton;
    private int _totalItemsCount;
    private List<SelectViewModel<SpanType>> _spanTypes = default!;
    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private Subscription? _resourcesSubscription;
    private Subscription? _tracesSubscription;
    private bool _resourceChanged;
    private string _filter = string.Empty;
    private AspirePageContentLayout? _contentLayout;
    private FluentDataGrid<OtlpTrace> _dataGrid = null!;
    private GridColumnManager _manager = null!;

    private ColumnResizeLabels _resizeLabels = ColumnResizeLabels.Default;
    private ColumnSortLabels _sortLabels = ColumnSortLabels.Default;
    private AIContext? _aiContext;

    public string SessionStorageKey => BrowserStorageKeys.TracesPageState;
    public string BasePath => DashboardUrls.TracesBasePath;
    public TracesPageViewModel PageViewModel { get; set; } = null!;

    [Parameter]
    public string? ResourceName { get; set; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required TracesViewModel TracesViewModel { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IOptions<DashboardOptions> DashboardOptions { get; init; }

    [Inject]
    public required IMessageService MessageService { get; init; }

    [Inject]
    public required ILogger<Traces> Logger { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; set; }

    [Inject]
    public required ISessionStorage SessionStorage { get; set; }

    [Inject]
    public required DimensionManager DimensionManager { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required ITelemetryErrorRecorder ErrorRecorder { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "type")]
    public string? SpanTypeText { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "filters")]
    public string? SerializedFilters { get; set; }

    private string GetNameTooltip(OtlpTrace trace)
    {
        var tooltip = string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.TracesFullName)], trace.FullName);
        tooltip += Environment.NewLine + string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.TracesTraceId)], trace.TraceId);

        return tooltip;
    }

    private string GetSpansTooltip(OrderedResource resourceSpans)
    {
        var count = resourceSpans.TotalSpans;
        var errorCount = resourceSpans.ErroredSpans;

        var tooltip = string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.TracesResourceSpans)], GetResourceName(resourceSpans.Resource));
        tooltip += Environment.NewLine + string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.TracesTotalTraces)], count);
        if (errorCount > 0)
        {
            tooltip += Environment.NewLine + string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.TracesTotalErroredTraces)], errorCount);
        }

        return tooltip;
    }

    private async ValueTask<GridItemsProviderResult<OtlpTrace>> GetData(GridItemsProviderRequest<OtlpTrace> request)
    {
        TracesViewModel.StartIndex = request.StartIndex;
        TracesViewModel.Count = request.Count ?? DashboardUIHelpers.DefaultDataGridResultCount;
        var traces = TracesViewModel.GetTraces();

        if (traces.IsFull && !TelemetryRepository.HasDisplayedMaxTraceLimitMessage)
        {
            TelemetryRepository.MaxTraceLimitMessage = await DashboardUIHelpers.DisplayMaxLimitMessageAsync(
                MessageService,
                Loc[nameof(Dashboard.Resources.Traces.MessageExceededLimitTitle)],
                string.Format(CultureInfo.InvariantCulture, Loc[nameof(Dashboard.Resources.Traces.MessageExceededLimitBody)], DashboardOptions.Value.TelemetryLimits.MaxTraceCount),
                () => TelemetryRepository.MaxTraceLimitMessage = null);

            TelemetryRepository.HasDisplayedMaxTraceLimitMessage = true;
        }
        else if (!traces.IsFull && TelemetryRepository.MaxTraceLimitMessage is { } message)
        {
            // Telemetry could have been cleared from the dashboard. Automatically remove full message on data update.
            message.Close();
        }

        // Updating the total item count as a field doesn't work because it isn't updated with the grid.
        // The workaround is to explicitly update and refresh the control.
        _totalItemsCount = traces.TotalItemCount;
        _totalItemsFooter.UpdateDisplayedCount(_totalItemsCount);

        _explainErrorsButton?.UpdateHasErrors(TracesViewModel.HasErrors());
        _aiContext?.ContextHasChanged();

        return GridItemsProviderResult.From(traces.Items, traces.TotalItemCount);
    }

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _aiContext = CreateAIContext();

        (_resizeLabels, _sortLabels) = DashboardUIHelpers.CreateGridLabels(ControlsStringsLoc);

        _gridColumns = [
            new GridColumn(Name: TimestampColumn, DesktopWidth: "0.8fr", MobileWidth: "0.8fr"),
            new GridColumn(Name: NameColumn, DesktopWidth: "2fr", MobileWidth: "2fr"),
            new GridColumn(Name: SpansColumn, DesktopWidth: "3fr"),
            new GridColumn(Name: DurationColumn, DesktopWidth: "0.8fr"),
            new GridColumn(Name: ActionsColumn, DesktopWidth: "0.5fr", MobileWidth: "1fr")
        ];

        _allResource = new SelectViewModel<ResourceTypeDetails> { Id = null, Name = ControlsStringsLoc[name: nameof(ControlsStrings.LabelAll)] };
        _spanTypes = SpanType.CreateKnownSpanTypes(ControlsStringsLoc);
        PageViewModel = new TracesPageViewModel { SelectedResource = _allResource, SelectedSpanType = _spanTypes[0] };

        UpdateResources();
        _resourcesSubscription = TelemetryRepository.OnNewResources(callback: () => InvokeAsync(workItem: () =>
        {
            UpdateResources();
            StateHasChanged();
        }));
    }

    protected override async Task OnParametersSetAsync()
    {
        if (await this.InitializeViewModelAsync())
        {
            return;
        }

        TracesViewModel.ResourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();
        UpdateSubscription();

        _aiContext?.ContextHasChanged();
    }

    private void UpdateResources()
    {
        _resources = TelemetryRepository.GetResources(includeUninstrumentedPeers: true);
        _resourceViewModels = ResourcesSelectHelpers.CreateResources(_resources);
        _resourceViewModels.Insert(0, _allResource);

        UpdateSubscription();
    }

    private Task HandleSelectedResourceChanged()
    {
        _resourceChanged = true;

        return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);
    }

    private async Task HandleSelectedSpanTypeChangedAsync()
    {
        //await ClearSelectedLogEntryAsync();
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);
    }

    private void UpdateSubscription()
    {
        var selectedResourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();

        // Subscribe to updates.
        if (_tracesSubscription is null || _tracesSubscription.ResourceKey != selectedResourceKey)
        {
            _tracesSubscription?.Dispose();
            _tracesSubscription = TelemetryRepository.OnNewTraces(selectedResourceKey, SubscriptionType.Read, async () =>
            {
                TracesViewModel.ClearData();
                await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
            });
        }
    }

    private async Task HandleAfterFilterBindAsync()
    {
        TracesViewModel.FilterText = _filter;
        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
    }

    private string GetResourceName(OtlpResource app) => OtlpHelpers.GetResourceName(app, _resources);
    private string GetResourceName(OtlpResourceView app) => OtlpHelpers.GetResourceName(app.Resource, _resources);

    private static string GetRowClass(OtlpTrace entry)
    {
        if (entry.Spans.Any(span => span.Status == OtlpSpanStatusCode.Error))
        {
            return "trace-row-error";
        }

        return string.Empty;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Check to see whether max item count should be set on every render.
        // This is required because the data grid's virtualize component can be recreated on data change.
        if (_dataGrid != null && FluentDataGridHelper<OtlpTrace>.TrySetMaxItemCount(_dataGrid, 10_000))
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

    private string? PauseText => PauseManager.AreTracesPaused(out var startTime)
        ? string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.Traces.PauseInProgressText)],
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, startTime.Value, MillisecondsDisplay.Truncated))
        : null;

    public void Dispose()
    {
        _aiContext?.Dispose();
        _resourcesSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        DimensionManager.OnViewportInformationChanged -= OnBrowserResize;
    }

    public async Task UpdateViewModelFromQueryAsync(TracesPageViewModel viewModel)
    {
        viewModel.SelectedResource = _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: true, _allResource);
        TracesViewModel.ResourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();

        viewModel.SelectedSpanType = _spanTypes.SingleOrDefault(t => t.Id?.Name == SpanTypeText) ?? _spanTypes[0];
        TracesViewModel.SpanType = viewModel.SelectedSpanType.Id;

        if (SerializedFilters is not null)
        {
            var filters = TelemetryFilterFormatter.DeserializeFiltersFromString(SerializedFilters);

            if (filters.Count > 0)
            {
                TracesViewModel.ClearFilters();
                foreach (var filter in filters)
                {
                    TracesViewModel.AddFilter(filter);
                }
            }
        }

        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
    }

    public string GetUrlFromSerializableViewModel(TracesPageState serializable)
    {
        var filters = (serializable.Filters.Count > 0) ? TelemetryFilterFormatter.SerializeFiltersToString(serializable.Filters) : null;

        return DashboardUrls.TracesUrl(
            resource: serializable.SelectedResource,
            type: serializable.SelectedSpanType,
            filters: filters);
    }

    public TracesPageState ConvertViewModelToSerializable()
    {
        return new TracesPageState
        {
            SelectedResource = PageViewModel.SelectedResource.Id is not null ? PageViewModel.SelectedResource.Name : null,
            SelectedSpanType = PageViewModel.SelectedSpanType.Id?.Name,
            Filters = TracesViewModel.Filters
        };
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
            Width = "450px"
        };
        var data = new FilterDialogViewModel
        {
            Filter = entry,
            PropertyKeys = TelemetryRepository.GetTracePropertyKeys(PageViewModel.SelectedResource.Id?.GetResourceKey()),
            KnownKeys = KnownTraceFields.AllFields,
            GetFieldValues = TelemetryRepository.GetTraceFieldValues
        };
        await DialogService.ShowPanelAsync<FilterDialog>(data, parameters);
    }

    private async Task HandleFilterDialog(DialogResult result)
    {
        if (result.Data is FilterDialogResult filterResult && filterResult.Filter is FieldTelemetryFilter filter)
        {
            if (filterResult.Delete)
            {
                TracesViewModel.RemoveFilter(filter);
            }
            else if (filterResult.Add)
            {
                TracesViewModel.AddFilter(filter);
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

    private async Task ExplainErrorsAsync()
    {
        await AIContextProvider.LaunchAssistantSidebarAsync(
            promptContext => PromptContextsBuilder.ErrorTraces(
                promptContext,
                AIPromptsLoc[nameof(AIPrompts.PromptErrorTraces)],
                () => TracesViewModel.GetErrorTraces(count: int.MaxValue)));
    }

    private Task ClearTraces(ResourceKey? key)
    {
        TelemetryRepository.ClearTraces(key);
        return Task.CompletedTask;
    }

    private List<MenuButtonItem> GetFilterMenuItems()
    {
        return this.GetFilterMenuItems(
            TracesViewModel.Filters,
            clearFilters: TracesViewModel.ClearFilters,
            openFilterAsync: OpenFilterAsync,
            filterLoc: FilterLoc,
            dialogsLoc: DialogsLoc,
            contentLayout: _contentLayout);
    }

    private static bool HasGenAISpans(OtlpTrace trace)
    {
        foreach (var span in trace.Spans)
        {
            if (GenAIHelpers.HasGenAIAttribute(span.Attributes))
            {
                return true;
            }
        }

        return false;
    }

    private async Task OnGenAIClickedAsync(OtlpTrace trace)
    {
        var firstSpan = trace.Spans.FirstOrDefault(s => GenAIHelpers.HasGenAIAttribute(s.Attributes));
        if (firstSpan == null)
        {
            return;
        }

        await GenAIVisualizerDialog.OpenDialogAsync(
            DialogService,
            firstSpan,
            selectedLogEntryId: null,
            TelemetryRepository,
            ErrorRecorder,
            _resources,
            () =>
            {
                var latestTrace = TelemetryRepository.GetTrace(trace.TraceId);
                if (latestTrace is null)
                {
                    return [];
                }
                return latestTrace.Spans.Where(span => GenAIHelpers.HasGenAIAttribute(span.Attributes)).ToList();
            });
    }

    private AIContext CreateAIContext()
    {
        return AIContextProvider.AddNew(nameof(Traces), c =>
        {
            c.BuildIceBreakers = (builder, context) =>
            {
                var resource = _resources?.SingleOrDefault(a => a.ResourceKey == PageViewModel.SelectedResource.Id?.GetResourceKey());
                if (resource != null)
                {
                    builder.Traces(context, resource, TracesViewModel.GetTraces, TracesViewModel.HasErrors(), () => TracesViewModel.GetErrorTraces(int.MaxValue));
                }
                else
                {
                    builder.Traces(context, TracesViewModel.GetTraces, TracesViewModel.HasErrors(), () => TracesViewModel.GetErrorTraces(int.MaxValue));
                }
            };
        });
    }

    public class TracesPageViewModel
    {
        public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
        public required SelectViewModel<SpanType> SelectedSpanType { get; set; }
    }

    public class TracesPageState
    {
        public string? SelectedResource { get; set; }
        public string? SelectedSpanType { get; set; }
        public required IReadOnlyCollection<FieldTelemetryFilter> Filters { get; set; }
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.Traces);
}
