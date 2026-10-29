// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Humanizer;
using System.Globalization;
using System.Web;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using ResourcesPage = Aspire.Dashboard.Components.Pages.Resources;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// Layout for the resource-centric pages (overview, console logs, structured logs, traces and metrics).
/// It hosts the resource list on the left, and the selected resource's header and tabs above the page.
/// </summary>
/// <remarks>
/// Blazor keeps a layout instance alive while navigating between pages that share it, so the resource list,
/// its scroll position, filters and expanded state survive switching tabs or resources. The selected resource
/// and tab are derived from the current URL, which keeps every view addressable and lets the browser's
/// back/forward buttons work as expected.
/// </remarks>
public sealed partial class ResourcesLayout : LayoutComponentBase, IAsyncDisposable
{
    private const string FilterButtonId = "resourcePaneFilterButton";
    private const string GroupingButtonId = "resourcePaneGroupingButton";
    internal const int MinimumPaneWidthPx = 200;
    internal const int MaximumPaneWidthPx = 560;
    internal const int DefaultPaneWidthPx = 290;
    private const string ResourcesSectionKey = "section:resources";
    private const string ParametersSectionKey = "section:parameters";
    private const string TelemetrySectionKey = "section:telemetry";

    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly HashSet<string> _collapsedResourceNames = new(StringComparers.ResourceName);
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private ResourcePaneGrouping _grouping;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<MenuButtonItem> _paneMenuItems = [];
    private readonly ResourcesPage.ResourcesViewModel _filter = new() { SelectedViewKind = ResourcesPage.ResourceViewKind.Table };

    private Task? _resourceSubscriptionTask;
    private Task? _ageRefreshTask;
    private Subscription? _logsSubscription;
    private Subscription? _telemetryResourcesSubscription;
    private Dictionary<ResourceKey, int>? _unviewedErrorCounts;
    private List<TelemetryOnlyResource> _telemetryOnlyResources = [];
    private string? _collapsedResourceNamesKey;
    private bool _isLoaded;
    private bool _isPaneCollapsed;
    private bool _isDrawerOpen;
    private bool _showTelemetryOnlyResources;
    // Which kinds of items the pane lists, and so what "all resources" covers. Telemetry sources use
    // _showTelemetryOnlyResources, which replaced the filter menu's telemetry-only option.
    private bool _showResources = true;
    private bool _showParameters = true;
    private bool _pendingLandingRedirect;
    private bool _hasLocation;
    private string? _lastLocation;
    // The tab a click on the tab strip is navigating to, until the new location arrives. See OnTabChanged.
    private ResourceTab? _navigatingToTab;
    private bool _isDisposed;
    private IReadOnlyList<string> _selectedResourceNames = [];
    private List<SelectedResourceItem> _selectedItems = [];
    private string? _selectionAnchor;
    private int? _paneWidth;
    private ElementReference _layoutElement;
    private IJSObjectReference? _jsModule;
    private bool _isTabOverflowRegistered;
    private string[] _overflowTabIds = [];
    private DotNetObjectReference<ResourcesLayout>? _selfRef;
    private bool _isResizerRegistered;
    private bool _isOrderingRegistered;
    private bool _fitPaneAfterInitialRender = true;
    private string? _orderStatus;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required ResourceMenuBuilder ResourceMenuBuilder { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required ResourceTagStore TagStore { get; init; }

    [Inject]
    public required ResourceOrderStore OrderStore { get; init; }

    [Inject]
    public required Microsoft.FluentUI.AspNetCore.Components.INotificationService ToastService { get; init; }

    [Inject]
    public required ResourcePaneState PaneState { get; init; }

    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> ResourcesLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.StructuredLogs> StructuredLogsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<TerminalStrings> TerminalLoc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Columns> ColumnsLoc { get; init; }

    [Inject]
    public required ILogger<ResourcesLayout> Logger { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    internal bool IsTelemetryPane => !DashboardClient.IsEnabled || PaneState.Mode == ResourcePaneMode.Telemetry;

    /// <summary>
    /// Gets a value indicating whether the initial resource snapshot has been received.
    /// </summary>
    internal bool IsLoaded => _isLoaded;

    /// <summary>
    /// Gets the tab of the current page.
    /// </summary>
    internal ResourceTab CurrentTab { get; private set; }

    /// <summary>
    /// Gets the names of the resources selected in the resource list, in selection order. The list is empty when no
    /// resource is selected, in which case telemetry pages show data from every resource.
    /// </summary>
    internal IReadOnlyList<string> SelectedResourceNames => _selectedResourceNames;

    /// <summary>
    /// Gets a value indicating whether more than one resource is selected.
    /// </summary>
    internal bool IsMultiSelection => _selectedResourceNames.Count > 1;

    /// <summary>
    /// Gets the resource name when exactly one resource is selected, otherwise <c>null</c>.
    /// </summary>
    internal string? SelectedResourceName => _selectedResourceNames.Count == 1 ? _selectedResourceNames[0] : null;

    /// <summary>
    /// The name of the section in the desktop tabs row where tab pages render their filter controls.
    /// </summary>
    internal const string TabToolbarSectionName = "resource-tab-toolbar";

    /// <summary>
    /// The name of the section where the current page renders actions beside the resource title.
    /// </summary>
    internal const string HeaderActionsSectionName = "resource-header-actions";

    /// <summary>
    /// The name of the section where the current page renders status in the desktop workspace footer.
    /// </summary>
    internal const string FooterStatusSectionName = "resource-footer-status";

    /// <summary>
    /// Gets the app model resource that matches <see cref="SelectedResourceName"/>, if any.
    /// </summary>
    internal ResourceViewModel? SelectedResource { get; private set; }

    /// <summary>
    /// Gets the app model resources of a multi-resource selection. Selected resources that only send telemetry are
    /// not included.
    /// </summary>
    internal IEnumerable<ResourceViewModel> SelectedResources => _selectedItems.Select(i => i.Resource).OfType<ResourceViewModel>();

    internal IReadOnlyList<string> AvailableResourceTags => TagStore.GetAllTags(_resourceByName.Values);

    internal ConcurrentDictionary<string, ResourceViewModel> ResourceByName => _resourceByName;

    /// <summary>
    /// Raised on the renderer's synchronization context after resources change, so pages that display data owned by
    /// the layout (such as the resource overview) can refresh.
    /// </summary>
    internal event Action? ResourcesChanged;

    protected override async Task OnInitializedAsync()
    {
        // Switching between the live view and a recording, including restoring one when the dashboard opens,
        // re-creates the page and can dispose this layout while it's still initializing. Capture the token before
        // the first await: DisposeAsync cancels and then disposes the source, after which its Token throws.
        var cancellationToken = _cts.Token;
        _ageRefreshTask = RefreshAgesPeriodicallyAsync(cancellationToken);

        _lastLocation = NavigationManager.Uri;
        UpdateFromLocation(_lastLocation);

        _unviewedErrorCounts = TelemetryRepository.GetResourceUnviewedErrorLogsCount();
        UpdateTelemetryOnlyResources();

        var paneCollapsedResult = await LocalStorage.GetUnprotectedAsync<bool>(BrowserStorageKeys.ResourcePaneCollapsed);
        if (paneCollapsedResult.Success)
        {
            _isPaneCollapsed = paneCollapsedResult.Value;
        }

        var paneWidthResult = await LocalStorage.GetUnprotectedAsync<int>(BrowserStorageKeys.ResourcePaneWidth);
        if (paneWidthResult.Success)
        {
            _paneWidth = Math.Clamp(paneWidthResult.Value, MinimumPaneWidthPx, MaximumPaneWidthPx);
        }
        // The URL selects the mode when navigating from the main navigation. Other resource URLs, such as a row's
        // link, don't include it, so the last mode is remembered.
        var paneModeResult = await LocalStorage.GetUnprotectedAsync<string>(BrowserStorageKeys.ResourcePaneMode);
        if (DashboardClient.IsEnabled && paneModeResult.Success && Enum.TryParse<ResourcePaneMode>(paneModeResult.Value, ignoreCase: true, out var storedPaneMode) && storedPaneMode != ResourcePaneMode.Telemetry)
        {
            // Migrate the former Tags navigation view to the resource pane's grouping preference.
            _grouping = storedPaneMode == ResourcePaneMode.Tags ? ResourcePaneGrouping.Tags : ResourcePaneGrouping.None;
            PaneState.SetMode(ResourcePaneMode.Resources);
        }
        var groupingResult = await LocalStorage.GetUnprotectedAsync<string>(BrowserStorageKeys.ResourcePaneGrouping);
        if (groupingResult.Success && Enum.TryParse<ResourcePaneGrouping>(groupingResult.Value, out var grouping) && Enum.IsDefined(grouping))
        {
            _grouping = grouping;
        }
        else if (_grouping == ResourcePaneGrouping.Tags)
        {
            await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneGrouping, _grouping.ToString());
        }
        await ViewPreferences.EnsureLoadedAsync();
        var kindsResult = await LocalStorage.GetUnprotectedAsync<string>(BrowserStorageKeys.ResourcePaneKinds);
        if (kindsResult.Success && kindsResult.Value is { } storedKinds)
        {
            var kinds = storedKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _showResources = kinds.Contains(nameof(ResourcePaneKind.Resources), StringComparer.Ordinal);
            _showParameters = kinds.Contains(nameof(ResourcePaneKind.Parameters), StringComparer.Ordinal);
            _showTelemetryOnlyResources = kinds.Contains(nameof(ResourcePaneKind.TelemetrySources), StringComparer.Ordinal);
        }
        await ApplyPaneModeFromLocationAsync(_lastLocation);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TagStore.Changed += OnTagsChanged;
        ViewPreferences.Changed += OnViewPreferencesChanged;
        OrderStore.Changed += OnOrderChanged;

        var showHiddenResources = await SessionStorage.GetAsync<bool>(BrowserStorageKeys.ResourcesShowHiddenResources);
        if (showHiddenResources.Success)
        {
            _filter.ShowHiddenResources = showHiddenResources.Value;
        }

        if (DashboardClient.IsEnabled)
        {
            // The application name is only correct once the dashboard is connected, and it scopes persisted tree state.
            await DashboardClient.WhenConnected;
            _collapsedResourceNamesKey = BrowserStorageKeys.CollapsedResourceNamesKey(DashboardClient.ApplicationName);
            var collapsedResult = await LocalStorage.GetAsync<List<string>>(_collapsedResourceNamesKey);
            if (collapsedResult.Success)
            {
                foreach (var resourceName in collapsedResult.Value)
                {
                    _collapsedResourceNames.Add(resourceName);
                }
            }
        }

        // Don't subscribe after DisposeAsync has run: nothing would dispose these subscriptions.
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _logsSubscription = TelemetryRepository.OnNewLogs(null, SubscriptionType.Other, async () =>
        {
            var counts = TelemetryRepository.GetResourceUnviewedErrorLogsCount();
            await InvokeAsync(() =>
            {
                _unviewedErrorCounts = counts;
                StateHasChanged();
            });
        });

        _telemetryResourcesSubscription = TelemetryRepository.OnNewResources(async () =>
        {
            await InvokeAsync(() =>
            {
                UpdateTelemetryOnlyResources();
                UpdateSelection();
                StateHasChanged();
                ResourcesChanged?.Invoke();
            });
        });

        // Standalone resources come from OTLP. The disabled AppHost client cannot be awaited or subscribed to.
        if (!DashboardClient.IsEnabled)
        {
            _isLoaded = true;
            UpdateTelemetryOnlyResources();
            UpdateSelection();
            ResourcesChanged?.Invoke();
            return;
        }

        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
        foreach (var resource in snapshot)
        {
            UpsertResource(resource);
        }
        OrderStore.EnsureResources(snapshot.Where(r => !r.IsParameter)
            .OrderBy(r => r.ResourceType).ThenBy(r => r, ResourceViewModelNameComparer.Instance));
        UpdateResourceTagFilters();
        EnsureGroupOrder();

        _isLoaded = true;
        UpdateTelemetryOnlyResources();
        CarrySelectionAcrossRuns();
        UpdateSelection();
        UpdatePaneMenuItems();
        await TryRedirectLandingAsync();

        _resourceSubscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        UpsertResource(resource);
                        if (!resource.IsParameter)
                        {
                            OrderStore.EnsureResources([resource]);
                        }
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                    }
                }

                await InvokeAsync(async () =>
                {
                    UpdateResourceTagFilters();
                    EnsureGroupOrder();
                    UpdateTelemetryOnlyResources();
                    UpdateSelection();
                    UpdatePaneMenuItems();
                    await TryRedirectLandingAsync();
                    StateHasChanged();
                    ResourcesChanged?.Invoke();
                });
            }
        });

        ResourcesChanged?.Invoke();
    }

    private void UpsertResource(ResourceViewModel resource)
    {
        _resourceByName[resource.Name] = resource;

        // New types and states are visible unless the user has hidden them already.
        _filter.ResourceTypesToVisibility.TryAdd(resource.ResourceType, true);
        _filter.ResourceStatesToVisibility.TryAdd(resource.State ?? string.Empty, true);
        _filter.ResourceHealthStatusesToVisibility.TryAdd(resource.HealthStatus?.Humanize() ?? string.Empty, true);
    }

    protected override async Task OnParametersSetAsync()
    {
        // The router sets a new body on every navigation. Selection is updated here, rather than in a
        // LocationChanged handler, so it's current before the page in the body renders.
        var location = NavigationManager.Uri;
        if (string.Equals(location, _lastLocation, StringComparison.Ordinal))
        {
            return;
        }

        _lastLocation = location;
        _navigatingToTab = null;
        var previousTab = CurrentTab;
        var previousSelection = _selectedResourceNames;
        UpdateFromLocation(location);
        await ApplyPaneModeFromLocationAsync(location);
        _isDrawerOpen = false;
        await TryRedirectLandingAsync();
        await PersistSelectedResourcesAsync();

        if (previousTab != CurrentTab || !previousSelection.SequenceEqual(_selectedResourceNames, StringComparers.ResourceName))
        {
            // Selection and query-only terminal focus changes can reuse the page's parameters.
            // Notify it so the mounted terminal changes layout even when no resource updates arrive.
            ResourcesChanged?.Invoke();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var showResizer = ViewportInformation.IsDesktop && !_isPaneCollapsed;
        var registrationChanged = showResizer != _isResizerRegistered || !_isOrderingRegistered;
        if (!registrationChanged && _isTabOverflowRegistered && !(_fitPaneAfterInitialRender && _isLoaded && showResizer))
        {
            return;
        }

        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./{Assets["Components/Layout/ResourcesLayout.razor.js"]}");
            _selfRef ??= DotNetObjectReference.Create(this);
            if (!_isTabOverflowRegistered)
            {
                _isTabOverflowRegistered = true;
                await _jsModule.InvokeVoidAsync("registerTabOverflow", _selfRef);
            }
            if (!_isOrderingRegistered)
            {
                await _jsModule.InvokeVoidAsync("registerResourceOrdering", _layoutElement, _selfRef);
                _isOrderingRegistered = true;
            }
            if (registrationChanged && showResizer)
            {
                await _jsModule.InvokeVoidAsync("registerPaneResizer", _layoutElement, _selfRef, MinimumPaneWidthPx, MaximumPaneWidthPx);
            }
            else if (registrationChanged)
            {
                await _jsModule.InvokeVoidAsync("unregisterPaneResizer", _layoutElement);
            }

            _isResizerRegistered = showResizer;
            await _jsModule.InvokeVoidAsync(
                "setTabListAriaLabel",
                GetTabId(CurrentTab),
                Loc[nameof(Dashboard.Resources.Layout.ResourceTabsLandmark)].Value);

            if (_fitPaneAfterInitialRender && _isLoaded && showResizer)
            {
                // Stay armed until rows with actions exist, e.g. when the resources arrive after the first render.
                _fitPaneAfterInitialRender = !await _jsModule.InvokeAsync<bool>("fitPaneToActions", _layoutElement);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected while rendering. There is nothing to register.
        }
    }

    /// <summary>
    /// Called by the tab strip when the tabs that no longer fit change. They move to the "..." menu.
    /// </summary>
    [JSInvokable]
    public void SetOverflowTabs(string[] tabIds)
    {
        if (_isDisposed || _overflowTabIds.SequenceEqual(tabIds))
        {
            return;
        }

        _overflowTabIds = tabIds;
        StateHasChanged();
    }

    private IList<MenuButtonItem> GetOverflowMenuItems() =>
        GetTabMenuItems().Where(item => _overflowTabIds.Contains(item.Id!.Replace("-menu-item", string.Empty, StringComparison.Ordinal))).ToList();

    /// <summary>
    /// Called by the pane resizer after the user resizes the resource list.
    /// </summary>
    [JSInvokable]
    public async Task SetPaneWidthAsync(int widthPx)
    {
        _paneWidth = Math.Clamp(widthPx, MinimumPaneWidthPx, MaximumPaneWidthPx);
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneWidth, _paneWidth.Value);
        StateHasChanged();
    }

    private string? GetLayoutStyle() => _paneWidth is { } width && ViewportInformation.IsDesktop
        ? string.Create(CultureInfo.InvariantCulture, $"--resource-pane-expanded-width: {width}px")
        : null;

    /// <summary>
    /// Derives the tab and selected resources from a URL such as <c>/traces/resource/api?type=http</c>.
    /// </summary>
    private void UpdateFromLocation(string location)
    {
        var baseRelativePath = NavigationManager.ToBaseRelativePath(location);
        var parsedLocation = ParseLocation(baseRelativePath);
        var wasAllResources = _hasLocation && _selectedResourceNames.Count == 0;
        _hasLocation = true;

        CurrentTab = parsedLocation.Tab;
        RouteResourceName = parsedLocation.RouteResourceName;
        if (!parsedLocation.KeepSelection)
        {
            _selectedResourceNames = parsedLocation.SelectedResourceNames;
        }

        // The overview without a selection lists all resources. Entering the resources view that way, for example
        // from the navigation rail, restores the last selection instead. Switching to the overview tab while all
        // resources are already shown keeps them. Telemetry sources has its own selection, so it must not restore
        // the last app-resource selection when its overview route has no selected source.
        _pendingLandingRedirect = DashboardClient.IsEnabled
            && ParsePaneMode(baseRelativePath) != ResourcePaneMode.Telemetry
            && parsedLocation.Tab == ResourceTab.Overview
            && parsedLocation.SelectedResourceNames.Count == 0
            && !wasAllResources;
        UpdateSelection();
    }

    /// <summary>
    /// Gets the resource in the URL path of the current page. It's usually the selected resource, but with several
    /// selected resources, the metrics page uses it for the resource whose instruments are displayed.
    /// </summary>
    internal string? RouteResourceName { get; private set; }

    /// <summary>
    /// Gets the URL of the given tab for the resources currently selected in the resource list.
    /// </summary>
    internal string GetTabUrl(ResourceTab tab)
    {
        if (!IsMultiSelection && tab is ResourceTab.Overview or ResourceTab.Terminal &&
            CurrentTab is ResourceTab.Overview or ResourceTab.Terminal &&
            NavigationManager.ToBaseRelativePath(NavigationManager.Uri).StartsWith(
                $"{DashboardUrls.ResourceOverviewBasePath}/", StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.SetResourceTerminalFocus(NavigationManager.Uri, focused: tab == ResourceTab.Terminal);
        }

        if (tab == ResourceTab.Metrics && _selectedResourceNames.Count == 0 && !DashboardClient.IsEnabled &&
            _telemetryOnlyResources.FirstOrDefault(resource => resource.Resource.HasMetrics) is { } metricsResource)
        {
            return GetTabUrl(tab, [metricsResource.Name], metricsResource.Name);
        }

        return GetTabUrl(tab, _selectedResourceNames, RouteResourceName);
    }

    internal string AddPaneToUrl(string url) => DashboardClient.IsEnabled && IsTelemetryPane ? DashboardUrls.AddTelemetryPane(url) : url;

    private async Task ApplyPaneModeFromLocationAsync(string location)
    {
        if (!DashboardClient.IsEnabled)
        {
            PaneState.SetMode(ResourcePaneMode.Telemetry);
            return;
        }

        var mode = ParsePaneMode(NavigationManager.ToBaseRelativePath(location));
        if (mode is null)
        {
            // A direct telemetry resource link can select the telemetry pane without a query parameter.
            mode = _selectedItems.Any(i => i.TelemetryOnly is not null) ? ResourcePaneMode.Telemetry
                : PaneState.Mode == ResourcePaneMode.Telemetry ? ResourcePaneMode.Resources : PaneState.Mode;
        }
        if (mode == ResourcePaneMode.Tags)
        {
            await SetGroupingAsync(ResourcePaneGrouping.Tags);
            mode = ResourcePaneMode.Resources;
        }
        PaneState.SetMode(mode.Value);
        if (mode == ResourcePaneMode.Telemetry)
        {
            return;
        }
        try
        {
            await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneMode, mode.Value.ToString());
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected while navigating. There is nothing to persist to.
        }
    }

    /// <summary>
    /// Gets the resource list mode from a URL such as <c>/resources?pane=tags</c>, or <c>null</c> when the URL
    /// doesn't specify one.
    /// </summary>
    internal static ResourcePaneMode? ParsePaneMode(string baseRelativePath)
    {
        var queryIndex = baseRelativePath.IndexOf('?');
        if (queryIndex < 0)
        {
            return null;
        }

        var query = baseRelativePath[queryIndex..];
        var fragmentIndex = query.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            query = query[..fragmentIndex];
        }

        if (!QueryHelpers.ParseQuery(query).TryGetValue(DashboardUrls.ResourcePaneQueryName, out var values))
        {
            return null;
        }

        return values.ToString().ToLowerInvariant() switch
        {
            DashboardUrls.ResourcePaneResourcesValue => ResourcePaneMode.Resources,
            DashboardUrls.ResourcePaneTagsValue => ResourcePaneMode.Tags,
            DashboardUrls.ResourcePaneTelemetryValue => ResourcePaneMode.Telemetry,
            _ => null
        };
    }

    internal static ResourceLocation ParseLocation(string baseRelativePath)
    {
        var path = baseRelativePath;
        var query = string.Empty;
        var fragmentIndex = path.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            path = path[..fragmentIndex];
        }
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            query = path[queryIndex..];
            path = path[..queryIndex];
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return new ResourceLocation(ResourceTab.Overview, RouteResourceName: null, SelectedResourceNames: [], KeepSelection: false);
        }

        // Trace details are addressed by trace id. Keep the previously selected resources so the resource header
        // and tabs stay stable while drilling into a trace.
        if (string.Equals(segments[0], DashboardUrls.TracesBasePath, StringComparisons.UrlPath) &&
            segments.Length > 1 &&
            string.Equals(segments[1], "detail", StringComparisons.UrlPath))
        {
            return new ResourceLocation(ResourceTab.Traces, RouteResourceName: null, SelectedResourceNames: [], KeepSelection: true);
        }

        var tab = segments[0].ToLowerInvariant() switch
        {
            DashboardUrls.ParametersBasePath => ResourceTab.Parameters,
            DashboardUrls.ConsoleLogBasePath => ResourceTab.Console,
            DashboardUrls.TerminalsBasePath => ResourceTab.Terminal,
            DashboardUrls.StructuredLogsBasePath => ResourceTab.StructuredLogs,
            DashboardUrls.TracesBasePath => ResourceTab.Traces,
            DashboardUrls.MetricsBasePath => ResourceTab.Metrics,
            _ => ResourceTab.Overview
        };

        string? routeResourceName = null;
        if (tab == ResourceTab.Overview)
        {
            // resources/{name}
            if (segments.Length > 1)
            {
                routeResourceName = Uri.UnescapeDataString(segments[1]);
            }
        }
        else if (segments.Length > 2 && string.Equals(segments[1], "resource", StringComparisons.UrlPath))
        {
            // {page}/resource/{name}
            routeResourceName = Uri.UnescapeDataString(segments[2]);
        }

        // Several selected resources are listed in the query string, for example ?resource=api&resource=worker.
        var selectedResourceNames = new List<string>();
        var queryParameters = QueryHelpers.ParseQuery(query);
        if (queryParameters.TryGetValue(DashboardUrls.ResourceSelectionQueryName, out var queryValues))
        {
            foreach (var value in queryValues)
            {
                if (!string.IsNullOrEmpty(value) && !selectedResourceNames.Contains(value, StringComparers.ResourceName))
                {
                    selectedResourceNames.Add(value);
                }
            }
        }

        if (selectedResourceNames.Count == 0 && routeResourceName is not null)
        {
            selectedResourceNames.Add(routeResourceName);
        }

        if (tab == ResourceTab.Overview && routeResourceName is not null &&
            selectedResourceNames.Count == 1 &&
            queryParameters.TryGetValue(DashboardUrls.ResourceTerminalQueryName, out var terminalFocus) &&
            string.Equals(terminalFocus, "true", StringComparison.OrdinalIgnoreCase))
        {
            tab = ResourceTab.Terminal;
        }

        return new ResourceLocation(tab, routeResourceName, selectedResourceNames, KeepSelection: false);
    }

    private void UpdateSelection()
    {
        _selectedItems = _selectedResourceNames.Select(name =>
        {
            if (ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource))
            {
                return new SelectedResourceItem(name, resource, TelemetryOnly: null);
            }

            var telemetryOnly = _telemetryOnlyResources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparisons.ResourceName));
            return new SelectedResourceItem(name, Resource: null, telemetryOnly);
        }).ToList();

        SelectedResource = _selectedItems is [{ Resource: { } selected }] ? selected : null;

        if (_isLoaded)
        {
            PaneState.SelectionRunId = DataSource.SelectedRun.RunId;
            PaneState.SelectionPersistentKeys = _selectedItems
                .Where(i => i.Resource is not null)
                .ToDictionary(i => i.Name, i => i.Resource!.PersistentKey, StringComparers.ResourceName);
        }

        if (DashboardClient.IsEnabled && _selectedItems.Any(i => i.TelemetryOnly is not null) &&
            ParsePaneMode(NavigationManager.ToBaseRelativePath(NavigationManager.Uri)) is null)
        {
            PaneState.SetMode(ResourcePaneMode.Telemetry);
        }

    }

    /// <summary>
    /// After switching between the live view and a recording, replaces selected resources that don't exist in the
    /// new run with the same resource in it, matched by persistent key: replica names end with a random suffix that
    /// differs between runs. Resources with no match are dropped, which shows every resource when none is left.
    /// </summary>
    private void CarrySelectionAcrossRuns()
    {
        var previousRunId = PaneState.SelectionRunId;
        if (previousRunId is null || string.Equals(previousRunId, DataSource.SelectedRun.RunId, StringComparison.Ordinal) || _selectedResourceNames.Count == 0)
        {
            return;
        }

        var selection = new List<string>();
        foreach (var name in _selectedResourceNames)
        {
            string? match = null;
            if (IsKnownResourceName(name) || _telemetryOnlyResources.Any(r => string.Equals(r.Name, name, StringComparisons.ResourceName)))
            {
                match = name;
            }
            else if (PaneState.SelectionPersistentKeys.TryGetValue(name, out var persistentKey))
            {
                match = _resourceByName.Values
                    .Where(r => string.Equals(r.PersistentKey, persistentKey, StringComparisons.ResourceName))
                    .Select(r => r.Name)
                    .FirstOrDefault();
            }

            if (match is not null && !selection.Contains(match, StringComparers.ResourceName))
            {
                selection.Add(match);
            }
        }

        if (!selection.SequenceEqual(_selectedResourceNames, StringComparers.ResourceName))
        {
            NavigationManager.NavigateTo(GetSelectionUrl(selection), new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    private async Task TryRedirectLandingAsync()
    {
        if (!_pendingLandingRedirect || !_isLoaded)
        {
            return;
        }

        _pendingLandingRedirect = false;

        List<string> resourceNames;
        try
        {
            // Entering the resources view restores the selection made earlier in this browser session. Without one,
            // such as when the dashboard opens, all resources stay selected and the overview summarizes the app.
            var lastSelected = await SessionStorage.GetAsync<List<string>>(BrowserStorageKeys.LastSelectedResources);
            resourceNames = lastSelected is { Success: true, Value: { } names }
                ? names.Where(IsKnownResourceName).ToList()
                : [];
        }
        catch (JSDisconnectedException)
        {
            return;
        }

        if (resourceNames.Count > 0)
        {
            NavigationManager.NavigateTo(GetTabUrl(ResourceTab.Overview, resourceNames), new NavigationOptions { ReplaceHistoryEntry = true });
        }
    }

    private bool IsKnownResourceName(string name) =>
        ResourceViewModel.TryGetResourceByName(name, _resourceByName, out _);

    private async Task PersistSelectedResourcesAsync()
    {
        // Navigating to a page without a selection, such as all structured logs, doesn't clear the remembered
        // selection. Only clearing the selection in the resource list does.
        if (_selectedResourceNames.Count == 0)
        {
            return;
        }

        await SetStoredSelectionAsync(_selectedResourceNames);
    }

    private async Task SetStoredSelectionAsync(IReadOnlyList<string> resourceNames)
    {
        if (IsTelemetryPane)
        {
            return;
        }

        try
        {
            await SessionStorage.SetAsync(BrowserStorageKeys.LastSelectedResources, resourceNames.ToList());
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected while navigating. There is nothing to persist to.
        }
    }

    private void UpdateTelemetryOnlyResources()
    {
        // Resources that send telemetry but aren't part of the app model (for example a browser app) are listed
        // in their own pane so they don't appear in Resources or Tags.
        var telemetryResources = TelemetryRepository.GetResources();
        var appModelKeys = new HashSet<ResourceKey>();
        foreach (var resource in _resourceByName.Values)
        {
            if (TelemetryRepository.GetResourceByCompositeName(resource.Name) is { } otlpResource)
            {
                appModelKeys.Add(otlpResource.ResourceKey);
            }
        }

        _telemetryOnlyResources = telemetryResources
            .Where(r => !appModelKeys.Contains(r.ResourceKey))
            .Select(r => new TelemetryOnlyResource(OtlpHelpers.GetResourceName(r, telemetryResources), r))
            .OrderBy(r => r.Name, StringComparers.ResourceName)
            .ToList();
    }

    private IEnumerable<TelemetryOnlyResource> GetVisibleTelemetryResources() =>
        _telemetryOnlyResources.Where(r =>
            (!DashboardClient.IsEnabled || _showTelemetryOnlyResources || IsSelected(r.Name)) &&
            r.Name.Contains(_filter.TextFilter, StringComparisons.UserTextSearch));

    internal IEnumerable<ResourceGridViewModel> GetPaneRows()
    {
        if (!_showResources)
        {
            return [];
        }

        var positions = OrderStore.GetPositions();
        var filteredResources = _resourceByName.Values
            .Where(r => !r.IsParameter && _filter.FilterAllResourceKinds(r, _filter.TextFilter))
            .Select(r => new ResourceGridViewModel { Resource = r })
            .OrderBy(r => positions.GetValueOrDefault(r.Resource.PersistentKey, long.MaxValue))
            .ThenBy(r => r.Resource, ResourceViewModelNameComparer.Instance)
            .ToList();

        // Nested resources are placed under their parent after sorting so children keep their order.
        return ResourceGridViewModel.OrderNestedResources(filteredResources, r => _collapsedResourceNames.Contains(r.PersistentKey))
            .Where(r => !r.IsHidden);
    }

    private List<ResourceViewModel> GetParameterPaneRows() =>
        !_showParameters ? [] : _resourceByName.Values
            .Where(r => r.IsParameter && _filter.FilterAllResourceKinds(r, _filter.TextFilter))
            .OrderBy(r => r.HasMissingParameterValueState() ? 0 : 1)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();

    /// <summary>
    /// Gets the filtered resources grouped by the selected property. Text search matches group names or resources.
    /// </summary>
    private List<ResourceGroup> GetResourceGroups()
    {
        // Tag groups include parameters (see GetTagGroups), so they can have content when only parameters are shown.
        if (_grouping == ResourcePaneGrouping.Tags ? !_showResources && !_showParameters : !_showResources)
        {
            return [];
        }

        var groups = _grouping == ResourcePaneGrouping.Tags ? GetTagGroups() : _resourceByName.Values
            .Where(r => !r.IsParameter && _filter.FilterAllResourceKinds(r, textFilter: string.Empty))
            .GroupBy(r => _grouping == ResourcePaneGrouping.ResourceType ? r.ResourceType
                : string.IsNullOrEmpty(r.State) ? nameof(KnownResourceState.Unknown) : r.State, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResourceGroup(group.Key,
                group.Where(r => GetGroupName(group.Key).Contains(_filter.TextFilter, StringComparisons.UserTextSearch) || r.MatchesFilter(_filter.TextFilter))
                    .OrderBy(r => r, ResourceViewModelNameComparer.Instance).ToList()))
            .Where(group => group.Resources.Count > 0)
            .OrderBy(group => GetGroupName(group.Value), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var positions = OrderStore.GetGroupPositions(_grouping.ToString());
        return groups.OrderBy(group => positions.GetValueOrDefault(GetGroupOrderKey(group), long.MaxValue)).ToList();
    }

    private string GetGroupName(string? value) => GetGroupName(_grouping, value);

    private string GetGroupName(ResourcePaneGrouping grouping, string? value) => grouping switch
    {
        ResourcePaneGrouping.Tags => value ?? Loc[nameof(Resources.Layout.ResourcePaneUntaggedGroup)],
        ResourcePaneGrouping.State => string.IsNullOrEmpty(value) ? ColumnsLoc[nameof(Columns.UnknownStateLabel)] : value.Humanize(),
        _ => value ?? string.Empty
    };

    private void EnsureGroupOrder()
    {
        // Register groups independently of search/type/state/health filters, so a filtered view cannot change
        // their saved order. Hidden resources participate once the user chooses to show them.
        var resources = _resourceByName.Values.Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources)).ToList();
        var tagsByResource = TagStore.GetTagsByResource(resources);
        var tags = TagStore.GetAllTags(resources)
            .Where(tag => resources.Any(r => tagsByResource.TryGetValue(r.DisplayName, out var resourceTags) && resourceTags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        if (resources.Any(r => !tagsByResource.ContainsKey(r.DisplayName)))
        {
            tags.Add(string.Empty);
        }
        OrderStore.EnsureGroups(nameof(ResourcePaneGrouping.Tags), tags);
        OrderStore.EnsureGroups(nameof(ResourcePaneGrouping.ResourceType), resources.Select(r => r.ResourceType)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase));
        OrderStore.EnsureGroups(nameof(ResourcePaneGrouping.State), resources
            .Select(r => string.IsNullOrEmpty(r.State) ? nameof(KnownResourceState.Unknown) : r.State)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => GetGroupName(ResourcePaneGrouping.State, value), StringComparer.CurrentCultureIgnoreCase));
    }

    // Tags are normalized to nonempty values, so the empty key uniquely identifies the synthetic untagged group.
    private static string GetGroupOrderKey(ResourceGroup group) => group.Value ?? string.Empty;

    private List<MenuButtonItem> GetGroupingMenuItems() =>
        new[]
        {
            new MenuButtonItem
            {
                IsHeader = true,
                Text = Loc[nameof(Resources.Layout.ResourcePaneGrouping)],
                Icon = new Icons.Regular.Size20.GroupList(),
                RenderKey = "resource-grouping-header"
            }
        }
        .Concat(Enum.GetValues<ResourcePaneGrouping>().Select(grouping => new MenuButtonItem
        {
            Id = $"resource-grouping-{grouping}",
            Class = "resource-grouping-menu-item",
            Text = Loc[grouping switch
            {
                ResourcePaneGrouping.None => nameof(Resources.Layout.ResourcePaneGroupingNone),
                ResourcePaneGrouping.Tags => nameof(Resources.Layout.NavMenuTagsTab),
                ResourcePaneGrouping.ResourceType => nameof(Resources.Layout.ResourcePaneGroupingResourceType),
                _ => nameof(Resources.Layout.ResourcePaneGroupingState)
            }],
            Tooltip = string.Empty,
            Role = MenuItemRole.Radio,
            Checked = _grouping == grouping,
            OnClick = () => SetGroupingAsync(grouping)
        }))
        .ToList();

    private async Task SetGroupingAsync(ResourcePaneGrouping grouping)
    {
        _grouping = grouping;
        StateHasChanged();
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneGrouping, grouping.ToString());
    }

    // Resources with multiple tags appear in each group; untagged resources initially appear last. Parameters are part of
    // the groups too, after the group's resources: an app tags a parameter like the resource that uses it, so the
    // parameter shows next to that resource.
    private List<ResourceGroup> GetTagGroups()
    {
        var textFilter = _filter.TextFilter;
        var resources = _resourceByName.Values
            .Where(r => (r.IsParameter ? _showParameters : _showResources) && _filter.FilterAllResourceKinds(r, textFilter: string.Empty))
            .OrderBy(r => r.IsParameter)
            .ThenBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();

        var tagsByResource = TagStore.GetTagsByResource(resources);
        var groups = new List<ResourceGroup>();
        foreach (var tag in TagStore.GetAllTags(resources))
        {
            // A resource can carry several tags. When the tag filter hides a tag, its group stays hidden even though
            // resources shown through another tag also carry it.
            if (!IsTagVisible(tag))
            {
                continue;
            }

            var tagMatchesFilter = textFilter.Length == 0 || tag.Contains(textFilter, StringComparisons.UserTextSearch);
            var taggedResources = resources
                .Where(r => tagsByResource.TryGetValue(r.DisplayName, out var tags) && tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Where(r => tagMatchesFilter || r.MatchesFilter(textFilter))
                .ToList();

            if (taggedResources.Count > 0)
            {
                groups.Add(new ResourceGroup(tag, taggedResources));
            }
        }

        var untaggedResources = resources
            .Where(r => !tagsByResource.ContainsKey(r.DisplayName))
            .Where(r => textFilter.Length == 0 || r.MatchesFilter(textFilter))
            .ToList();

        if (untaggedResources.Count > 0 && IsTagVisible(string.Empty))
        {
            groups.Add(new ResourceGroup(Value: null, untaggedResources));
        }

        return groups;
    }

    // The tag filter keys untagged resources by the empty string, and tags missing from it are visible.
    private bool IsTagVisible(string tag) =>
        !_filter.ResourceTagsToVisibility.TryGetValue(tag, out var visible) || visible;

    private bool IsGroupCollapsed(ResourceGroup group) => _collapsedGroups.Contains(GetGroupKey(group));

    private bool IsSectionCollapsed(string sectionKey) => _collapsedGroups.Contains(sectionKey);

    private void ToggleGroup(ResourceGroup group)
    {
        ToggleCollapsedGroup(GetGroupKey(group));
    }

    private void ToggleSection(string sectionKey)
    {
        ToggleCollapsedGroup(sectionKey);
    }

    private void ToggleCollapsedGroup(string key)
    {
        if (!_collapsedGroups.Remove(key))
        {
            _collapsedGroups.Add(key);
        }
    }

    private string GetGroupKey(ResourceGroup group) => $"{_grouping}:{group.Value}";

    private void OnTagsChanged() => _ = InvokeAsync(() =>
    {
        if (_isLoaded)
        {
            UpdateResourceTagFilters();
            EnsureGroupOrder();
        }
        StateHasChanged();
    });

    private IReadOnlyList<ResourcePaneFilter.FilterGroup> GetFilterGroups() =>
    [
        new("types", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesResourceTypesHeader)], _filter.ResourceTypesToVisibility, r => [r.ResourceType]),
        new("states", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesResourceStatesHeader)], _filter.ResourceStatesToVisibility, r => [r.State ?? string.Empty]),
        new("health", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesDetailsHealthStateProperty)], _filter.ResourceHealthStatusesToVisibility, r => [r.HealthStatus?.Humanize() ?? string.Empty]),
        new("tags",
            Loc[nameof(Resources.Layout.NavMenuTagsTab)],
            _filter.ResourceTagsToVisibility,
            r => _filter.ResourceTagsByResource.GetValueOrDefault(r.DisplayName) ?? [string.Empty],
            key => string.IsNullOrEmpty(key) ? Loc[nameof(Resources.Layout.ResourcePaneUntaggedGroup)] : key)
    ];

    private IEnumerable<ResourceViewModel> GetFilterCountResources() =>
        _resourceByName.Values.Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources));

    private void UpdateResourceTagFilters()
    {
        var resources = _resourceByName.Values.ToList();
        var tagsByResource = TagStore.GetTagsByResource(resources);
        var filterKeys = TagStore.GetAllTags(resources).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (resources.Any(r => !tagsByResource.ContainsKey(r.DisplayName)))
        {
            filterKeys.Add(string.Empty);
        }

        foreach (var key in _filter.ResourceTagsToVisibility.Keys)
        {
            if (!filterKeys.Contains(key))
            {
                _filter.ResourceTagsToVisibility.TryRemove(key, out _);
            }
        }

        foreach (var key in filterKeys)
        {
            _filter.ResourceTagsToVisibility.TryAdd(key, true);
        }

        _filter.ResourceTagsByResource = tagsByResource;
    }

    private bool NoFiltersSet =>
        _filter.ResourceTypesToVisibility.Values.All(v => v) &&
        _filter.ResourceStatesToVisibility.Values.All(v => v) &&
        _filter.ResourceHealthStatusesToVisibility.Values.All(v => v) &&
        _filter.ResourceTagsToVisibility.Values.All(v => v);

    private bool CanReorderPane =>
        _isLoaded && !IsTelemetryPane &&
        _filter.TextFilter.Length == 0 && NoFiltersSet;

    private bool CanReorderResources => CanReorderPane && _grouping == ResourcePaneGrouping.None;

    private bool CanReorderGroups => CanReorderPane && _grouping != ResourcePaneGrouping.None;

    private string GetReorderParentName(ResourceViewModel resource)
    {
        var parentName = resource.GetResourcePropertyValue(KnownProperties.Resource.ParentName);
        return parentName is not null && _resourceByName.TryGetValue(parentName, out var parent) && _filter.Filter(parent)
            ? parentName : string.Empty;
    }

    [JSInvokable]
    public async Task<bool> ReorderResourceAsync(string resourceName, string targetName, bool after)
    {
        // Revalidate on the server: filters, grouping, or the resource tree can change during a drag.
        if (!CanReorderResources ||
            !_resourceByName.TryGetValue(resourceName, out var resource) ||
            !_resourceByName.TryGetValue(targetName, out var target) ||
            !_filter.Filter(resource) || !_filter.Filter(target) ||
            !StringComparers.ResourceName.Equals(GetReorderParentName(resource), GetReorderParentName(target)))
        {
            Logger.LogDebug("Rejected resource reorder from '{ResourceName}' to '{TargetName}' because the visible list changed or the resources aren't siblings.", resourceName, targetName);
            await ToastService.ShowWarningToastAsync(Loc[nameof(Resources.Layout.ResourcePaneReorderUnavailable)]);
            return false;
        }
        if (!OrderStore.TryMove(resource.PersistentKey, target.PersistentKey, after))
        {
            await ToastService.ShowErrorToastAsync(Loc[nameof(Resources.Layout.ResourcePaneReorderFailed)]);
            return false;
        }

        _orderStatus = string.Format(CultureInfo.CurrentCulture,
            Loc[after ? nameof(Resources.Layout.ResourcePaneMovedAfter) : nameof(Resources.Layout.ResourcePaneMovedBefore)],
            GetResourceName(resource), GetResourceName(target));
        StateHasChanged();
        return true;
    }

    private void OnOrderChanged() => _ = InvokeAsync(StateHasChanged);

    [JSInvokable]
    public async Task<bool> ReorderGroupAsync(string grouping, string groupKey, string targetKey, bool after)
    {
        var groups = CanReorderGroups && string.Equals(grouping, _grouping.ToString(), StringComparison.Ordinal)
            ? GetResourceGroups() : [];
        var group = groups.Find(group => StringComparers.ResourceName.Equals(GetGroupKey(group), groupKey));
        var target = groups.Find(group => StringComparers.ResourceName.Equals(GetGroupKey(group), targetKey));
        if (group is null || target is null)
        {
            Logger.LogDebug("Rejected group reorder from '{GroupKey}' to '{TargetKey}' because the grouping or visible list changed.", groupKey, targetKey);
            await ToastService.ShowWarningToastAsync(Loc[nameof(Resources.Layout.ResourcePaneGroupReorderUnavailable)]);
            return false;
        }
        if (!OrderStore.TryMoveGroup(grouping, GetGroupOrderKey(group), GetGroupOrderKey(target), after))
        {
            await ToastService.ShowErrorToastAsync(Loc[nameof(Resources.Layout.ResourcePaneGroupReorderFailed)]);
            return false;
        }

        _orderStatus = string.Format(CultureInfo.CurrentCulture,
            Loc[after ? nameof(Resources.Layout.ResourcePaneMovedAfter) : nameof(Resources.Layout.ResourcePaneMovedBefore)],
            GetGroupName(group.Value), GetGroupName(target.Value));
        StateHasChanged();
        return true;
    }

    private async Task OnToggleCollapseAsync(ResourceGridViewModel viewModel)
    {
        if (!_collapsedResourceNames.Remove(viewModel.Resource.PersistentKey))
        {
            _collapsedResourceNames.Add(viewModel.Resource.PersistentKey);
        }

        await PersistCollapsedResourceNamesAsync();
        UpdatePaneMenuItems();
    }

    private async Task PersistCollapsedResourceNamesAsync()
    {
        if (_collapsedResourceNamesKey is not null)
        {
            await LocalStorage.SetAsync(_collapsedResourceNamesKey, _collapsedResourceNames.ToList());
        }
    }

    private async Task TogglePaneCollapsedAsync()
    {
        _isPaneCollapsed = !_isPaneCollapsed;
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneCollapsed, _isPaneCollapsed);
    }

    private void OpenDrawer() => _isDrawerOpen = true;

    private void CloseDrawer() => _isDrawerOpen = false;

    private void UpdatePaneMenuItems()
    {
        _paneMenuItems.Clear();

        var resourcesWithChildren = _resourceByName.Values
            .Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources))
            .Where(r => _resourceByName.Values.Any(nested => string.Equals(nested.GetResourcePropertyValue(KnownProperties.Resource.ParentName), r.Name, StringComparisons.ResourceName)))
            .ToList();

        if (resourcesWithChildren.Count > 0)
        {
            var hasCollapsed = resourcesWithChildren.Any(r => _collapsedResourceNames.Contains(r.PersistentKey));
            _paneMenuItems.Add(new MenuButtonItem
            {
                Text = hasCollapsed ? ResourcesLoc[nameof(Resources.Resources.ResourceExpandAllChildren)] : ResourcesLoc[nameof(Resources.Resources.ResourceCollapseAllChildren)],
                Icon = hasCollapsed ? new Icons.Regular.Size16.Eye() : new Icons.Regular.Size16.EyeOff(),
                OnClick = async () =>
                {
                    foreach (var resource in resourcesWithChildren)
                    {
                        if (hasCollapsed)
                        {
                            _collapsedResourceNames.Remove(resource.PersistentKey);
                        }
                        else
                        {
                            _collapsedResourceNames.Add(resource.PersistentKey);
                        }
                    }

                    await PersistCollapsedResourceNamesAsync();
                    UpdatePaneMenuItems();
                    StateHasChanged();
                }
            });
        }

        CommonMenuItems.AddToggleHiddenResourcesMenuItem(
            _paneMenuItems,
            ControlsStringsLoc,
            _filter.ShowHiddenResources,
            _resourceByName.Values,
            SessionStorage,
            EventCallback.Factory.Create<bool>(this, value =>
            {
                _filter.ShowHiddenResources = value;
                EnsureGroupOrder();
                UpdatePaneMenuItems();
            }));
    }

    // The density is set in the settings panel, which isn't part of this layout.
    private void OnViewPreferencesChanged() => InvokeAsync(StateHasChanged);

    private bool IsCompact => ViewPreferences.Density == ResourceListDensity.Compact;
    private bool IsSpacious => ViewPreferences.Density == ResourceListDensity.Spacious;

    // How long ago a resource started, for the detail line of a spacious row, and the same with the exact time for its tooltip.
    private string? GetStartedAge(ResourceViewModel resource) =>
        resource.StartTimeStamp is { } startTimeStamp
            ? Aspire.Dashboard.Components.Controls.DashboardRunSelect.FormatRunAge(new DateTimeOffset(startTimeStamp), TimeProvider.GetUtcNow(), Loc)
            : null;

    private string? GetStartedTooltip(ResourceViewModel resource) =>
        resource.StartTimeStamp is { } startTimeStamp
            ? $"{GetStartedAge(resource)} ({FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, startTimeStamp)})"
            : null;

    // Spacious rows show the age, so re-render now and then to keep it current.
    private async Task RefreshAgesPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsSpacious)
                {
                    await InvokeAsync(StateHasChanged).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static IEnumerable<CommandViewModel> GetHighlightedCommands(ResourceViewModel resource) => resource.Commands
        .Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden)
        .Take(DashboardUIHelpers.MaxHighlightedCommands);

    private bool IsCommandDisabled(ResourceViewModel resource, CommandViewModel command) =>
        DashboardClient.IsReadOnly || command.State == CommandViewModelState.Disabled || DashboardCommandExecutor.IsExecuting(resource.Name, command.Name);

    private List<MenuButtonItem> GetResourceMenuItems(ResourceViewModel resource)
    {
        var items = new List<MenuButtonItem>();
        if (CanViewResourceTerminal(resource))
        {
            items.Add(new MenuButtonItem
            {
                Text = TerminalLoc[nameof(TerminalStrings.TerminalTitle)],
                Icon = new Icons.Regular.Size20.WindowConsole(),
                OnClick = () =>
                {
                    NavigateToResourceTerminal(resource);
                    return Task.CompletedTask;
                }
            });
        }

        ResourceMenuBuilder.AddMenuItems(
            items,
            resource,
            _resourceByName,
            onViewDetails: EventCallback.Empty,
            EventCallback.Factory.Create<CommandViewModel>(this, command => ExecuteResourceCommandAsync(resource, command)),
            (r, command) => DashboardCommandExecutor.IsExecuting(r.Name, command.Name),
            showViewDetails: false,
            showTerminalItem: false,
            showConsoleLogsItem: false,
            showUrls: false);
        return items;
    }

    private bool CanViewResourceTerminal(ResourceViewModel resource) =>
        DashboardClient.IsEnabled && !DashboardClient.IsReadOnly && ResourceSelectHelpers.HasUsableTerminal(resource);

    private string GetResourceTerminalUrl(ResourceViewModel resource)
    {
        var location = new Uri(NavigationManager.Uri);
        var query = HttpUtility.ParseQueryString(location.Query);
        // A row shortcut selects this resource alone, even from an aggregate page such as
        // /resources?resource=api&resource=worker&pane=tags#output. Keep the remaining view state.
        query.Remove(DashboardUrls.ResourceSelectionQueryName);
        var url = DashboardUrls.ResourceOverviewUrl(GetResourceName(resource)) +
            (query.Count > 0 ? $"?{query}" : string.Empty) + location.Fragment;
        return DashboardUrls.SetResourceTerminalFocus(url, focused: true);
    }

    private void NavigateToResourceTerminal(ResourceViewModel resource)
    {
        CloseDrawer();
        NavigationManager.NavigateTo(GetResourceTerminalUrl(resource));
    }

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
    {
        if (resource.IsParameter &&
            string.Equals(command.Name, CommandViewModel.SetParameterCommand, StringComparisons.CommandName))
        {
            NavigationManager.NavigateTo(DashboardUrls.ResourceOverviewUrl(GetResourceName(resource)));
            return Task.CompletedTask;
        }

        return DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);
    }

    internal string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    private string GetResourceTypeLabel(ResourceViewModel resource) => resource.GetResourceTypeDisplayName(ResourcesLoc);

    private string GetResourceAccessibilityDescription(ResourceViewModel resource, string stateText, int errorCount)
    {
        List<string> descriptions = [stateText, GetResourceTypeLabel(resource)];
        if (resource.IsResourceHidden(showHiddenResources: false))
        {
            descriptions.Add(ColumnsLoc[nameof(Columns.HiddenResourceIconTooltip)]);
        }
        if (errorCount > 0)
        {
            descriptions.Add(string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.ResourcePaneUnviewedErrors)], errorCount));
        }

        return string.Join("; ", descriptions);
    }

    private bool HasMultipleReplicas(ResourceViewModel resource)
        => _resourceByName.Values.Count(r => string.Equals(r.DisplayName, resource.DisplayName, StringComparisons.ResourceName)) > 1;

    private OtlpResource? GetTelemetryResource(ResourceViewModel resource) => TelemetryRepository.GetResourceByCompositeName(resource.Name);

    internal int GetUnviewedErrorCount(ResourceViewModel resource)
    {
        return _unviewedErrorCounts is not null &&
            GetTelemetryResource(resource) is { } otlpResource &&
            _unviewedErrorCounts.TryGetValue(otlpResource.ResourceKey, out var count)
            ? count
            : 0;
    }

    private TelemetryOnlyResource? SelectedTelemetryOnlyResource =>
        _selectedItems is [{ TelemetryOnly: { } telemetryOnly }] ? telemetryOnly : null;

    /// <summary>
    /// Gets the telemetry keys for a multi-resource selection, or all sources in the current pane when there is
    /// no selection. A single resource is filtered by the page's resource key.
    /// </summary>
    internal IReadOnlyList<ResourceKey>? GetSelectionTelemetryKeys(IReadOnlyCollection<string>? resourceNames)
    {
        if (resourceNames is null || resourceNames.Count == 0)
        {
            resourceNames = _selectedResourceNames;
        }

        if (resourceNames.Count == 0)
        {
            return GetPaneTelemetryKeys();
        }

        if (resourceNames.Count < 2)
        {
            var name = resourceNames.First();
            var hasTelemetry = IsTelemetryPane
                ? _telemetryOnlyResources.Any(r => string.Equals(r.Name, name, StringComparisons.ResourceName))
                : ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource) && GetTelemetryResource(resource) is not null;
            return hasTelemetry ? null : [];
        }

        var keys = new List<ResourceKey>();
        foreach (var name in resourceNames)
        {
            OtlpResource? otlpResource = null;
            if (ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource))
            {
                otlpResource = GetTelemetryResource(resource);
            }
            else if (_telemetryOnlyResources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparisons.ResourceName)) is { } telemetryOnly)
            {
                otlpResource = telemetryOnly.Resource;
            }

            if (otlpResource is not null && !keys.Contains(otlpResource.ResourceKey))
            {
                keys.Add(otlpResource.ResourceKey);
            }
        }

        return keys;
    }

    internal IReadOnlyList<ResourceKey> GetPaneTelemetryKeys()
    {
        if (IsTelemetryPane)
        {
            return _telemetryOnlyResources.Select(r => r.Resource.ResourceKey).Distinct().ToList();
        }

        // "All resources" covers the kinds the pane lists. Parameters don't send telemetry.
        IEnumerable<ResourceKey> keys = _showResources
            ? _resourceByName.Values.Where(r => !r.IsParameter).Select(GetTelemetryResource).OfType<OtlpResource>().Select(r => r.ResourceKey)
            : [];
        if (_showTelemetryOnlyResources)
        {
            keys = keys.Concat(_telemetryOnlyResources.Select(r => r.Resource.ResourceKey));
        }

        return keys.Distinct().ToList();
    }

    internal bool IsKindShown(ResourcePaneKind kind) => kind switch
    {
        ResourcePaneKind.Resources => _showResources,
        ResourcePaneKind.Parameters => _showParameters,
        _ => _showTelemetryOnlyResources
    };

    /// <summary>
    /// Gets the names of the app model resources that "all resources" covers, or null when it covers every one of
    /// them, which is the case with both resources and parameters shown.
    /// </summary>
    internal IReadOnlyList<string>? GetScopeResourceNames()
    {
        if (_showResources && _showParameters)
        {
            return null;
        }

        return [.. _resourceByName.Values
            .Where(r => r.IsParameter ? _showParameters : _showResources)
            .Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources))
            .Select(GetResourceName)];
    }

    /// <summary>
    /// Gets the title for the page when nothing is selected, naming the kinds of items it covers.
    /// </summary>
    private string GetScopeTitle()
    {
        if (!DashboardClient.IsEnabled)
        {
            return Loc[nameof(Resources.Layout.TelemetryHeaderAllSources)];
        }

        if (_showResources && _showParameters)
        {
            return Loc[nameof(Resources.Layout.ResourceHeaderAllResources)];
        }

        List<string> kinds = [];
        if (_showResources)
        {
            kinds.Add(ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesHeader)]);
        }
        if (_showParameters)
        {
            kinds.Add(Loc[nameof(Resources.Layout.NavMenuParametersTab)]);
        }
        if (_showTelemetryOnlyResources)
        {
            kinds.Add(Loc[nameof(Resources.Layout.NavMenuTelemetrySourcesTab)]);
        }

        return kinds.Count > 0 ? string.Join(", ", kinds) : Loc[nameof(Resources.Layout.ResourcePaneNoKindsShown)];
    }

    private ResourcePaneKind GetKind(string name) =>
        ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource)
            ? (resource.IsParameter ? ResourcePaneKind.Parameters : ResourcePaneKind.Resources)
            : ResourcePaneKind.TelemetrySources;

    private async Task ToggleKindAsync(ResourcePaneKind kind)
    {
        switch (kind)
        {
            case ResourcePaneKind.Resources:
                _showResources = !_showResources;
                break;
            case ResourcePaneKind.Parameters:
                _showParameters = !_showParameters;
                break;
            default:
                _showTelemetryOnlyResources = !_showTelemetryOnlyResources;
                break;
        }

        // Showing resources again adds row actions that may not fit the current width.
        _fitPaneAfterInitialRender = true;

        var shown = new List<string>();
        if (_showResources)
        {
            shown.Add(nameof(ResourcePaneKind.Resources));
        }
        if (_showParameters)
        {
            shown.Add(nameof(ResourcePaneKind.Parameters));
        }
        if (_showTelemetryOnlyResources)
        {
            shown.Add(nameof(ResourcePaneKind.TelemetrySources));
        }
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneKinds, string.Join(',', shown));

        // A kind that's switched off is no longer part of the selection.
        var remaining = _selectedResourceNames.Where(name => IsKindShown(GetKind(name))).ToList();
        if (remaining.Count != _selectedResourceNames.Count)
        {
            if (remaining.Count == 0)
            {
                await OnAllResourcesClickAsync();
            }
            else
            {
                NavigationManager.NavigateTo(GetSelectionUrl(remaining));
            }
        }

        ResourcesChanged?.Invoke();
        StateHasChanged();
    }

    private string GetPaneCountText(int resourceCount, int parameterCount, int telemetrySourceCount)
    {
        var resourceCountText = resourceCount > 0
            ? string.Format(CultureInfo.CurrentCulture,
                Loc[resourceCount == 1 ? nameof(Resources.Layout.ResourcePaneCountSingular) : nameof(Resources.Layout.ResourcePaneCountPlural)],
                resourceCount)
            : null;
        var parameterCountText = parameterCount > 0
            ? string.Format(CultureInfo.CurrentCulture,
                Loc[parameterCount == 1 ? nameof(Resources.Layout.ResourcePaneParameterCountSingular) : nameof(Resources.Layout.ResourcePaneParameterCountPlural)],
                parameterCount)
            : null;
        var telemetrySourceCountText = telemetrySourceCount > 0
            ? string.Format(CultureInfo.CurrentCulture,
                Loc[telemetrySourceCount == 1 ? nameof(Resources.Layout.TelemetryPaneCountSingular) : nameof(Resources.Layout.TelemetryPaneCountPlural)],
                telemetrySourceCount)
            : null;

        var counts = new[] { resourceCountText, parameterCountText, telemetrySourceCountText }
            .OfType<string>()
            .ToList();
        if (counts.Count == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.ResourcePaneCountPlural)], 0);
        }

        return counts.Skip(1).Aggregate(
            counts[0],
            (combined, count) => string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.ResourcePaneMixedCount)], combined, count));
    }

    /// <summary>
    /// Gets a value indicating whether an app model resource is one of the named resources. Names are either the
    /// resource's unique name or, for resources without replicas, its display name.
    /// </summary>
    internal static bool IsResourceInSelection(ResourceViewModel resource, IReadOnlyCollection<string> resourceNames) =>
        resourceNames.Contains(resource.Name, StringComparers.ResourceName) ||
        resourceNames.Contains(resource.DisplayName, StringComparers.ResourceName);

    /// <summary>
    /// Adds the current multi-resource selection to a page URL, so changing page state such as filters keeps the
    /// selection. A single selected resource is already part of the page's URL path.
    /// </summary>
    internal string AddSelectionToUrl(string url) => AddPaneToUrl(IsMultiSelection ? DashboardUrls.AddResourceSelection(url, _selectedResourceNames) : url);

    internal bool IsTabAvailable(ResourceTab tab) => IsTabAvailable(tab, _selectedItems);

    private bool IsTabAvailable(ResourceTab tab, IReadOnlyList<SelectedResourceItem> selection)
    {
        if (tab == ResourceTab.Terminal)
        {
            return !IsTelemetryPane && selection is [{ Resource: { } resource }] && CanViewResourceTerminal(resource);
        }

        if (tab == ResourceTab.Parameters)
        {
            return DashboardClient.IsEnabled &&
                selection.Count == 0 &&
                _resourceByName.Values.Any(r => r.IsParameter);
        }

        if (IsTelemetryPane && tab is ResourceTab.Overview or ResourceTab.Console)
        {
            return false;
        }

        if (selection.Count == 0)
        {
            // Without a selection the telemetry pages list every resource, so all of them are available.
            return true;
        }

        // With several selected resources, a tab is available when any of them supports it.
        return selection.Any(item => IsTabAvailable(tab, item));
    }

    private bool IsTabAvailable(ResourceTab tab, SelectedResourceItem item)
    {
        if (item.Resource is { } resource)
        {
            if (resource.IsParameter)
            {
                return tab == ResourceTab.Overview;
            }

            var telemetryResource = GetTelemetryResource(resource);
            return tab switch
            {
                ResourceTab.Overview or ResourceTab.Console => true,
                ResourceTab.Traces => telemetryResource is not null,
                _ => telemetryResource is { UninstrumentedPeer: false }
            };
        }

        if (item.TelemetryOnly is not null)
        {
            return tab is ResourceTab.StructuredLogs or ResourceTab.Traces or ResourceTab.Metrics;
        }

        // The resource isn't known (yet). Keep the tab so the page can report it.
        return tab == CurrentTab;
    }

    internal static string GetTabUrl(ResourceTab tab, IReadOnlyList<string> resourceNames, string? activeResourceName = null)
    {
        if (resourceNames.Count > 1)
        {
            // The metrics page displays one resource's instruments at a time, and keeps the displayed resource in
            // the URL path.
            var url = tab switch
            {
                ResourceTab.Overview => DashboardUrls.ResourceOverviewUrl(),
                ResourceTab.Parameters => DashboardUrls.ParametersUrl(),
                ResourceTab.Console => DashboardUrls.ConsoleLogsUrl(),
                ResourceTab.Terminal => DashboardUrls.TerminalsUrl(),
                ResourceTab.StructuredLogs => DashboardUrls.StructuredLogsUrl(),
                ResourceTab.Traces => DashboardUrls.TracesUrl(),
                ResourceTab.Metrics => DashboardUrls.MetricsUrl(activeResourceName is not null && resourceNames.Contains(activeResourceName, StringComparers.ResourceName) ? activeResourceName : null),
                _ => throw new InvalidOperationException($"Unexpected tab: {tab}")
            };

            return DashboardUrls.AddResourceSelection(url, resourceNames);
        }

        var resourceName = resourceNames.Count == 1 ? resourceNames[0] : null;
        return tab switch
        {
            ResourceTab.Overview => DashboardUrls.ResourceOverviewUrl(resourceName),
            ResourceTab.Parameters => resourceName is null
                ? DashboardUrls.ParametersUrl()
                : DashboardUrls.AddResourceSelection(DashboardUrls.ParametersUrl(), [resourceName]),
            ResourceTab.Console => DashboardUrls.ConsoleLogsUrl(resourceName),
            ResourceTab.Terminal => resourceName is null ? DashboardUrls.TerminalsUrl() :
                DashboardUrls.SetResourceTerminalFocus(DashboardUrls.ResourceOverviewUrl(resourceName), focused: true),
            ResourceTab.StructuredLogs => DashboardUrls.StructuredLogsUrl(resourceName),
            ResourceTab.Traces => DashboardUrls.TracesUrl(resourceName),
            ResourceTab.Metrics => DashboardUrls.MetricsUrl(resourceName),
            _ => throw new InvalidOperationException($"Unexpected tab: {tab}")
        };
    }

    /// <summary>
    /// Gets the URL that selects the given resources on the current tab, falling back to the overview when none of
    /// the resources support the current tab. Metrics is kept for any selection but a single parameter.
    /// </summary>
    private string GetSelectionUrl(IReadOnlyList<string> resourceNames)
    {
        var selection = resourceNames.Select(name =>
        {
            if (ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource))
            {
                return new SelectedResourceItem(name, resource, TelemetryOnly: null);
            }

            return new SelectedResourceItem(name, Resource: null, _telemetryOnlyResources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparisons.ResourceName)));
        }).ToList();

        ResourceTab tab;
        if (IsTabAvailable(CurrentTab, selection))
        {
            tab = CurrentTab;
        }
        else if (CurrentTab == ResourceTab.Metrics && selection is not [{ Resource: { IsParameter: true } }])
        {
            // Changing the selection on the metrics page never leaves it. The page reports resources without metrics.
            tab = ResourceTab.Metrics;
        }
        else if (selection is [{ Resource: { IsParameter: true } }])
        {
            tab = ResourceTab.Overview;
        }
        else if (!IsTelemetryPane && (selection.Any(i => i.Resource is not null) || selection.Count == 0))
        {
            tab = ResourceTab.Overview;
        }
        else
        {
            // Resources that only send telemetry don't have an overview.
            tab = ResourceTab.StructuredLogs;
        }

        return GetTabUrl(tab, resourceNames, RouteResourceName);
    }

    /// <summary>
    /// Gets the URL a resource row links to, which selects only that resource.
    /// </summary>
    private string GetRowUrl(string resourceName) => GetSelectionUrl([resourceName]);

    /// <summary>
    /// Handles a click on a resource row. A click selects the resource, Ctrl+click (Cmd+click on macOS) adds or
    /// removes it from the selection, and Shift+click selects the range of rows between the last clicked row and
    /// this one.
    /// </summary>
    private async Task OnRowClickAsync(MouseEventArgs e, string resourceName)
    {
        // The selection can also change by navigating, for example with a link to a resource. Ranges then start at
        // the last selected resource.
        var anchor = _selectionAnchor is not null && IsSelected(_selectionAnchor) ? _selectionAnchor : _selectedResourceNames is [.., var last] ? last : null;

        IReadOnlyList<string> selection;
        if (e.ShiftKey && anchor is not null && GetVisibleRowNames() is var rowNames &&
            rowNames.FindIndex(n => string.Equals(n, anchor, StringComparisons.ResourceName)) is var anchorIndex and >= 0 &&
            rowNames.FindIndex(n => string.Equals(n, resourceName, StringComparisons.ResourceName)) is var clickedIndex and >= 0)
        {
            var start = Math.Min(anchorIndex, clickedIndex);
            selection = rowNames.GetRange(start, Math.Abs(anchorIndex - clickedIndex) + 1);
            _selectionAnchor = anchor;
        }
        else if (e.CtrlKey || e.MetaKey)
        {
            var toggled = GetToggleBaseSelection();
            if (toggled.RemoveAll(n => string.Equals(n, resourceName, StringComparisons.ResourceName)) == 0)
            {
                toggled.Add(resourceName);
            }

            selection = toggled;
            _selectionAnchor = resourceName;
        }
        else
        {
            if (_selectedResourceNames is [var only] && string.Equals(only, resourceName, StringComparisons.ResourceName))
            {
                // Selecting the only selected row again deselects it, like toggling.
                await ClearSelectionAsync();
                return;
            }

            selection = [resourceName];
            _selectionAnchor = resourceName;
        }

        if (selection.Count == 0)
        {
            // Remember the cleared selection so the overview doesn't select a resource again.
            await SetStoredSelectionAsync(selection);
        }

        NavigationManager.NavigateTo(GetSelectionUrl(selection));
    }

    // A single selected resource is shown by its row alone, so a one-resource group doesn't duplicate the highlight.
    private bool IsResourceGroupSelected(List<string> groupResourceNames) =>
        groupResourceNames.Count > 1 &&
        groupResourceNames.Count == _selectedResourceNames.Count &&
        groupResourceNames.All(IsSelected);

    private async Task OnResourceGroupClick(MouseEventArgs e, List<string> groupResourceNames)
    {
        if (groupResourceNames.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> selection;
        if (e.CtrlKey || e.MetaKey)
        {
            // Like rows, a modifier click toggles the group's resources in the current selection.
            var toggled = GetToggleBaseSelection();
            if (groupResourceNames.All(n => toggled.Contains(n, StringComparers.ResourceName)))
            {
                toggled.RemoveAll(n => groupResourceNames.Contains(n, StringComparers.ResourceName));
            }
            else
            {
                toggled.AddRange(groupResourceNames.Where(n => !toggled.Contains(n, StringComparers.ResourceName)));
            }

            selection = toggled;
        }
        else
        {
            if (IsResourceGroupSelected(groupResourceNames))
            {
                await ClearSelectionAsync();
                return;
            }

            selection = groupResourceNames;
        }

        _selectionAnchor = selection is [.., var last] ? last : null;
        NavigationManager.NavigateTo(GetSelectionUrl(selection));
    }

    private async Task OnAllResourcesClickAsync()
    {
        _selectionAnchor = null;

        // Remember the cleared selection so entering the overview later doesn't select a resource again.
        await SetStoredSelectionAsync([]);

        // On the overview, selecting everything shows the resource list. Other tabs stay where they are and list
        // every resource.
        NavigationManager.NavigateTo(DashboardClient.IsEnabled && CurrentTab == ResourceTab.Overview ? DashboardUrls.ResourceListUrl() : GetSelectionUrl([]));
    }

    /// <summary>
    /// Gets the selection a Ctrl+click (Cmd+click on macOS) adds a resource to or removes it from. "Select all" is an
    /// empty selection, and toggling a resource then starts from every resource it stands for, so the click deselects
    /// that resource instead of selecting it alone.
    /// </summary>
    private List<string> GetToggleBaseSelection()
    {
        if (!IsAllSelected)
        {
            return _selectedResourceNames.ToList();
        }

        // Resources that only send telemetry are listed in their own section, and are part of everything too.
        var names = GetAllResourceNames();
        if (DashboardClient.IsEnabled)
        {
            names.AddRange(_telemetryOnlyResources.Select(r => r.Name).Where(n => !names.Contains(n, StringComparers.ResourceName)));
        }

        return names;
    }

    /// <summary>
    /// Deselects everything without leaving the current tab, e.g. clearing a selection on Metrics stays on Metrics.
    /// </summary>
    private async Task ClearSelectionAsync()
    {
        _selectionAnchor = null;
        await SetStoredSelectionAsync([]);
        NavigationManager.NavigateTo(GetSelectionUrl([]));
    }

    /// <summary>
    /// Gets the names of the resources that "All resources" stands for, in the order of the resource list. The pane's
    /// filters don't apply because they only narrow down the list.
    /// </summary>
    internal List<string> GetAllResourceNames()
    {
        if (!DashboardClient.IsEnabled)
        {
            return _telemetryOnlyResources.Select(r => r.Name).ToList();
        }

        var names = _resourceByName.Values
            .Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources))
            .OrderBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .Select(GetResourceName)
            .ToList();

        return names;
    }

    private List<string> GetVisibleRowNames()
    {
        List<string> names;
        if (DashboardClient.IsEnabled && _grouping != ResourcePaneGrouping.None)
        {
            // A resource with several tags is listed several times. Ranges use its first position.
            names = GetResourceGroups()
                .Where(g => !IsGroupCollapsed(g))
                .SelectMany(g => g.Resources)
                .Select(GetResourceName)
                .Distinct(StringComparers.ResourceName)
                .ToList();
        }
        else
        {
            names = IsSectionCollapsed(ResourcesSectionKey)
                ? []
                : GetPaneRows().Select(r => GetResourceName(r.Resource)).ToList();

            if (!IsSectionCollapsed(ParametersSectionKey))
            {
                names.AddRange(GetParameterPaneRows().Select(GetResourceName));
            }
        }

        if (!IsSectionCollapsed(TelemetrySectionKey))
        {
            names.AddRange(GetVisibleTelemetryResources().Select(r => r.Name));
        }
        return names;
    }

    private bool IsSelected(string resourceName) => _selectedResourceNames.Contains(resourceName, StringComparers.ResourceName);

    // An empty selection means everything is selected, so every row is shown selected. Only the look follows: the
    // rows aren't each announced as current, "Select all" being pressed already says it.
    private bool IsAllSelected => _selectedResourceNames.Count == 0;

    private string? GetRowAriaCurrent(bool isSelected) => isSelected ? (IsMultiSelection ? "true" : "page") : null;

    private static string GetTabId(ResourceTab tab) => $"tab-{tab}";

    private void OnTabChanged(FluentTab? newTab, bool isDesktop, string? renderedLocation)
    {
        // FluentTabs also raises this when a tab is disposed, for example when leaving the resources view, and
        // when it syncs with ActiveTabId after a navigation. Only a change made while this layout shows the current
        // URL is a user selecting a tab.
        // Switching between the live view and a recording re-creates the page, and the outgoing layout's tabs
        // raise changes while they're torn down, which would otherwise navigate to another tab.
        if (_isDisposed
            || isDesktop != ViewportInformation.IsDesktop
                // A delayed event from the previous header must not navigate the newly selected resource.
            || !string.Equals(renderedLocation, _lastLocation, StringComparison.Ordinal)
            || !string.Equals(NavigationManager.Uri, _lastLocation, StringComparison.Ordinal)
            || newTab?.Id is not { } id
            || !id.StartsWith("tab-", StringComparison.Ordinal)
            || !Enum.TryParse<ResourceTab>(id["tab-".Length..], out var tab)
            || tab == CurrentTab
            // A navigation started from the tab strip is still in flight. The layout re-renders when this handler
            // returns, before the router reaches the new location, and that render must not put the previous tab
            // back: the strip would report it as a change once the new page shows, and navigate back to it, forever.
            || _navigatingToTab is not null)
        {
            return;
        }

        // Tabs map to pages, so a tab change is a navigation. The tab URL keeps the current resource selection.
        if (GetTabs().FirstOrDefault(t => t.Tab == tab && t.IsVisible)?.Href is { } href)
        {
            _navigatingToTab = tab;
            NavigationManager.NavigateTo(href);
        }
    }

    private IEnumerable<TabItem> GetTabs()
    {
        if (CurrentTab == ResourceTab.Parameters)
        {
            yield return CreateTab(ResourceTab.Parameters, Loc[nameof(Resources.Layout.NavMenuParametersTab)], new Icons.Regular.Size24.Key(), isVisible: true);
            yield break;
        }

        var resource = SelectedResource;
        var isTelemetryOnly = SelectedTelemetryOnlyResource is not null;
        var isMultiSelection = IsMultiSelection;

        // Every tab is always rendered and hidden when it doesn't apply. Removing a FluentTab makes FluentTabs
        // activate its first tab, which would navigate away from the current page.
        var errorCount = _selectedItems.Sum(i => i.Resource is { } r ? GetUnviewedErrorCount(r) : 0);
        yield return CreateTab(ResourceTab.Overview, Loc[nameof(Resources.Layout.ResourceTabOverview)], new Icons.Regular.Size24.Board(),
            isVisible: DashboardClient.IsEnabled && !isTelemetryOnly && (resource is not null || isMultiSelection || _selectedItems.Count == 0));
        yield return CreateTab(ResourceTab.Console, Loc[nameof(Resources.Layout.NavMenuConsoleLogsTab)], new Icons.Regular.Size24.SlideText(),
            isVisible: DashboardClient.IsEnabled && !isTelemetryOnly);
        yield return CreateTab(ResourceTab.Terminal, TerminalLoc[nameof(TerminalStrings.TerminalTitle)], new Icons.Regular.Size20.WindowConsole(),
            isVisible: IsTabAvailable(ResourceTab.Terminal) || CurrentTab == ResourceTab.Terminal);
        yield return CreateTab(ResourceTab.StructuredLogs, StructuredLogsLoc[nameof(Resources.StructuredLogs.StructuredLogsHeader)], new Icons.Regular.Size24.SlideTextSparkle(),
            isVisible: true, errorCount);
        yield return CreateTab(ResourceTab.Traces, Loc[nameof(Resources.Layout.NavMenuTracesTab)], new Icons.Regular.Size24.GanttChart(),
            isVisible: true);
        yield return CreateTab(ResourceTab.Metrics, Loc[nameof(Resources.Layout.NavMenuMetricsTab)], new Icons.Regular.Size24.ChartMultiple(),
            isVisible: true);

        TabItem CreateTab(ResourceTab tab, string text, Icon icon, bool isVisible, int errorCount = 0)
        {
            // Tabs are never disabled. A tab that doesn't apply to the selection still navigates, and the page explains why it is empty.
            return new TabItem(tab, text, icon, isVisible, GetTabUrl(tab), errorCount, IsTabAvailable(tab));
        }
    }

    // Mirrors GetTabs() as a dropdown for narrow layouts, where the FluentTabs row no longer fits and would
    // otherwise need its own horizontal scrollbar (see the "resource-tabs-nav" container query in the stylesheet).
    private IList<MenuButtonItem> GetTabMenuItems()
    {
        var items = new List<MenuButtonItem>();

        foreach (var tab in GetTabs())
        {
            if (!tab.IsVisible)
            {
                continue;
            }

            items.Add(new MenuButtonItem
            {
                Id = $"{GetTabId(tab.Tab)}-menu-item",
                Class = "resource-tab-menu-item",
                Text = tab.Text,
                StartIcon = tab.Icon,
                Role = MenuItemRole.Radio,
                Checked = tab.Tab == CurrentTab,
                Tooltip = tab.IsAvailable ? null : Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value,
                OnClick = () => { NavigationManager.NavigateTo(tab.Href); return Task.CompletedTask; }
            });
        }

        return items;
    }

    internal static string GetStateClass(ResourceViewModel resource) =>
        resource.IsParameter && resource.HasMissingParameterValueState()
            ? "state-warning"
            : resource.KnownState switch
    {
        KnownResourceState.Running when resource.HealthStatus is Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy => "state-warning",
        KnownResourceState.Running => "state-success",
        KnownResourceState.FailedToStart or KnownResourceState.RuntimeUnhealthy => "state-error",
        KnownResourceState.Exited when resource.TryGetExitCode(out var exitCode) && exitCode is not 0 => "state-error",
        KnownResourceState.Starting or KnownResourceState.Building or KnownResourceState.Waiting or KnownResourceState.Stopping => "state-pending",
        _ => "state-neutral"
    };

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        _cts.Cancel();
        TagStore.Changed -= OnTagsChanged;
        ViewPreferences.Changed -= OnViewPreferencesChanged;
        OrderStore.Changed -= OnOrderChanged;
        _logsSubscription?.Dispose();
        _telemetryResourcesSubscription?.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_ageRefreshTask);
        _cts.Dispose();

        if (_jsModule is { } module)
        {
            try
            {
                await module.InvokeVoidAsync("unregisterPaneResizer", _layoutElement);
                await module.InvokeVoidAsync("unregisterResourceOrdering", _layoutElement);
                await module.InvokeVoidAsync("unregisterTabOverflow");
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone, so the browser has already discarded the handlers.
            }

            await JSInteropHelpers.SafeDisposeAsync(module);
        }

        _selfRef?.Dispose();
    }

    internal sealed record ResourceLocation(ResourceTab Tab, string? RouteResourceName, IReadOnlyList<string> SelectedResourceNames, bool KeepSelection);

    private sealed record TelemetryOnlyResource(string Name, OtlpResource Resource);

    private sealed record SelectedResourceItem(string Name, ResourceViewModel? Resource, TelemetryOnlyResource? TelemetryOnly);

    private sealed record ResourceGroup(string? Value, List<ResourceViewModel> Resources);

    private sealed record TabItem(ResourceTab Tab, string Text, Icon Icon, bool IsVisible, string Href, int ErrorCount, bool IsAvailable);
}

// These enums are top-level rather than nested in ResourcesLayout because pages reference the layout through
// [Layout(typeof(ResourcesLayout))], which preserves all of the layout's members for trimming. Native AOT reports
// every preserved nested enum as a use of Enum.GetValues(Type) (IL3050) on each page that uses the layout.

internal enum ResourceTab
{
    Overview,
    Parameters,
    Console,
    Terminal,
    StructuredLogs,
    Traces,
    Metrics
}

internal enum ResourcePaneKind
{
    Resources,
    Parameters,
    TelemetrySources
}

internal enum ResourcePaneGrouping
{
    None,
    Tags,
    ResourceType,
    State
}
