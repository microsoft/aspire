// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
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
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Pages;

public partial class Metrics : IAsyncDisposable, IComponentWithTelemetry, IPageWithSessionAndUrlState<Metrics.MetricsViewModel, Metrics.MetricsPageState>
{
    private SelectViewModel<ResourceTypeDetails> _selectResource = null!;
    private List<SelectViewModel<TimeSpan>> _durations = null!;
    private static readonly TimeSpan s_defaultDuration = TimeSpan.FromMinutes(5);
    private AspirePageContentLayout? _contentLayout;
    private MetricGroupList? _metricGroupList;
    private ElementReference _metricsSplitterElement;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<Metrics>? _selfRef;
    private string? _registeredMetricsSplitterElementId;
    private string _metricGroupsPaneSize = "20%";
    private string _metricsContentPaneSize = "80%";
    private readonly string _selectDurationId = $"select-duration-{Guid.NewGuid():N}";

    private List<OtlpResource> _resources = default!;
    private List<SelectViewModel<ResourceTypeDetails>> _resourceViewModels = default!;
    private List<SelectViewModel<ResourceTypeDetails>>? _selectionResourceViewModels;
    private List<OtlpResource>? _aggregateResources;
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
    [SupplyParameterFromQuery(Name = "pinned")]
    public bool? Pinned { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "all")]
    public bool? ShowAll { get; set; }

    [Parameter]
    [SupplyParameterFromQuery(Name = "total")]
    public bool? ShowRunningTotal { get; set; }

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

    [Inject]
    public required MetricPinStore PinStore { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    /// <summary>
    /// The resources layout hosting this page. When present, the layout's resource pane selects the resource
    /// and displays resource commands, so the page doesn't display its own resource selector.
    /// </summary>
    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!ViewportInformation.IsDesktop || PageViewModel.Instruments is not { Count: > 0 } ||
            _registeredMetricsSplitterElementId == _metricsSplitterElement.Id)
        {
            return;
        }

        _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"/{Assets["Components/Pages/Metrics.razor.js"]}");
        _selfRef ??= DotNetObjectReference.Create(this);
        if (await _jsModule.InvokeAsync<bool>("registerMetricsSplitter", _metricsSplitterElement, _selfRef))
        {
            // Selecting another resource can replace the splitter element while the Metrics component remains alive.
            _registeredMetricsSplitterElementId = _metricsSplitterElement.Id;
        }
    }

    private void HandleMetricsSplitterResize(FluentMultiSplitterResizeEventArgs args)
    {
        if (args.PaneIndex == 0)
        {
            SetMetricGroupsPaneSize(args.NewSize);
        }
    }

    [JSInvokable]
    public Task SetMetricGroupsPaneSizeAsync(double percent)
    {
        SetMetricGroupsPaneSize(percent);
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void SetMetricGroupsPaneSize(double percent)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        _metricGroupsPaneSize = string.Create(CultureInfo.InvariantCulture, $"{clampedPercent}%");
        _metricsContentPaneSize = string.Create(CultureInfo.InvariantCulture, $"{100 - clampedPercent}%");
    }

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
        _resourcesSubscription = TelemetryRepository.OnNewResources(() => InvokeAsync(OnResourcesUpdatedAsync));

        // The pinned group and its cards follow pins changed from a card or a chart header.
        PinStore.Changed += OnPinsChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (await this.InitializeViewModelAsync())
        {
            return;
        }

        if (!_organizeStateLoaded)
        {
            _organizeStateLoaded = true;
            await LoadOrganizeStateAsync();
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
            Pinned = PageViewModel.ShowPinned,
            All = PageViewModel.ShowAll,
            ShowRunningTotal = PageViewModel.ShowRunningTotal,
            DurationMinutes = (int)PageViewModel.SelectedDuration.Id.TotalMinutes,
            ViewKind = PageViewModel.SelectedViewKind?.ToString()
        };
    }

    public Task UpdateViewModelFromQueryAsync(MetricsViewModel viewModel)
    {
        UpdateSelectionResources();
        if (_selectionResourceViewModels is { Count: > 0 } selectionResources &&
            ResourceName is not null && !selectionResources.Any(r => string.Equals(r.Name, ResourceName, StringComparisons.ResourceName)))
        {
            // The URL names a resource that isn't part of the multi-resource selection. Display the first selected
            // resource instead. Without a named resource, the page lists the instruments of every selected resource.
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

        // Where an instrument is opened from, so its chart can go back there. Captured before the
        // URL replaces the selection.
        var openedFrom = viewModel.SelectedInstrument is null ? GetCurrentGroup(viewModel) : viewModel.ReturnTarget;
        var showedFilterResults = viewModel.ShowFilterResults;

        viewModel.SelectedMeter = null;
        viewModel.SelectedInstrument = null;
        viewModel.ShowPinned = false;
        viewModel.ShowAll = false;
        viewModel.SelectedViewKind = Enum.TryParse(typeof(MetricViewKind), ViewKindName, out var view) && view is MetricViewKind vk ? vk : null;
        viewModel.ShowRunningTotal = ShowRunningTotal is true;

        if (viewModel.Instruments != null && Pinned is true)
        {
            viewModel.ShowPinned = true;
        }
        else if (viewModel.Instruments != null && ShowAll is true)
        {
            viewModel.ShowAll = true;
        }
        else if (viewModel.Instruments != null && !string.IsNullOrEmpty(MeterName))
        {
            viewModel.SelectedMeter = viewModel.Instruments.FirstOrDefault(i => i.Parent.Name == MeterName)?.Parent.Name;
            if (viewModel.SelectedMeter != null && !string.IsNullOrEmpty(InstrumentName))
            {
                viewModel.SelectedInstrument = viewModel.Instruments.FirstOrDefault(i => i.Parent.Name == MeterName && i.Name == InstrumentName);
            }
        }
        else
        {
            SelectDefaultView(viewModel);
        }

        // Search results aren't part of the URL. Keep showing them across navigations that don't open an
        // instrument, e.g. going back to them from an instrument's chart. Opening an instrument hides them.
        viewModel.ShowFilterResults = showedFilterResults && viewModel.SelectedInstrument is null && viewModel.TextFilter.Trim().Length > 0;
        viewModel.ReturnTarget = viewModel.SelectedInstrument is { } instrument
            ? openedFrom ?? new MetricReturnTarget(MetricGroupKind.Meter, instrument.Parent.Name)
            : null;

        return Task.CompletedTask;

        SelectViewModel<ResourceTypeDetails>? TryGetSingleResource()
        {
            var apps = _resourceViewModels.Where(e => e != _selectResource).ToList();
            return apps.Count == 1 ? apps[0] : null;
        }
    }

    /// <summary>
    /// Selects what to show when nothing is selected, so the page doesn't open on an empty pane. The pinned group
    /// comes first when this resource reports pinned instruments, because the user chose them as the ones to watch.
    /// Otherwise every instrument is shown, grouped by meter, so entering the page doesn't arbitrarily prioritize
    /// one signal over the rest.
    /// </summary>
    private void SelectDefaultView(MetricsViewModel viewModel)
    {
        if (viewModel.SelectedMeter is not null || viewModel.SelectedInstrument is not null || viewModel.ShowPinned || viewModel.ShowAll || viewModel.Instruments is not { Count: > 0 } instruments)
        {
            return;
        }

        if (PinStore.GetPinnedInstruments(instruments).Count > 0)
        {
            viewModel.ShowPinned = true;
            return;
        }

        viewModel.ShowAll = true;
    }

    private void UpdateInstruments(MetricsViewModel viewModel)
    {
        if (viewModel.SelectedResource.Id is null && _aggregateResources is not null)
        {
            viewModel.Instruments = GetAggregateInstruments();
            return;
        }

        var selectedInstance = viewModel.SelectedResource.Id?.GetResourceKey();
        if (selectedInstance is null &&
            ResourceName is not null &&
            ResourcesLayout is { } layout &&
            ResourceViewModel.TryGetResourceByName(ResourceName, layout.ResourceByName, out _))
        {
            // A resource from the app model that hasn't sent telemetry has no metrics, rather than being unknown.
            viewModel.Instruments = [];
            return;
        }

        viewModel.Instruments = selectedInstance != null ? TelemetryRepository.GetInstrumentSummaries(selectedInstance.Value) : null;
    }

    /// <summary>
    /// Gets whether the page lists the instruments of every selected resource, rather than of one resource.
    /// That is the case for all resources and for a multi-resource selection. Each instrument keeps its source
    /// resource, so the same instrument from two resources is two entries.
    /// </summary>
    private bool IsAggregate => PageViewModel.SelectedResource.Id is null && _aggregateResources is not null;

    private List<OtlpInstrumentSummary> GetAggregateInstruments() =>
        [.. (_aggregateResources ?? []).SelectMany(resource => TelemetryRepository.GetInstrumentSummaries(resource.ResourceKey))];

    private void UpdateAggregateResources()
    {
        _aggregateResources = ResourcesLayout?.GetSelectionTelemetryKeys(SelectedResourceNames) is { } keys
            ? _resources.Where(resource => keys.Contains(resource.ResourceKey)).ToList()
            : null;
    }

    private string GetInstrumentResourceLabel(OtlpInstrumentSummary instrument) =>
        OtlpHelpers.GetResourceName(instrument.ResourceView.Resource, _resources);

    private readonly HashSet<string> _collapsedSections = new(StringComparer.OrdinalIgnoreCase);
    private bool _organizeStateLoaded;

    private void ToggleSection(string key)
    {
        if (!_collapsedSections.Remove(key))
        {
            _collapsedSections.Add(key);
        }
    }

    /// <summary>
    /// The most instrument cards a list renders. Larger lists show a notice asking to filter.
    /// </summary>
    internal const int MaxRenderedInstruments = 200;

    // Instruments are grouped by meter unless the user picks another grouping, which matches the meters listed in the
    // groups pane. None lists every instrument in one list, in the chosen sort order.
    private MetricGroupBy _groupBy = MetricGroupBy.Meter;
    private MetricSortBy _sortBy = MetricSortBy.Instrument;

    /// <summary>
    /// Gets whether the page lists instruments, for one resource or several, so they can be grouped and sorted.
    /// </summary>
    private bool CanOrganize => PageViewModel.SelectedResource.Id is not null || IsAggregate;

    // Grouping or sorting by resource only means something across several resources. The stored choice is kept so
    // that it applies again when the page goes back to several resources.
    private MetricGroupBy EffectiveGroupBy => !IsAggregate && _groupBy == MetricGroupBy.Resource ? MetricGroupBy.None : _groupBy;

    private MetricSortBy EffectiveSortBy => !IsAggregate && _sortBy == MetricSortBy.Resource ? MetricSortBy.Instrument : _sortBy;

    private sealed record OrganizeState(MetricGroupBy GroupBy, MetricSortBy SortBy);

    private async Task LoadOrganizeStateAsync()
    {
        var stored = await SessionStorage.GetAsync<OrganizeState>(BrowserStorageKeys.MetricsOrganize);
        if (stored.Success && stored.Value is { } state)
        {
            _groupBy = Enum.IsDefined(state.GroupBy) ? state.GroupBy : MetricGroupBy.Meter;
            _sortBy = Enum.IsDefined(state.SortBy) ? state.SortBy : MetricSortBy.Instrument;
        }
    }

    private async Task SetGroupByAsync(MetricGroupBy groupBy)
    {
        _groupBy = groupBy;

        // The organize menus run this from their own click handler, which only re-renders the menu.
        StateHasChanged();
        await SessionStorage.SetAsync(BrowserStorageKeys.MetricsOrganize, new OrganizeState(_groupBy, _sortBy));
    }

    private async Task SetSortByAsync(MetricSortBy sortBy)
    {
        _sortBy = sortBy;

        // The organize menus run this from their own click handler, which only re-renders the menu.
        StateHasChanged();
        await SessionStorage.SetAsync(BrowserStorageKeys.MetricsOrganize, new OrganizeState(_groupBy, _sortBy));
    }

    private string GetGroupByName(MetricGroupBy value) => Loc[value switch
    {
        MetricGroupBy.Resource => nameof(Dashboard.Resources.Metrics.MetricsOrganizeResource),
        MetricGroupBy.Meter => nameof(Dashboard.Resources.Metrics.MetricsOrganizeMeter),
        MetricGroupBy.Instrument => nameof(Dashboard.Resources.Metrics.MetricsOrganizeInstrument),
        _ => nameof(Dashboard.Resources.Metrics.MetricsGroupNone)
    }];

    private string GetSortByName(MetricSortBy value) => Loc[value switch
    {
        MetricSortBy.Resource => nameof(Dashboard.Resources.Metrics.MetricsOrganizeResource),
        MetricSortBy.ValueDescending => nameof(Dashboard.Resources.Metrics.MetricsSortValueDescending),
        MetricSortBy.ValueAscending => nameof(Dashboard.Resources.Metrics.MetricsSortValueAscending),
        _ => nameof(Dashboard.Resources.Metrics.MetricsSortName)
    }];

    private string GetGroupByTooltip() => Loc[nameof(Dashboard.Resources.Metrics.MetricsGroupByTooltip)];

    private string GetSortByTooltip() => Loc[nameof(Dashboard.Resources.Metrics.MetricsSortByTooltip)];

    private IList<MenuButtonItem> GetGroupByMenuItems() =>
        new[]
        {
            new MenuButtonItem
            {
                IsHeader = true,
                Text = Loc[nameof(Dashboard.Resources.Metrics.MetricsGroupMenuTitle)],
                Icon = new Icons.Regular.Size20.GroupList(),
                RenderKey = "group-header"
            }
        }
        .Concat(new[] { MetricGroupBy.None, MetricGroupBy.Resource, MetricGroupBy.Meter, MetricGroupBy.Instrument }
            .Where(value => IsAggregate || value != MetricGroupBy.Resource)
            .Select(value => new MenuButtonItem
            {
                Class = "metrics-organize-menu-item",
                Text = GetGroupByName(value),
                Role = MenuItemRole.Radio,
                Checked = value == EffectiveGroupBy,
                OnClick = () => SetGroupByAsync(value),
                RenderKey = value
            }))
        .ToList();

    private IList<MenuButtonItem> GetSortByMenuItems() =>
        new[]
        {
            new MenuButtonItem
            {
                IsHeader = true,
                Text = Loc[nameof(Dashboard.Resources.Metrics.MetricsSortMenuTitle)],
                Icon = new Icons.Regular.Size16.ArrowSort(),
                RenderKey = "sort-header"
            }
        }
        .Concat(new[] { MetricSortBy.Instrument, MetricSortBy.Resource, MetricSortBy.ValueDescending, MetricSortBy.ValueAscending }
            .Where(value => IsAggregate || value != MetricSortBy.Resource)
            .Select(value => new MenuButtonItem
            {
                Class = "metrics-organize-menu-item",
                Text = GetSortByName(value),
                Role = MenuItemRole.Radio,
                Checked = value == EffectiveSortBy,
                OnClick = () => SetSortByAsync(value),
                RenderKey = value
            }))
        .ToList();

    /// <summary>
    /// Splits instruments into titled sections for the selected grouping. None returns a single untitled section.
    /// </summary>
    /// <param name="instruments">The instruments to show.</param>
    /// <param name="isSingleMeter">Whether the list shows one meter, whose name is already the page's title, so grouping by meter would only repeat it.</param>
    private List<IGrouping<string, OtlpInstrumentSummary>> GetSections(IEnumerable<OtlpInstrumentSummary> instruments, bool isSingleMeter)
    {
        var groupBy = EffectiveGroupBy;
        if (groupBy == MetricGroupBy.Meter && isSingleMeter)
        {
            groupBy = MetricGroupBy.None;
        }

        Func<OtlpInstrumentSummary, string> keySelector = groupBy switch
        {
            MetricGroupBy.Resource => GetInstrumentResourceLabel,
            MetricGroupBy.Meter => i => i.Parent.Name,
            MetricGroupBy.Instrument => i => i.Name,
            _ => _ => string.Empty
        };

        return [.. instruments.GroupBy(keySelector).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)];
    }

    private MetricSortBy? OverviewSortBy => CanOrganize ? EffectiveSortBy : null;

    private string? CurrentResourceName => PageViewModel.SelectedResource.Id is null ? null : PageViewModel.SelectedResource.Name;

    private ResourceKey? OverviewResourceKey => PageViewModel.SelectedResource.Id?.GetResourceKey();

    private Func<OtlpInstrumentSummary, string>? ResourceLabelProvider => IsAggregate ? GetInstrumentResourceLabel : null;

    /// <summary>
    /// Updates the resources the user can pick from when several resources are selected in the resource list.
    /// </summary>
    private void UpdateSelectionResources()
    {
        UpdateAggregateResources();
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
        _ = InvokeAsync(OnResourcesUpdatedAsync);
    }

    private async Task OnResourcesUpdatedAsync()
    {
        UpdateResources();

        // The route's resource can be unknown when the page first resolves it. With the resource pane, the pane's
        // resources arrive asynchronously from the resource service after a direct page load, and without it a
        // resource's telemetry can arrive after the page opened. The route then resolved to "None" and, because
        // nothing resolved it again, the page kept showing "Select a resource" for a valid deep link. Resolve the
        // route again once its resource is known, which also restores the meter and instrument it names.
        if (ResourceName is not null &&
            PageViewModel.SelectedResource == _selectResource &&
            _resourceViewModels.GetResource(Logger, ResourceName, canSelectGrouping: true, _selectResource) != _selectResource)
        {
            await UpdateViewModelFromQueryAsync(PageViewModel);
            UpdateSubscription();
            UpdateTelemetryProperties();
        }
        else if (IsAggregate)
        {
            // Resources joining or leaving the pane change which instruments are listed.
            UpdateInstruments(PageViewModel);
            SelectDefaultView(PageViewModel);
        }

        StateHasChanged();
    }

    internal async Task HandleSelectedResourceChangedAsync()
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

        SelectDefaultView(PageViewModel);

        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: true);

        // The mobile view doesn't update the URL when the resource changes.
        // Because of this, the page doesn't autoamtically use updated instruments.
        // Force the group list to update so it re-renders with the new resource's instruments.
        _metricGroupList?.OnResourceChanged();
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
        public string? SelectedMeter { get; set; }

        /// <summary>
        /// Gets or sets whether the pinned group is selected. It shows pinned instruments from every meter.
        /// </summary>
        public bool ShowPinned { get; set; }

        /// <summary>
        /// Gets or sets whether every instrument is shown, grouped by meter.
        /// </summary>
        public bool ShowAll { get; set; }

        /// <summary>
        /// Gets or sets where the selected instrument was opened from, so its chart can navigate back there.
        /// </summary>
        public MetricReturnTarget? ReturnTarget { get; set; }

        /// <summary>
        /// Gets or sets the text that filters the metric groups and the cards of the selected group.
        /// </summary>
        public string TextFilter { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets whether the content shows the filter's matches from every meter instead of the selection.
        /// Set while the user types, and cleared when they pick a group or open an instrument.
        /// </summary>
        public bool ShowFilterResults { get; set; }

        /// <summary>
        /// Gets or sets whether a counter's chart shows its running total instead of its change per interval.
        /// </summary>
        public bool ShowRunningTotal { get; set; }
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
        public bool Pinned { get; set; }
        public bool All { get; set; }
        public bool ShowRunningTotal { get; set; }
        public int DurationMinutes { get; set; }
        public required string? ViewKind { get; set; }
    }

    public enum MetricViewKind
    {
        Table,
        Graph
    }

    /// <summary>
    /// A set of instruments shown as cards: one meter's, the pinned ones, all of them, or the filter's matches.
    /// </summary>
    public enum MetricGroupKind
    {
        Meter,
        Pinned,
        All,
        FilterResults
    }

    public sealed record MetricReturnTarget(MetricGroupKind Kind, string? MeterName);

    private static MetricReturnTarget? GetCurrentGroup(MetricsViewModel viewModel)
    {
        if (viewModel.ShowFilterResults)
        {
            return new MetricReturnTarget(MetricGroupKind.FilterResults, viewModel.SelectedMeter);
        }
        if (viewModel.ShowPinned)
        {
            return new MetricReturnTarget(MetricGroupKind.Pinned, null);
        }
        if (viewModel.ShowAll)
        {
            return new MetricReturnTarget(MetricGroupKind.All, null);
        }

        return viewModel.SelectedMeter is { } meterName ? new MetricReturnTarget(MetricGroupKind.Meter, meterName) : null;
    }

    private Task SelectGroupAsync(MetricGroupKind kind, string? meterName)
    {
        // Picking a group narrows the filter's matches to that group. Search results come back only through
        // the chart's back button, which returns to where the instrument was opened from.
        PageViewModel.ShowFilterResults = kind == MetricGroupKind.FilterResults;
        PageViewModel.ShowPinned = kind == MetricGroupKind.Pinned;
        PageViewModel.ShowAll = kind == MetricGroupKind.All;
        PageViewModel.SelectedMeter = kind is MetricGroupKind.Meter or MetricGroupKind.FilterResults ? meterName : null;
        PageViewModel.SelectedInstrument = null;

        return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private Task NavigateBackAsync()
    {
        var target = PageViewModel.ReturnTarget ?? new MetricReturnTarget(MetricGroupKind.Meter, PageViewModel.SelectedMeter);
        return SelectGroupAsync(target.Kind, target.MeterName);
    }

    private string GetBackLabel()
    {
        var target = PageViewModel.ReturnTarget ?? new MetricReturnTarget(MetricGroupKind.Meter, PageViewModel.SelectedMeter);
        var name = target.Kind switch
        {
            MetricGroupKind.Pinned => Loc[nameof(Dashboard.Resources.Metrics.MetricsPinnedHeader)].Value,
            MetricGroupKind.All => Loc[nameof(Dashboard.Resources.Metrics.MetricsAllInstrumentsTitle)].Value,
            MetricGroupKind.FilterResults => string.Format(CultureInfo.CurrentCulture, Loc[nameof(Dashboard.Resources.Metrics.MetricsFilterResultsTitle)], PageViewModel.TextFilter.Trim()),
            _ => target.MeterName ?? string.Empty
        };

        return string.Format(CultureInfo.CurrentCulture, Loc[nameof(Dashboard.Resources.Metrics.MetricsBackTo)], name);
    }

    public string GetUrlFromSerializableViewModel(MetricsPageState serializable)
    {
        // In the resources layout, the resource list owns the selection, so a resource remembered in the session
        // doesn't replace it. Without a single selected resource (all resources, or several), no resource is named
        // and the page lists the instruments of every selected resource. A remembered name could also be stale,
        // for example a replica from an earlier run.
        var resourceName = ResourcesLayout is { } layout
            ? (layout.IsMultiSelection ? null : layout.SelectedResourceName)
            : serializable.ResourceName;
        var url = DashboardUrls.MetricsUrl(
            resource: resourceName,
            meter: serializable.MeterName,
            instrument: serializable.InstrumentName,
            duration: serializable.DurationMinutes,
            view: serializable.ViewKind,
            pinned: serializable.Pinned,
            all: serializable.All,
            total: serializable.ShowRunningTotal);

        return ResourcesLayout?.AddSelectionToUrl(url) ?? url;
    }

    private async Task OnShowRunningTotalChangedAsync(bool showRunningTotal)
    {
        PageViewModel.ShowRunningTotal = showRunningTotal;
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private async Task OnViewChangedAsync(MetricViewKind newView)
    {
        PageViewModel.SelectedViewKind = newView;
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private List<OtlpInstrumentSummary> GetOverviewInstruments()
    {
        if (PageViewModel.Instruments is not { } instruments)
        {
            return [];
        }

        var groupInstruments = PageViewModel.ShowPinned
            ? PinStore.GetPinnedInstruments(instruments)
            : [.. instruments.Where(i => i.Parent.Name == PageViewModel.SelectedMeter).OrderBy(i => i.Name, StringComparer.Ordinal)];

        return [.. groupInstruments.Where(i => MetricFilter.Matches(i, PageViewModel.TextFilter))];
    }

    private string GetGroupUrl(MetricGroupKind kind, string? meterName)
    {
        var url = DashboardUrls.MetricsUrl(
            resource: CurrentResourceName,
            meter: kind == MetricGroupKind.Meter ? meterName : null,
            duration: DurationMinutes,
            view: ViewKindName,
            pinned: kind == MetricGroupKind.Pinned,
            all: kind == MetricGroupKind.All);

        // Listing several resources keeps the selection in the URL, so the page keeps listing them all.
        return (IsAggregate ? ResourcesLayout?.AddSelectionToUrl(url) : ResourcesLayout?.AddPaneToUrl(url)) ?? url;
    }

    private void OnPinsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnTextFilterChanged()
    {
        // Typing searches every meter: matches are often in another group than the selected one, e.g. typing "cpu"
        // and then "request". Clearing the filter returns to the selection, including an open chart.
        PageViewModel.ShowFilterResults = PageViewModel.TextFilter.Trim().Length > 0;
    }

    /// <summary>
    /// Gets the filter's matches from every meter, grouped by meter. The pinned group isn't included because its
    /// instruments also belong to a meter, and listing them twice would make the results look like duplicates.
    /// </summary>
    private List<IGrouping<string, OtlpInstrumentSummary>> GetFilterResultGroups()
    {
        return GetSections((PageViewModel.Instruments ?? []).Where(i => MetricFilter.Matches(i, PageViewModel.TextFilter)).OrderBy(i => i.Name, StringComparer.Ordinal), isSingleMeter: false);
    }

    private string GetInstrumentUrl(OtlpInstrumentSummary instrument)
    {
        // A card in a list of several resources opens the chart of the resource it came from.
        var url = DashboardUrls.MetricsUrl(
            resource: IsAggregate ? GetInstrumentResourceLabel(instrument) : PageViewModel.SelectedResource.Name,
            meter: instrument.Parent.Name,
            instrument: instrument.Name,
            duration: DurationMinutes,
            view: ViewKindName);

        return (IsAggregate ? ResourcesLayout?.AddSelectionToUrl(url) : ResourcesLayout?.AddPaneToUrl(url)) ?? url;
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
                if (selectedResourceKey != null || IsAggregate)
                {
                    // If there are more instruments than before then update the UI.
                    var instruments = selectedResourceKey != null
                        ? TelemetryRepository.GetInstrumentSummaries(selectedResourceKey.Value)
                        : GetAggregateInstruments();

                    if (PageViewModel.Instruments is null || instruments.Count != PageViewModel.Instruments.Count)
                    {
                        PageViewModel.Instruments = instruments;

                        // Instruments can arrive after the page loads, e.g. when the resource has just started.
                        SelectDefaultView(PageViewModel);
                        await InvokeAsync(StateHasChanged);
                    }
                }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        PinStore.Changed -= OnPinsChanged;
        if (ResourcesLayout is { } layout)
        {
            layout.ResourcesChanged -= OnLayoutResourcesChanged;
        }
        _resourcesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        TelemetryContext.Dispose();

        if (_jsModule is not null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("unregisterMetricsSplitter", _metricsSplitterElement);
                await _jsModule.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
                // The browser has already discarded the splitter and its event handlers.
            }
        }

        _selfRef?.Dispose();
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
