// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Controls.Grid;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Pages;

public partial class Metrics : IDisposable, IComponentWithTelemetry, IPageWithSessionAndUrlState<Metrics.MetricsViewModel, Metrics.MetricsPageState>
{
    private static readonly EnumerableGridSort<OtlpInstrumentSummary> s_instrumentDescriptionSort = EnumerableGridSort<OtlpInstrumentSummary>.ByAscending(item => item.Description);
    private SelectViewModel<ResourceTypeDetails> _selectResource = null!;
    private List<SelectViewModel<TimeSpan>> _durations = null!;
    private static readonly TimeSpan s_defaultDuration = TimeSpan.FromMinutes(5);
    private AspirePageContentLayout? _contentLayout;
    private TreeMetricSelector? _treeMetricSelector;
    private readonly string _selectDurationId = $"select-duration-{Guid.NewGuid():N}";

    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private List<SelectViewModel<ResourceTypeDetails>>? _selectionResourceViewModels;
    private Subscription? _resourcesSubscription;
    private Subscription? _metricsSubscription;

    public string BasePath => DashboardUrls.MetricsBasePath;
    public string SessionStorageKey => BrowserStorageKeys.MetricsPageState;
    public MetricsViewModel PageViewModel { get; set; } = null!;

    [Parameter]
    public string? ResourceName { get; set; }

    /// <summary>
    /// Gets or sets the resources selected in the resource list when several resources are selected. The page
    /// displays one of them at a time, and lets the user switch between them.
    /// </summary>
    [Parameter]
    [SupplyParameterFromQuery(Name = DashboardUrls.ResourceSelectionQueryName)]
    public string[]? SelectedResourceNames { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "meter")]
    public string? MeterName { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "instrument")]
    public string? InstrumentName { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "duration")]
    public int? DurationMinutes { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "view")]
    public string? ViewKindName { get; set; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    [Inject]
    public required ITelemetryRepositoryWriter TelemetryRepositoryWriter { get; init; }

    [Inject]
    public required ILogger<Metrics> Logger { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    /// <summary>
    /// The resources layout hosting this page. When present, the layout's resource pane selects the resource
    /// and displays resource commands, so the page doesn't display its own resource selector.
    /// </summary>
    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);

        _durations = new List<SelectViewModel<TimeSpan>>
        {
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastOneMinute)], Id = TimeSpan.FromMinutes(1) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastFiveMinutes)], Id = TimeSpan.FromMinutes(5) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastFifteenMinutes)], Id = TimeSpan.FromMinutes(15) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastThirtyMinutes)], Id = TimeSpan.FromMinutes(30) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastHour)], Id = TimeSpan.FromHours(1) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastThreeHours)], Id = TimeSpan.FromHours(3) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastSixHours)], Id = TimeSpan.FromHours(6) },
            new() { Name = Loc[nameof(Dashboard.Resources.Metrics.MetricsLastTwelveHours)], Id = TimeSpan.FromHours(12) },
        };

        _selectResource = new SelectViewModel<ResourceTypeDetails>
        {
            Id = null,
            Name = ControlsStringsLoc[nameof(ControlsStrings.LabelNone)]
        };

        PageViewModel = new MetricsViewModel
        {
            SelectedResource = _selectResource,
            SelectedDuration = _durations.Single(d => d.Id == s_defaultDuration),
            SelectedViewKind = null
        };

        UpdateResources();
        if (ResourcesLayout is { } layout)
        {
            layout.ResourcesChanged += OnLayoutResourcesChanged;
        }
        _resourcesSubscription = TelemetryRepository.OnNewResources(() => InvokeAsync(() =>
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

        UpdateSubscription();
        UpdateTelemetryProperties();
    }

    public MetricsPageState ConvertViewModelToSerializable()
    {
        return new MetricsPageState
        {
            ResourceName = PageViewModel.SelectedResource.Id is not null ? PageViewModel.SelectedResource.Name : null,
            MeterName = PageViewModel.SelectedMeter,
            InstrumentName = PageViewModel.SelectedInstrument?.Name,
            DurationMinutes = (int)PageViewModel.SelectedDuration.Id.TotalMinutes,
            ViewKind = PageViewModel.SelectedViewKind?.ToString()
        };
    }

    public Task UpdateViewModelFromQueryAsync(MetricsViewModel viewModel)
    {
        UpdateSelectionResources();
        if (_selectionResourceViewModels is { Count: > 0 } selectionResources &&
            (ResourceName is null || !selectionResources.Any(r => string.Equals(r.Name, ResourceName, StringComparisons.ResourceName))))
        {
            // Several resources are selected in the resource list. Metrics are displayed for one resource at a time,
            // so display the first selected resource until the user picks another one.
            PageViewModel.SelectedResource = selectionResources[0];
            ResourceName = selectionResources[0].Name;
            return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
        }

        if (ResourcesLayout is null && ResourceName is null && TryGetSingleResource() is { } r)
        {
            // If there is no resource selected and there is only one resource available, select it.
            PageViewModel.SelectedResource = r;
            ResourceName = r.Name;
            return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
        }

        viewModel.SelectedDuration = _durations.SingleOrDefault(d => (int)d.Id.TotalMinutes == DurationMinutes) ?? _durations.Single(d => d.Id == s_defaultDuration);
        viewModel.SelectedResource = _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: true, _selectResource);

        UpdateInstruments(viewModel);

        viewModel.SelectedMeter = null;
        viewModel.SelectedInstrument = null;
        viewModel.SelectedViewKind = Enum.TryParse(typeof(MetricViewKind), ViewKindName, out var view) && view is MetricViewKind vk ? vk : null;

        if (viewModel.Instruments != null && !string.IsNullOrEmpty(MeterName))
        {
            viewModel.SelectedMeter = viewModel.Instruments.FirstOrDefault(i => i.Parent.Name == MeterName)?.Parent.Name;
            if (viewModel.SelectedMeter != null && !string.IsNullOrEmpty(InstrumentName))
            {
                viewModel.SelectedInstrument = viewModel.Instruments.FirstOrDefault(i => i.Parent.Name == MeterName && i.Name == InstrumentName);
            }
        }
        else
        {
            SelectDefaultInstrument(viewModel);
        }
        return Task.CompletedTask;

        SelectViewModel<ResourceTypeDetails>? TryGetSingleResource()
        {
            var apps = _resourceViewModels.Where(e => e != _selectResource).ToList();
            return apps.Count == 1 ? apps[0] : null;
        }
    }

    /// <summary>
    /// Selects an instrument when nothing is selected, so the page opens on a chart instead of an empty pane.
    /// Instruments that describe request traffic and latency come first because they're the most useful signals
    /// for most apps, followed by connection, CPU and memory instruments. Otherwise the first instrument is used.
    /// </summary>
    private static void SelectDefaultInstrument(MetricsViewModel viewModel)
    {
        if (viewModel.SelectedMeter is not null || viewModel.SelectedInstrument is not null || viewModel.Instruments is not { Count: > 0 } instruments)
        {
            return;
        }

        var instrument = s_preferredInstrumentNames
            .Select(name => instruments.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal)))
            .FirstOrDefault(i => i is not null)
            ?? instruments.OrderBy(i => i.Parent.Name, StringComparer.Ordinal).ThenBy(i => i.Name, StringComparer.Ordinal).First();

        viewModel.SelectedMeter = instrument.Parent.Name;
        viewModel.SelectedInstrument = instrument;
    }

    // Instrument names follow the OpenTelemetry semantic conventions:
    // https://opentelemetry.io/docs/specs/semconv/http/http-metrics/
    // https://opentelemetry.io/docs/specs/semconv/database/database-metrics/
    // https://opentelemetry.io/docs/specs/semconv/runtime/dotnet-metrics/
    private static readonly string[] s_preferredInstrumentNames =
    [
        "http.server.request.duration",
        "http.client.request.duration",
        "db.client.operation.duration",
        "db.client.commands.duration",
        "rpc.server.duration",
        "messaging.process.duration",
        "kestrel.active_connections",
        "http.server.active_requests",
        "dotnet.process.cpu.time",
        "process.cpu.time",
        "dotnet.gc.heap.total_allocated",
        "process.runtime.dotnet.gc.allocations.size",
    ];

    private void UpdateInstruments(MetricsViewModel viewModel)
    {
        var selectedInstance = viewModel.SelectedResource.Id?.GetResourceKey();
        viewModel.Instruments = selectedInstance != null ? TelemetryRepository.GetInstrumentSummaries(selectedInstance.Value) : null;
    }

    /// <summary>
    /// Updates the resources the user can pick from when several resources are selected in the resource list.
    /// </summary>
    private void UpdateSelectionResources()
    {
        if (SelectedResourceNames is not { Length: > 1 } || ResourcesLayout?.GetSelectionTelemetryKeys(SelectedResourceNames) is not { } selectionKeys)
        {
            _selectionResourceViewModels = null;
            return;
        }

        // Groupings of replicas don't have a resource key. The resource list selects replicas individually.
        _selectionResourceViewModels = _resourceViewModels
            .Where(r => r.Id is { ReplicaSetName: not null } id && selectionKeys.Contains(id.GetResourceKey()))
            .ToList();
    }

    private void UpdateResources()
    {
        _resources = TelemetryRepository.GetResources();
        if (ResourcesLayout is { } layout)
        {
            var paneKeys = layout.GetPaneTelemetryKeys();
            _resources = _resources.Where(resource => paneKeys.Contains(resource.ResourceKey)).ToList();
        }
        _resourceViewModels = ResourcesSelectHelpers.CreateResources(_resources);

        if (_resourceViewModels.Count != 1)
        {
            _resourceViewModels.Insert(0, _selectResource);
        }
        else
        {
            PageViewModel.SelectedResource = _resourceViewModels.Single();
        }

        UpdateSelectionResources();

        UpdateSubscription();
    }

    private void OnLayoutResourcesChanged()
    {
        UpdateResources();
        StateHasChanged();
    }

    private async Task HandleSelectedResourceChangedAsync()
    {
        UpdateInstruments(PageViewModel);

        // The new resource might not have the currently selected meter/instrument.
        // Check whether the new resource has the current values or not, and clear if they're not available.
        if (PageViewModel.SelectedMeter != null ||
            PageViewModel.SelectedInstrument != null)
        {
            if (PageViewModel.Instruments == null || ShouldClearSelectedMetrics(PageViewModel.Instruments))
            {
                PageViewModel.SelectedMeter = null;
                PageViewModel.SelectedInstrument = null;
            }
        }

        SelectDefaultInstrument(PageViewModel);

        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);

        // The mobile view doesn't update the URL when the resource changes.
        // Because of this, the page doesn't autoamtically use updated instruments.
        // Force the metrics tree to update so it re-renders with the new resource's instruments.
        _treeMetricSelector?.OnResourceChanged();
    }

    private bool ShouldClearSelectedMetrics(List<OtlpInstrumentSummary> instruments)
    {
        if (PageViewModel.SelectedMeter != null && !instruments.Any(i => i.Parent.Name == PageViewModel.SelectedMeter))
        {
            return true;
        }
        if (PageViewModel.SelectedInstrument != null && !instruments.Any(i => i.Name == PageViewModel.SelectedInstrument.Name))
        {
            return true;
        }

        return false;
    }

    private Task ClearMetrics(ResourceKey? key)
    {
        DataSource.EnsureWritable();
        return TelemetryRepositoryWriter.ClearMetricsAsync(key);
    }

    private Task HandleSelectedDurationChangedAsync()
    {
        return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);
    }

    private string? PauseText => PauseManager.AreMetricsPaused(out var startTime)
        ? string.Format(
            CultureInfo.CurrentCulture,
            Loc[nameof(Dashboard.Resources.Metrics.PauseInProgressText)],
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, startTime.Value))
        : null;

    public sealed class MetricsViewModel
    {
        public FluentTreeItem? SelectedTreeItem { get; set; }
        public string? SelectedMeter { get; set; }
        public OtlpInstrumentSummary? SelectedInstrument { get; set; }
        public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
        public SelectViewModel<TimeSpan> SelectedDuration { get; set; } = null!;
        public List<OtlpInstrumentSummary>? Instruments { get; set; }
        public required MetricViewKind? SelectedViewKind { get; set; }
    }

    public class MetricsPageState
    {
        public string? ResourceName { get; set; }
        public string? MeterName { get; set; }
        public string? InstrumentName { get; set; }
        public int DurationMinutes { get; set; }
        public required string? ViewKind { get; set; }
    }

    public enum MetricViewKind
    {
        Table,
        Graph
    }

    private Task HandleSelectedTreeItemChangedAsync()
    {
        if (PageViewModel.SelectedTreeItem?.Data is string meter)
        {
            PageViewModel.SelectedMeter = meter;
            PageViewModel.SelectedInstrument = null;
        }
        else if (PageViewModel.SelectedTreeItem?.Data is OtlpInstrumentSummary instrument)
        {
            PageViewModel.SelectedMeter = instrument.Parent.Name;
            PageViewModel.SelectedInstrument = instrument;
        }
        else
        {
            PageViewModel.SelectedMeter = null;
            PageViewModel.SelectedInstrument = null;
        }

        return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    public string GetUrlFromSerializableViewModel(MetricsPageState serializable)
    {
        // In the resources layout, the resource list owns the selection, so a resource remembered in the session
        // doesn't replace it. With several selected resources, the page displays the resource the user picked.
        var resourceName = ResourcesLayout is { IsMultiSelection: false, SelectedResourceName: { } selectedName } ? selectedName : serializable.ResourceName;
        var url = DashboardUrls.MetricsUrl(
            resource: resourceName,
            meter: serializable.MeterName,
            instrument: serializable.InstrumentName,
            duration: serializable.DurationMinutes,
            view: serializable.ViewKind);

        return ResourcesLayout?.AddSelectionToUrl(url) ?? url;
    }

    private async Task OnViewChangedAsync(MetricViewKind newView)
    {
        PageViewModel.SelectedViewKind = newView;
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private void UpdateSubscription()
    {
        var selectedResourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();

        // Subscribe to updates.
        if (_metricsSubscription is null || _metricsSubscription.ResourceKey != selectedResourceKey)
        {
            _metricsSubscription?.Dispose();
            _metricsSubscription = TelemetryRepository.OnNewMetrics(selectedResourceKey, SubscriptionType.Read, async () =>
            {
                if (selectedResourceKey != null)
                {
                    // If there are more instruments than before then update the UI.
                    var instruments = TelemetryRepository.GetInstrumentSummaries(selectedResourceKey.Value);

                    if (PageViewModel.Instruments is null || instruments.Count != PageViewModel.Instruments.Count)
                    {
                        PageViewModel.Instruments = instruments;

                        // Instruments can arrive after the page loads, e.g. when the resource has just started.
                        SelectDefaultInstrument(PageViewModel);
                        await InvokeAsync(StateHasChanged);
                    }
                }
            });
        }
    }

    public void Dispose()
    {
        if (ResourcesLayout is { } layout)
        {
            layout.ResourcesChanged -= OnLayoutResourcesChanged;
        }
        _resourcesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        TelemetryContext.Dispose();
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.Metrics);

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsResourceIsReplica, new AspireTelemetryProperty(PageViewModel.SelectedResource.Id?.ReplicaSetName is not null)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsInstrumentsCount, new AspireTelemetryProperty((PageViewModel.Instruments?.Count ?? -1).ToString(CultureInfo.InvariantCulture), AspireTelemetryPropertyType.Metric)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsSelectedDuration, new AspireTelemetryProperty(PageViewModel.SelectedDuration.Id.ToString(), AspireTelemetryPropertyType.UserSetting)),
            new ComponentTelemetryProperty(TelemetryPropertyKeys.MetricsSelectedView, new AspireTelemetryProperty(PageViewModel.SelectedViewKind?.ToString() ?? string.Empty, AspireTelemetryPropertyType.UserSetting))
        ], Logger);
    }
}
