// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Layout;
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
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Pages;

public partial class TraceDetail : ComponentBase, IComponentWithTelemetry, IDisposable
{
    private const string NameColumn = nameof(NameColumn);
    private const string ResourceColumn = nameof(ResourceColumn);
    private const string TicksColumn = nameof(TicksColumn);
    private const string ActionsColumn = nameof(ActionsColumn);
    private const int RootSpanDepth = 1;

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _peerChangesSubscriptions = new();
    private OtlpTrace? _trace;
    private Subscription? _tracesSubscription;
    private List<SpanWaterfallViewModel>? _spanWaterfallViewModels;
    private int _maxDepth;
    private int _resourceCount;
    private List<OtlpResource> _resources = default!;
    private readonly List<string> _collapsedSpanIds = [];
    private string? _elementIdBeforeDetailsViewOpened;
    private FluentDataGrid<SpanWaterfallViewModel> _dataGrid = null!;
    private GridColumnManager _manager = null!;
    private AIContext? _aiContext;
    private IList<GridColumn> _gridColumns = null!;
    private string _filter = string.Empty;
    private readonly List<MenuButtonItem> _traceActionsMenuItems = [];
    private AspirePageContentLayout? _layout;
    private List<SelectViewModel<SpanType>> _spanTypes = default!;
    private SelectViewModel<SpanType> _selectedSpanType = default!;

    [Parameter]
    public required string TraceId { get; set; }

    [Parameter]
    [SupplyParameterFromQuery]
    public string? SpanId { get; set; }

    [Inject]
    public required ILogger<TraceDetail> Logger { get; init; }

    [Inject]
    public required ITelemetryErrorRecorder ErrorRecorder { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required IEnumerable<IOutgoingPeerResolver> OutgoingPeerResolvers { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.TraceDetail> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.StructuredLogs> StructuredLogsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlStringsLoc { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required TraceMenuBuilder TraceMenuBuilder { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _aiContext = CreateAIContext();

        _gridColumns = [
            new GridColumn(Name: NameColumn, DesktopWidth: "7fr", MobileWidth: "7fr"),
            new GridColumn(Name: ResourceColumn, DesktopWidth: "4fr", MobileWidth: null),
            new GridColumn(Name: TicksColumn, DesktopWidth: "12fr", MobileWidth: "12fr"),
            new GridColumn(Name: ActionsColumn, DesktopWidth: "100px", MobileWidth: null)
        ];

        foreach (var resolver in OutgoingPeerResolvers)
        {
            _peerChangesSubscriptions.Add(resolver.OnPeerChanges(async () =>
            {
                UpdateDetailViewData();
                await InvokeAsync(StateHasChanged);
                await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
            }));
        }

        UpdateTraceActionsMenu();

        _spanTypes = SpanType.CreateKnownSpanTypes(ControlStringsLoc);
        _selectedSpanType = _spanTypes[0];
    }

    private void UpdateTraceActionsMenu()
    {
        if (_trace is null)
        {
            return;
        }

        _traceActionsMenuItems.Clear();

        // Add expand/collapse options
        _traceActionsMenuItems.Add(new MenuButtonItem
        {
            Text = ControlStringsLoc[nameof(ControlsStrings.ExpandAllSpansText)],
            Icon = new Icons.Regular.Size16.ArrowExpandAll(),
            OnClick = ExpandAllSpansAsync,
            IsDisabled = !HasCollapsedSpans()
        });

        _traceActionsMenuItems.Add(new MenuButtonItem
        {
            Text = ControlStringsLoc[nameof(ControlsStrings.CollapseAllSpansText)],
            Icon = new Icons.Regular.Size16.ArrowCollapseAll(),
            OnClick = CollapseAllSpansAsync,
            IsDisabled = !HasExpandedSpans()
        });

        // Add divider
        _traceActionsMenuItems.Add(new MenuButtonItem
        {
            IsDivider = true
        });

        TraceMenuBuilder.AddMenuItems(_traceActionsMenuItems, _trace, showViewDetails: false);
    }

    // Internal to be used in unit tests
    internal ValueTask<GridItemsProviderResult<SpanWaterfallViewModel>> GetData(GridItemsProviderRequest<SpanWaterfallViewModel> request)
    {
        var page = GetVisibleSpanViewModels();
        var totalItemCount = page.Count();
        if (request.StartIndex > 0)
        {
            page = page.Skip(request.StartIndex);
        }
        page = page.Take(request.Count ?? DashboardUIHelpers.DefaultDataGridResultCount);

        return ValueTask.FromResult(new GridItemsProviderResult<SpanWaterfallViewModel>
        {
            Items = page.ToList(),
            TotalItemCount = totalItemCount
        });
    }

    private IEnumerable<SpanWaterfallViewModel> GetVisibleSpanViewModels()
    {
        Debug.Assert(_spanWaterfallViewModels != null);

        var visibleViewModels = new HashSet<SpanWaterfallViewModel>();
        foreach (var viewModel in _spanWaterfallViewModels)
        {
            if (viewModel.IsHidden || visibleViewModels.Contains(viewModel))
            {
                continue;
            }

            if (viewModel.MatchesFilter(_filter, _selectedSpanType.Id?.Filter, GetResourceName, out var matchedDescendents))
            {
                visibleViewModels.Add(viewModel);
                foreach (var descendent in matchedDescendents.Where(d => !d.IsHidden))
                {
                    visibleViewModels.Add(descendent);
                }
            }
        }

        return _spanWaterfallViewModels.Where(visibleViewModels.Contains);
    }

    private string? GetPageTitle()
    {
        if (_trace is null)
        {
            return null;
        }

        var headerSpan = _trace.RootOrFirstSpan;
        return $"{GetResourceName(headerSpan.Source)}: {headerSpan.Name}";
    }

    protected override async Task OnParametersSetAsync()
    {
        if (TraceId != _trace?.TraceId)
        {
            UpdateDetailViewData();
            UpdateSubscription();

            // If parameters change after render then the grid is automatically updated.
            // Explicitly update data grid to support navigating between traces via span links.
            await _dataGrid.SafeRefreshDataAsync();

            // Update AI context with new trace.
            _aiContext?.ContextHasChanged();
        }

        if (SpanId is not null && _spanWaterfallViewModels is not null)
        {
            var spanVm = _spanWaterfallViewModels.SingleOrDefault(vm => vm.Span.SpanId == SpanId);
            if (spanVm != null)
            {
                await OnShowPropertiesAsync(spanVm, buttonId: null);
            }

            // Navigate to remove ?spanId=xxx in the URL. A small delay is required here, otherwise the page rendering breaks.
            await Task.Delay(200, _cts.Token);

            NavigationManager.NavigateTo(DashboardUrls.TraceDetailUrl(TraceId), new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    private AIContext CreateAIContext()
    {
        return AIContextProvider.AddNew(nameof(TraceDetail), c =>
        {
            c.BuildIceBreakers = (builder, context) =>
            {
                if (_trace is { } trace)
                {
                    builder.Trace(context, trace);
                }
                else
                {
                    builder.Default(context);
                }
            };
        });
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Check to see whether max item count should be set on every render.
        // This is required because the data grid's virtualize component can be recreated on data change.
        if (_dataGrid != null && FluentDataGridHelper<SpanWaterfallViewModel>.TrySetMaxItemCount(_dataGrid, 10_000))
        {
            StateHasChanged();
        }
    }

    private void UpdateDetailViewData()
    {
        _resources = TelemetryRepository.GetResources();

        // Copying a large trace can be expensive so only do this if required.
        if (_trace == null || _trace.TraceId != TraceId || TelemetryRepository.HasUpdatedTrace(_trace))
        {
            Logger.LogInformation("Getting trace '{TraceId}'.", TraceId);
            _trace = (TraceId != null) ? TelemetryRepository.GetTrace(TraceId) : null;
        }

        if (_trace == null)
        {
            Logger.LogInformation("Couldn't find trace '{TraceId}'.", TraceId);
            _spanWaterfallViewModels = null;
            _maxDepth = 0;
            _resourceCount = 0;
            UpdateTraceActionsMenu();
            return;
        }

        // Get logs for the trace. Note that there isn't a limit on this query so all logs are returned.
        // There is a limit on the number of logs stored by the dashboard so this is implicitly limited.
        // If there are performance issues with displaying all logs then consider adding a limit to this query.
        var result = TelemetryRepository.GetLogsForTrace(_trace.TraceId);

        Logger.LogInformation("Trace '{TraceId}' has {SpanCount} spans.", _trace.TraceId, _trace.Spans.Count);
        _spanWaterfallViewModels = SpanWaterfallViewModel.Create(_trace, result, new SpanWaterfallViewModel.TraceDetailState(OutgoingPeerResolvers.ToArray(), _collapsedSpanIds, _resources));
        _maxDepth = _spanWaterfallViewModels.Max(s => s.Depth);

        var apps = new HashSet<OtlpResource>();
        foreach (var span in _trace.Spans)
        {
            apps.Add(span.Source.Resource);
            if (span.UninstrumentedPeer != null)
            {
                apps.Add(span.UninstrumentedPeer);
            }
        }
        _resourceCount = apps.Count;

        UpdateTraceActionsMenu();
    }

    private async Task HandleAfterFilterBindAsync()
    {
        SelectedData = null;
        await InvokeAsync(StateHasChanged);

        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
    }

    private async Task HandleSelectedSpanTypeChangedAsync()
    {
        SelectedData = null;
        await InvokeAsync(StateHasChanged);

        await InvokeAsync(_dataGrid.SafeRefreshDataAsync);
    }

    private void UpdateSubscription()
    {
        if (_trace == null)
        {
            _tracesSubscription?.Dispose();
            return;
        }

        if (_tracesSubscription is null || _tracesSubscription.ResourceKey != _trace.FirstSpan.Source.ResourceKey)
        {
            _tracesSubscription?.Dispose();
            _tracesSubscription = TelemetryRepository.OnNewTraces(_trace.FirstSpan.Source.ResourceKey, SubscriptionType.Read, () => InvokeAsync(async () =>
            {
                if (_trace == null)
                {
                    return;
                }

                // Only update trace if required.
                if (TelemetryRepository.HasUpdatedTrace(_trace))
                {
                    UpdateDetailViewData();
                    StateHasChanged();
                    await _dataGrid.SafeRefreshDataAsync();
                }
                else
                {
                    Logger.LogTrace("Trace '{TraceId}' is unchanged.", TraceId);
                }
            }));
        }
    }

    private string GetRowClass(SpanWaterfallViewModel viewModel)
    {
        // Test with id rather than the object reference because the data and view model objects are recreated on trace updates.
        if (SelectedData?.SpanViewModel is { } selectedSpan && selectedSpan.Span.SpanId == viewModel.Span.SpanId)
        {
            return "selected-row";
        }
        else if (SelectedData?.LogEntryViewModel is { } selectedLog && viewModel.SpanLogs.Any(l => l.LogEntry.InternalId == selectedLog.LogEntry.InternalId))
        {
            return "selected-row";
        }

        return string.Empty;
    }

    public TraceDetailSelectedDataViewModel? SelectedData { get; set; }

    private async Task OnToggleCollapse(SpanWaterfallViewModel viewModel)
    {
        SetSpanCollapsedState(viewModel, !viewModel.IsCollapsed);
        await RefreshSpanViewAsync();
    }

    private void SetSpanCollapsedState(SpanWaterfallViewModel viewModel, bool isCollapsed)
    {
        // View model data is recreated if the trace updates.
        // Persist the collapsed state in a separate list.
        viewModel.IsCollapsed = isCollapsed;
        if (isCollapsed)
        {
            _collapsedSpanIds.Add(viewModel.Span.SpanId);
        }
        else
        {
            _collapsedSpanIds.Remove(viewModel.Span.SpanId);
        }
    }

    private async Task RefreshSpanViewAsync()
    {
        UpdateDetailViewData();
        UpdateTraceActionsMenu();
        await _dataGrid.SafeRefreshDataAsync();

        await InvokeAsync(StateHasChanged);

        // Close mobile toolbar if open, as the content has changed.
        Debug.Assert(_layout is not null);
        await _layout.CloseMobileToolbarAsync();
    }

    private async Task OnShowPropertiesAsync(SpanWaterfallViewModel viewModel, string? buttonId)
    {
        _elementIdBeforeDetailsViewOpened = buttonId;

        if (SelectedData?.SpanViewModel?.Span.SpanId == viewModel.Span.SpanId)
        {
            await ClearSelectedSpanAsync();
        }
        else
        {
            var spanDetailsViewModel = SpanDetailsViewModel.Create(viewModel.Span, TelemetryRepository, _resources);

            SelectedData = new TraceDetailSelectedDataViewModel
            {
                SpanViewModel = spanDetailsViewModel
            };
        }
    }

    private async Task ClearSelectedSpanAsync(bool causedByUserAction = false)
    {
        SelectedData = null;

        if (_elementIdBeforeDetailsViewOpened is not null && causedByUserAction)
        {
            await JS.InvokeVoidAsync("focusElement", _elementIdBeforeDetailsViewOpened);
        }

        _elementIdBeforeDetailsViewOpened = null;
    }

    private bool HasCollapsedSpans()
    {
        if (_spanWaterfallViewModels is null)
        {
            return false;
        }

        return _spanWaterfallViewModels.Any(vm => vm.IsCollapsed);
    }

    private bool HasExpandedSpans()
    {
        if (_spanWaterfallViewModels is null)
        {
            return false;
        }

        // Don't consider root spans (depth 0) when determining if collapse all should be enabled
        return _spanWaterfallViewModels.Any(vm => vm.Depth > RootSpanDepth && !vm.IsCollapsed && vm.Children.Count > 0);
    }

    private async Task CollapseAllSpansAsync()
    {
        if (_spanWaterfallViewModels is null)
        {
            return;
        }

        foreach (var viewModel in _spanWaterfallViewModels)
        {
            // Don't collapse root spans.
            if (viewModel.Depth > RootSpanDepth && viewModel.Children.Count > 0 && !viewModel.IsCollapsed)
            {
                SetSpanCollapsedState(viewModel, true);
            }
        }

        await RefreshSpanViewAsync();
    }

    private async Task ExpandAllSpansAsync()
    {
        if (_spanWaterfallViewModels is null)
        {
            return;
        }

        foreach (var viewModel in _spanWaterfallViewModels)
        {
            if (viewModel.IsCollapsed)
            {
                SetSpanCollapsedState(viewModel, false);
            }
        }

        await RefreshSpanViewAsync();
    }

    private string GetResourceName(OtlpResourceView app) => OtlpResource.GetResourceName(app, _resources);

    private async Task ToggleSpanLogsAsync(OtlpLogEntry logEntry)
    {
        if (SelectedData?.LogEntryViewModel?.LogEntry.InternalId == logEntry.InternalId)
        {
            await ClearSelectedSpanAsync();
        }
        else
        {
            SelectedData = new TraceDetailSelectedDataViewModel
            {
                LogEntryViewModel = new StructureLogsDetailsViewModel { LogEntry = logEntry }
            };
        }
    }

    private static bool IsGenAISpan(SpanWaterfallViewModel spanViewModel)
    {
        return GenAIHelpers.HasGenAIAttribute(spanViewModel.Span.Attributes);
    }

    private async Task OnGenAIClickedAsync(OtlpSpan span)
    {
        await GenAIVisualizerDialog.OpenDialogAsync(
            DialogService,
            span,
            selectedLogEntryId: null,
            TelemetryRepository,
            ErrorRecorder,
            _resources,
            () =>
            {
                var genAISpans = new List<OtlpSpan>();
                var visibleSpanViewModels = GetVisibleSpanViewModels();
                foreach (var vm in visibleSpanViewModels.Where(IsGenAISpan))
                {
                    genAISpans.Add(vm.Span);
                }
                return genAISpans;
            });
    }

    private async Task ExplainTraceAsync()
    {
        await AIContextProvider.LaunchAssistantSidebarAsync(
            promptContext =>
            {
                if (_trace is { } trace)
                {
                    return PromptContextsBuilder.AnalyzeTrace(
                        promptContext,
                        AIPromptsLoc.GetString(nameof(AIPrompts.PromptAnalyzeTrace), OtlpHelpers.ToShortenedId(trace.TraceId)),
                        trace);
                }

                return Task.CompletedTask;
            });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _aiContext?.Dispose();
        foreach (var subscription in _peerChangesSubscriptions)
        {
            subscription.Dispose();
        }
        _tracesSubscription?.Dispose();
        TelemetryContext.Dispose();
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.TraceDetail);
}
