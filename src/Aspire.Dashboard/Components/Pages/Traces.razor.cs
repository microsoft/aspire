// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
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
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.Extensions.Options;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

public partial class Traces : IComponentWithTelemetry, IPageWithSessionAndUrlState<Traces.TracesPageViewModel, Traces.TracesPageState>
{
    private SelectViewModel<ResourceTypeDetails> _allResource = null!;

    private ExplainErrorsButton? _explainErrorsButton;
    private int _totalItemsCount;
    private bool _hasLoaded;
    private List<SelectViewModel<SpanType>> _spanTypes = default!;
    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private Subscription? _resourcesSubscription;
    private Subscription? _tracesSubscription;
    private string _filter = string.Empty;
    private Virtualize<OtlpTrace>? _tracesList;
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

    // Bridges Blazor's Virtualize with the telemetry repository's paged trace data. The repository
    // streams traces lazily (up to the configured max), so the provider is invoked as the user
    // scrolls. Side effects (limit message, total count, AI context) mirror the previous data grid.
    private async ValueTask<ItemsProviderResult<OtlpTrace>> GetVirtualizedData(ItemsProviderRequest request)
    {
        TracesViewModel.StartIndex = request.StartIndex;
        TracesViewModel.Count = request.Count;
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

        _explainErrorsButton?.UpdateHasErrors(TracesViewModel.HasErrors());
        _aiContext?.ContextHasChanged();

        // Refresh the header subtitle and empty state once the count is known. Done via InvokeAsync
        // because the provider runs during Virtualize's render pass.
        if (!_hasLoaded || _totalItemsCount != traces.TotalItemCount)
        {
            _hasLoaded = true;
            _totalItemsCount = traces.TotalItemCount;
            _ = InvokeAsync(StateHasChanged);
        }

        return new ItemsProviderResult<OtlpTrace>(traces.Items, traces.TotalItemCount);
    }

    private Task RefreshDataAsync()
    {
        // Always hop to the renderer's dispatcher: this is invoked both from dispatcher contexts
        // (UI events) and from background continuations (telemetry subscriptions, session-state
        // initialization), and Virtualize.RefreshDataAsync triggers a render that requires it.
        return InvokeAsync(async () =>
        {
            if (_tracesList is not null)
            {
                await _tracesList.RefreshDataAsync();
                StateHasChanged();
            }
        });
    }

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _aiContext = CreateAIContext();

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
        return this.AfterViewModelChangedAsync(null, waitToApplyMobileChange: true);
    }

    private async Task HandleSelectedSpanTypeChangedAsync()
    {
        await this.AfterViewModelChangedAsync(null, waitToApplyMobileChange: true);
        await RefreshDataAsync();
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
                await RefreshDataAsync();
            });
        }
    }

    private async Task OnFilterChangedAsync(string value)
    {
        _filter = value;
        TracesViewModel.FilterText = value;
        await RefreshDataAsync();
    }

    private string GetResourceName(OtlpResource app) => OtlpHelpers.GetResourceName(app, _resources);
    private string GetResourceName(OtlpResourceView app) => OtlpHelpers.GetResourceName(app.Resource, _resources);

    // Fraction (0-100) of the longest trace's duration, used to size the inline duration bar so
    // traces are visually comparable at a glance.
    private double GetDurationPercent(OtlpTrace trace)
    {
        var maxMs = DashboardUIHelpers.SafeConvertToMilliseconds(TracesViewModel.MaxDuration);
        if (maxMs <= 0)
        {
            return 0;
        }

        var ms = DashboardUIHelpers.SafeConvertToMilliseconds(trace.Duration);
        return Math.Clamp((double)ms / maxMs * 100, 0, 100);
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

        await RefreshDataAsync();
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
        await FilterHelpers.OpenFilterAsync(
            entry,
            DialogService,
            DialogService.CreateDialogCallback(this, HandleFilterDialog),
            propertyKeys: TelemetryRepository.GetTracePropertyKeys(PageViewModel.SelectedResource.Id?.GetResourceKey()),
            knownKeys: KnownTraceFields.AllFields,
            getFieldValues: TelemetryRepository.GetTraceFieldValues,
            FilterLoc);
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

        await this.AfterViewModelChangedAsync(null, waitToApplyMobileChange: false);
        await RefreshDataAsync();
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
        return FilterHelpers.GetFilterMenuItems(
            TracesViewModel.Filters,
            clearFilters: TracesViewModel.ClearFilters,
            openFilterAsync: OpenFilterAsync,
            afterChangeAsync: async () =>
            {
                await this.AfterViewModelChangedAsync(null, waitToApplyMobileChange: false);
                await RefreshDataAsync();
            },
            filterLoc: FilterLoc,
            dialogsLoc: DialogsLoc);
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
