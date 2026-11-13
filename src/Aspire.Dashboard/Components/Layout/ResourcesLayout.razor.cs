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
    private const string ResourcePickerButtonId = "resourcePanePickerButton";
    // The list has layouts down to this width (see the resource-pane container queries in ResourcesLayout.razor.css):
    // the type column narrows and the lowest-priority row actions give way first. It opens wider, at the default.
    internal const int MinimumPaneWidthPx = 300;
    internal const int MaximumPaneWidthPx = 560;
    internal const int DefaultPaneWidthPx = 420;
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
    private readonly string _tabOverflowRegistrationId = Guid.NewGuid().ToString("N");
    // Blazor compares event handlers and delegates by reference when it diffs a render. A lambda created for each row
    // on each render would make every row's handlers, buttons and menus look changed, so each resource's handlers
    // are created once and reused across renders (see RowHandlers). They're keyed by the resource's unique name, not
    // the name a row shows: a display name can equal another resource's unique name.
    private readonly Dictionary<string, RowHandlers> _rowHandlers = new(StringComparers.ResourceName);
    // Telemetry-only rows have no resource, only a name to select, and are kept apart so they can't share an entry
    // with a resource whose unique name is the same.
    private readonly Dictionary<string, EventCallback<MouseEventArgs>> _telemetryRowClicks = new(StringComparers.ResourceName);
    // Resources whose display name is shared by another resource, such as replicas, are shown by their unique name.
    // The set is rebuilt lazily when the resources change, so resolving a name doesn't scan every resource.
    private DuplicateDisplayNames _duplicateDisplayNames = new(Version: -1, Names: new(StringComparers.ResourceName));
    private int _resourceVersion;

    private Task? _resourceSubscriptionTask;
    private Task? _ageRefreshTask;
    private Subscription? _logsSubscription;
    private Subscription? _telemetryResourcesSubscription;
    private Dictionary<ResourceKey, int>? _unviewedErrorCounts;
    private Dictionary<string, OtlpResource> _telemetryResourcesByName = new(StringComparers.ResourceName);
    private Dictionary<ResourceKey, ResourceSignalAvailability> _resourceSignalAvailability = [];
    private List<TelemetryOnlyResource> _telemetryOnlyResources = [];
    private string? _collapsedResourceNamesKey;
    private bool _isLoaded;
    private bool _isPaneCollapsed;
    private bool _isDrawerOpen;
    private bool _isPaneFullCanvas;
    private string? _fullCanvasNavigation;

    /// <summary>
    /// History entry state for a navigation to the dashboard's home: the unscoped resources URL opened as the
    /// full-width resource list rather than the overview summary.
    /// </summary>
    internal const string ResourceListHomeHistoryEntryState = "aspire-resource-list-home";
    private bool _animatePaneTransition;
    private bool _isPaneSelectionReveal;
    private Task? _paneInteractionsRegistration;
    private string? _animatedPaneNavigation;
    private string? _startPaneSelectionReveal;
    private string? _focusPaneSelectionReveal;
    private AspireMenu? _resourceContextMenu;
    private IReadOnlyList<MenuButtonItem> _resourceContextMenuItems = [];
    private bool _isResourcePickerOpen;
    private string? _resourcePickerSelectionLocation;
    private bool _wasResourcePickerOpen;
    private bool _focusResourcePicker;
    private FluentTextInput? _resourcePaneSearch;
    // Whether the pane lists the resources that only send telemetry, and so whether "all resources" covers them. It's
    // the "Telemetry sources" option at the end of the filter's resource types. Like the filter's other options, it
    // starts on, so an unfiltered list shows everything.
    private bool _showTelemetryOnlyResources = true;
    // The name telemetry sources are stored under in BrowserStorageKeys.ResourcePaneKinds.
    private const string TelemetrySourcesKind = "TelemetrySources";
    private bool _pendingLandingRedirect;
    // Whether the landing redirect is for the first location this layout shows, which is the dashboard opening.
    private bool _isInitialLanding;
    // Whether the resource content was open, as last stored for this browser tab (see PersistContentOpenAsync).
    private bool? _storedContentOpen;
    private bool _isDecidingLanding;
    private bool _hasLocation;
    private string? _lastLocation;
    // The tab a click on the tab strip is navigating to, until the new location arrives. See OnTabChanged.
    private ResourceTab? _navigatingToTab;
    // A close (the page's close button or Escape) clears the stored selection before it navigates to the parent, so
    // there's a moment when it has started but the location hasn't changed. A tab chosen then waits for the close and
    // opens for the parent's selection, rather than racing it with a URL for the resource being closed.
    private string? _closingToLocation;
    private ResourceTab? _tabAfterClose;
    private bool _isDisposed;
    private IReadOnlyList<string> _selectedResourceNames = [];
    private List<SelectedResourceItem> _selectedItems = [];
    private string? _selectionAnchor;
    private int? _paneWidth;
    private ElementReference _layoutElement;
    private IJSObjectReference? _jsModule;
    private bool? _usesCommandSelectionModifier;
    private bool _isTabOverflowRegistered;
    private string[] _overflowTabIds = [];
    private DotNetObjectReference<ResourcesLayout>? _selfRef;
    private bool _isResizerRegistered;
    private bool _isOrderingRegistered;
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
        _paneActivityTimerTask = RefreshPaneActivityPeriodicallyAsync(cancellationToken);

        _lastLocation = NavigationManager.Uri;
        UpdateFromLocation(_lastLocation);

        // When the dashboard is first opened, "/" renders without this layout and redirects to /resources, so this
        // layout is created by the home navigation itself. OnParametersSetAsync then sees no location change and
        // wouldn't check for it, so the home check also runs here. No transition: there's no earlier view to animate from.
        if (IsResourceListHomeNavigation(_lastLocation))
        {
            SetResourceListVisible(true);
            _animatePaneTransition = false;
        }

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
        // A comma-separated list of the kinds of items the pane lists, such as "Resources,Parameters,TelemetrySources".
        // The pane used to have a toggle for each kind. Resources and parameters are now always listed (the filter's
        // types narrow them down), so only the telemetry sources entry still counts.
        var kindsResult = await LocalStorage.GetUnprotectedAsync<string>(BrowserStorageKeys.ResourcePaneKinds);
        if (kindsResult.Success && kindsResult.Value is { } storedKinds)
        {
            var kinds = storedKinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _showTelemetryOnlyResources = kinds.Contains(TelemetrySourcesKind, StringComparer.Ordinal);
        }
        await ApplyPaneModeFromLocationAsync(_lastLocation);
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        TagStore.Changed += OnTagsChanged;
        ViewPreferences.Changed += OnViewPreferencesChanged;
        OrderStore.Changed += OnOrderChanged;

        // Start reading stored tags and order now, so the reads overlap the other storage reads and the connection.
        var settingsLoaded = Task.WhenAll(TagStore.EnsureLoadedAsync(), OrderStore.EnsureLoadedAsync());

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
                if (_isDisposed || (_unviewedErrorCounts is { } current && current.Count == counts.Count &&
                    counts.All(pair => current.TryGetValue(pair.Key, out var previous) && previous == pair.Value)))
                {
                    return;
                }

                _unviewedErrorCounts = counts;
                StateHasChanged();
            });
        });

        _telemetryResourcesSubscription = TelemetryRepository.OnNewResources(async () =>
        {
            await InvokeAsync(() =>
            {
                UpdateTelemetryOnlyResources();
                // Standalone, this is the only callback: removed telemetry sources must drop their row handlers here.
                PruneRowHandlers();
                UpdateSelection();
                QueuePaneActivityRefresh();
                StateHasChanged();
                ResourcesChanged?.Invoke();
            });
        });
        _paneActivitySubscription = TelemetryRepository.OnNewTraces(null, SubscriptionType.Other, () =>
            InvokeAsync(() => QueuePaneActivityRefresh()));

        // Standalone resources come from OTLP. The disabled AppHost client cannot be awaited or subscribed to.
        if (!DashboardClient.IsEnabled)
        {
            _isLoaded = true;
            UpdateTelemetryOnlyResources();
            UpdateSelection();
            ResourcesChanged?.Invoke();
            return;
        }

        // Wait for stored tags and order before showing resources, so the list doesn't first appear in another order.
        await settingsLoaded;
        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(cancellationToken);
        foreach (var resource in snapshot)
        {
            UpsertResource(resource);
        }
        OrderStore.EnsureResources(snapshot.Where(r => !r.IsParameter)
            .OrderBy(r => r.ResourceType).ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .Select(r => r.PersistentKey));
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
                            OrderStore.EnsureResources([resource.PersistentKey]);
                        }
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                        Interlocked.Increment(ref _resourceVersion);
                    }
                }

                await InvokeAsync(async () =>
                {
                    UpdateResourceTagFilters();
                    EnsureGroupOrder();
                    UpdateTelemetryOnlyResources();
                    // After the telemetry sources are refreshed, so both kinds of row handlers are pruned against
                    // the current rows.
                    PruneRowHandlers();
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
        Interlocked.Increment(ref _resourceVersion);

        // New types and states are visible unless the user has hidden them already.
        _filter.ResourceTypesToVisibility.TryAdd(resource.ResourceType, true);
        _filter.ResourceStatesToVisibility.TryAdd(resource.State ?? string.Empty, true);
        _filter.ResourceHealthStatusesToVisibility.TryAdd(resource.HealthStatus?.Humanize() ?? string.Empty, true);
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!ViewportInformation.IsDesktop)
        {
            _isResourcePickerOpen = false;
        }
        if (!ViewportInformation.IsDesktop && (_isPaneFullCanvas || _isPaneSelectionReveal))
        {
            _isPaneFullCanvas = false;
            _animatePaneTransition = false;
            CancelPaneSelectionReveal();
            _animatedPaneNavigation = null;
            _isDrawerOpen = false;
            ClearPaneActivity();
        }

        // The router sets a new body on every navigation. Selection is updated here, rather than in a
        // LocationChanged handler, so it's current before the page in the body renders.
        var location = NavigationManager.Uri;
        if (string.Equals(location, _lastLocation, StringComparison.Ordinal))
        {
            return;
        }

        // An unscoped page can restore saved filters, e.g. /traces -> /traces?type=server.
        // That redirect must not reopen content the user just closed. Resource selections and
        // navigation to another page still leave the full-canvas list normally.
        // Browser back/forward can produce the same URL change, for example /metrics back to a
        // directly opened /metrics?meter=Test, so only the redirect's history entry marker counts.
        var previousUri = NavigationManager.ToAbsoluteUri(_lastLocation);
        var currentUri = NavigationManager.ToAbsoluteUri(location);
        var restoringPageState = _isPaneFullCanvas && IsAllSelected && previousUri.Query.Length == 0 &&
            string.Equals(NavigationManager.HistoryEntryState, PageExtensions.RestoredPageStateHistoryEntryState, StringComparison.Ordinal) &&
            string.Equals(previousUri.AbsolutePath, currentUri.AbsolutePath, StringComparisons.UrlPath) &&
            ParseLocation(NavigationManager.ToBaseRelativePath(location)).SelectedResourceNames.Count == 0;
        _lastLocation = location;
        // Only a row selection owns this reveal. Tab changes and unrelated navigation
        // cancel it rather than replaying a stale full-canvas transition.
        _animatePaneTransition = _isPaneFullCanvas && string.Equals(location, _animatedPaneNavigation, StringComparison.Ordinal);
        _isPaneSelectionReveal = _animatePaneTransition;
        _startPaneSelectionReveal = _isPaneSelectionReveal ? location : null;
        _focusPaneSelectionReveal = null;
        _animatedPaneNavigation = null;
        var showResourceList = restoringPageState || string.Equals(location, _fullCanvasNavigation, StringComparison.Ordinal) ||
            IsResourceListHomeNavigation(location);
        _isPaneFullCanvas = false;
        _fullCanvasNavigation = null;
        _closingToLocation = null;
        var tabAfterClose = _tabAfterClose;
        _tabAfterClose = null;
        ClearPaneActivity();
        _navigatingToTab = null;
        var previousTab = CurrentTab;
        var previousSelection = _selectedResourceNames;
        // Closing the menu can yield a render. Publish the new selection first so
        // its reveal cannot briefly show the previous resource's heading.
        UpdateFromLocation(location);
        if (showResourceList)
        {
            SetResourceListVisible(true);
        }
        _isDrawerOpen = !ViewportInformation.IsDesktop && showResourceList;
        // Modifier selection changes the URL without dismissing the picker. Other navigation still closes it.
        _isResourcePickerOpen = _isResourcePickerOpen && string.Equals(location, _resourcePickerSelectionLocation, StringComparison.Ordinal);
        _resourcePickerSelectionLocation = null;
        if (previousTab != CurrentTab || !previousSelection.SequenceEqual(_selectedResourceNames, StringComparers.ResourceName))
        {
            // Reused pages must replace subscriptions and terminal state before an
            // asynchronous operation can expose their previous resource's content.
            ResourcesChanged?.Invoke();
        }
        if (_resourceContextMenu is { } menu)
        {
            await menu.CloseAsync();
        }
        await ApplyPaneModeFromLocationAsync(location);
        await TryRedirectLandingAsync();
        await PersistSelectedResourcesAsync();

        // The close has landed: open the tab chosen while it was in flight, for the new selection.
        if (tabAfterClose is { } chosenTab && GetTabs().FirstOrDefault(t => t.Tab == chosenTab && t.IsVisible)?.Href is { } chosenHref)
        {
            await NavigateToTabAsync(chosenHref);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await PersistContentOpenAsync();
        var focusResourcePicker = _focusResourcePicker && _isResourcePickerOpen;
        _focusResourcePicker = false;
        var pickerChanged = _isResourcePickerOpen != _wasResourcePickerOpen;
        var showResizer = ViewportInformation.IsDesktop && !_isPaneCollapsed && !_isPaneFullCanvas;
        var registrationChanged = showResizer != _isResizerRegistered || !_isOrderingRegistered;
        if (_usesCommandSelectionModifier is not null && !pickerChanged && !focusResourcePicker &&
            !registrationChanged && _isTabOverflowRegistered &&
            _startPaneSelectionReveal is null && _focusPaneSelectionReveal is null)
        {
            return;
        }

        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./{Assets["Components/Layout/ResourcesLayout.razor.js"]}");
            var renderSelectionModifier = _usesCommandSelectionModifier is null;
            if (renderSelectionModifier)
            {
                _usesCommandSelectionModifier = await _jsModule.InvokeAsync<bool>("usesCommandSelectionModifier");
            }
            _selfRef ??= DotNetObjectReference.Create(this);
            if (pickerChanged)
            {
                await _jsModule.InvokeVoidAsync("setResourcePickerOpen", _layoutElement, _selfRef, _isResourcePickerOpen);
                _wasResourcePickerOpen = _isResourcePickerOpen;
            }
            if (focusResourcePicker && _resourcePaneSearch is { } search)
            {
                await search.Element.FocusAsync();
            }
            if (!_isTabOverflowRegistered)
            {
                _isTabOverflowRegistered = true;
                await _jsModule.InvokeVoidAsync("registerTabOverflow", _tabOverflowRegistrationId, _selfRef);
            }
            if (!_isOrderingRegistered)
            {
                // Re-renders can overlap asynchronous setup. Sharing the registration prevents
                // handlers from being replaced while they hold the initiating selection control.
                var registration = _paneInteractionsRegistration ??= RegisterPaneInteractionsAsync(_jsModule);
                try
                {
                    await registration;
                }
                catch
                {
                    // A faulted shared task would fail every later render, so the handlers could never
                    // register after the circuit reconnects. Only clear this attempt: an overlapping render
                    // may already have started a newer one.
                    if (ReferenceEquals(_paneInteractionsRegistration, registration))
                    {
                        _paneInteractionsRegistration = null;
                    }
                    throw;
                }
                _isOrderingRegistered = true;
            }
            if (registrationChanged && showResizer)
            {
                await _jsModule.InvokeVoidAsync("registerPaneResizer", _layoutElement, _selfRef, MinimumPaneWidthPx, MaximumPaneWidthPx, DefaultPaneWidthPx);
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

            if (_startPaneSelectionReveal is { } startLocation)
            {
                _startPaneSelectionReveal = null;
                await _jsModule.InvokeVoidAsync("startPaneSelectionReveal", _layoutElement, _selfRef, startLocation);
            }
            if (_focusPaneSelectionReveal is { } focusLocation)
            {
                _focusPaneSelectionReveal = null;
                // This render has removed inert. Focus only after the selected page is
                // accessible, not while its normal-width surface is still clipped.
                await _jsModule.InvokeVoidAsync("focusPaneSelectionReveal", _layoutElement, focusLocation);
            }
            if (renderSelectionModifier)
            {
                StateHasChanged();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected while rendering. There is nothing to register.
        }
    }

    private async Task RegisterPaneInteractionsAsync(IJSObjectReference jsModule)
    {
        await jsModule.InvokeVoidAsync("registerResourceOrdering", _layoutElement, _selfRef);
        await jsModule.InvokeVoidAsync("registerResourceContextMenu", _layoutElement, _selfRef);
        await jsModule.InvokeVoidAsync("registerPaneActivityVisibility", _layoutElement, _selfRef);
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
        _animatePaneTransition = false;
        CancelPaneSelectionReveal();
        _paneWidth = Math.Clamp(widthPx, MinimumPaneWidthPx, MaximumPaneWidthPx);
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneWidth, _paneWidth.Value);
        StateHasChanged();
    }

    private string? GetLayoutStyle() => _paneWidth is { } width && ViewportInformation.IsDesktop
        ? string.Create(CultureInfo.InvariantCulture, $"--resource-pane-expanded-width: {width}px")
        : null;

    // The home page is the full-width resource list, with its URLs, on desktop. The overview summary stays at the
    // same URL and opens from the list's "All resources" row, so links to /resources keep showing the summary.
    private bool IsResourceListHomeNavigation(string location)
    {
        if (!ViewportInformation.IsDesktop || !DashboardClient.IsEnabled ||
            !string.Equals(NavigationManager.HistoryEntryState, ResourceListHomeHistoryEntryState, StringComparison.Ordinal))
        {
            return false;
        }

        var parsed = ParseLocation(NavigationManager.ToBaseRelativePath(location));
        return parsed.Tab == ResourceTab.Overview && parsed.RouteResourceName is null && parsed.SelectedResourceNames.Count == 0;
    }

    internal void SetResourceListVisible(bool visible)
    {
        CancelPaneSelectionReveal();
        _animatedPaneNavigation = null;
        _animatePaneTransition = true;
        _isPaneFullCanvas = ViewportInformation.IsDesktop && visible;
        _isDrawerOpen = !ViewportInformation.IsDesktop && visible;
        if (_isPaneFullCanvas)
        {
            QueuePaneActivityRefresh(immediate: true);
        }
        else
        {
            ClearPaneActivity();
        }
        StateHasChanged();
    }

    private void CancelPaneSelectionReveal()
    {
        _isPaneSelectionReveal = false;
        _startPaneSelectionReveal = null;
        _focusPaneSelectionReveal = null;
    }

    [JSInvokable]
    public void CompletePaneSelectionReveal(string location)
    {
        // A canceled animation can finish after a second selection or browser navigation.
        // Only the reveal that still owns this location may expose or focus its details.
        if (_isDisposed || !_isPaneSelectionReveal || !string.Equals(location, _lastLocation, StringComparison.Ordinal))
        {
            return;
        }

        _isPaneSelectionReveal = false;
        _animatePaneTransition = false;
        _focusPaneSelectionReveal = location;
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OpenResourceContextMenuAsync(string resourceName, int clientX, int clientY)
    {
        if (_isDisposed || _resourceContextMenu is not { } menu || !_resourceByName.TryGetValue(resourceName, out var resource))
        {
            return;
        }

        // Use the row's actions, not the current selection: a context click must not silently
        // turn a single-resource command into a batch command or navigate to another resource.
        _resourceContextMenuItems =
        [
            new MenuButtonItem
            {
                Id = "resource-pane-context-menu-header",
                IsHeader = true,
                Text = GetResourceName(resource),
                Icon = ResourceIconHelpers.GetIconForResource(IconResolver, resource, IconSize.Size16)
            },
            .. GetResourceMenuItems(resource)
        ];
        await menu.OpenAsync(clientX, clientY);
        StateHasChanged();
    }

    private async Task OnResourceContextMenuOpenChangedAsync(bool open)
    {
        if (!open && _jsModule is { } module)
        {
            // Restore before an item callback can open a dialog; never steal focus from that dialog.
            await module.InvokeVoidAsync("restoreResourceContextMenuFocus", _layoutElement);
        }
    }

    /// <summary>
    /// Derives the tab and selected resources from a URL such as <c>/traces/resource/api?type=http</c>.
    /// </summary>
    private void UpdateFromLocation(string location)
    {
        var baseRelativePath = NavigationManager.ToBaseRelativePath(location);
        var parsedLocation = ParseLocation(baseRelativePath);
        var isFirstLocation = !_hasLocation;
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
        // The dashboard opens on /resources: the root URL redirects there (see Home). When that's the first location
        // and there's no selection to restore, the resource list fills the page instead of an overview (see
        // TryRedirectLandingAsync). Other URLs, such as /resources?pane=tags, open what they ask for.
        _isInitialLanding = isFirstLocation && _pendingLandingRedirect &&
            string.Equals("/" + baseRelativePath, DashboardUrls.ResourceListUrl(), StringComparisons.UrlPath);
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

    internal string? DetailParentUrl
    {
        get
        {
            var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
            var path = NavigationManager.ToBaseRelativePath(uri.GetLeftPart(UriPartial.Path));
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var query = HttpUtility.ParseQueryString(uri.Query);
            // "pane=tags" is a legacy grouping shortcut, not a pane mode. Replaying it on
            // parent navigation would overwrite grouping changes made in the current list.
            if (string.Equals(query["pane"], "tags", StringComparisons.UrlPath))
            {
                query.Remove("pane");
            }
            string parentUrl;
            if (CurrentTab == ResourceTab.Traces && segments.Length == 3 &&
                string.Equals(segments[1], "detail", StringComparisons.UrlPath))
            {
                parentUrl = GetTabUrl(ResourceTab.Traces);
                query.Remove("spanId");
            }
            else if (CurrentTab == ResourceTab.Metrics && segments.Length == 7 &&
                string.Equals(segments[1], "resource", StringComparisons.UrlPath) &&
                string.Equals(segments[3], "meter", StringComparisons.UrlPath) &&
                string.Equals(segments[5], "instrument", StringComparisons.UrlPath))
            {
                parentUrl = "/" + string.Join('/', segments[..5]);
                query.Remove("instrument");
            }
            else if (CurrentTab == ResourceTab.Metrics && query["instrument"] is not null)
            {
                parentUrl = "/" + path;
                query.Remove("instrument");
            }
            else if (CurrentTab == ResourceTab.Metrics &&
                (segments.Length == 5 && string.Equals(segments[1], "resource", StringComparisons.UrlPath) &&
                    string.Equals(segments[3], "meter", StringComparisons.UrlPath) || query["meter"] is not null))
            {
                parentUrl = GetTabUrl(ResourceTab.Metrics);
                query.Remove("meter");
                query.Remove("instrument");
            }
            else if (RouteResourceName is not null || CurrentTab == ResourceTab.Overview)
            {
                parentUrl = GetTabUrl(CurrentTab == ResourceTab.Terminal ? ResourceTab.Overview : CurrentTab, []);
                query.Remove(DashboardUrls.ResourceSelectionQueryName);
                query.Remove(DashboardUrls.ResourceTerminalQueryName);
            }
            else
            {
                parentUrl = "/" + path;
            }

            var parentQueryIndex = parentUrl.IndexOf('?');
            var parentQuery = HttpUtility.ParseQueryString(parentQueryIndex >= 0 ? parentUrl[(parentQueryIndex + 1)..] : string.Empty);
            foreach (var key in query.AllKeys.OfType<string>())
            {
                // Selection is encoded by the parent route. Other query values, including repeated
                // filters and the telemetry pane, keep their scope when closing a deep link.
                if (key != DashboardUrls.ResourceSelectionQueryName || parentQuery[key] is null)
                {
                    parentQuery.Remove(key);
                    foreach (var value in query.GetValues(key) ?? [])
                    {
                        parentQuery.Add(key, value);
                    }
                }
            }

            var parentPath = parentQueryIndex >= 0 ? parentUrl[..parentQueryIndex] : parentUrl;
            return AddPaneToUrl(parentPath + (parentQuery.Count > 0 ? $"?{parentQuery}" : string.Empty));
        }
    }

    private async Task CloseDetailAsync(string parentUrl)
    {
        _closingToLocation = NavigationManager.ToAbsoluteUri(parentUrl).AbsoluteUri;
        if (ParseLocation(NavigationManager.ToBaseRelativePath(NavigationManager.ToAbsoluteUri(parentUrl).AbsoluteUri)).SelectedResourceNames.Count == 0)
        {
            // An unscoped overview normally restores the previous selection. Closing explicitly
            // clears it first, including on a freshly opened resource detail link.
            await SetStoredSelectionAsync([]);
        }

        var target = NavigationManager.ToAbsoluteUri(parentUrl).AbsoluteUri;
        if (string.Equals(target, NavigationManager.Uri, StringComparison.Ordinal))
        {
            // Closing an unscoped tab changes presentation without changing its URL, so it's done: nothing will land.
            _closingToLocation = null;
            SetResourceListVisible(true);
            if (_tabAfterClose is { } chosenTab)
            {
                _tabAfterClose = null;
                await OpenChosenTabAsync(chosenTab);
            }
        }
        else
        {
            _fullCanvasNavigation = target;
        }
    }

    internal string GetTraceDetailUrl(string traceId, string? spanId = null)
    {
        var url = DashboardUrls.AddResourceSelection(DashboardUrls.TraceDetailUrl(traceId, spanId), _selectedResourceNames);
        if (CurrentTab == ResourceTab.Traces)
        {
            var query = HttpUtility.ParseQueryString(NavigationManager.ToAbsoluteUri(NavigationManager.Uri).Query);
            foreach (var key in new[] { "type", "filters" })
            {
                if (query[key] is { } value)
                {
                    url = QueryHelpers.AddQueryString(url, key, value);
                }
            }
        }

        return AddPaneToUrl(url);
    }

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
            var names = QueryHelpers.ParseQuery(query).TryGetValue(DashboardUrls.ResourceSelectionQueryName, out var selection)
                ? selection.OfType<string>().Where(name => !string.IsNullOrEmpty(name)).Distinct(StringComparers.ResourceName).ToList()
                : [];
            return new ResourceLocation(ResourceTab.Traces, RouteResourceName: null, SelectedResourceNames: names, KeepSelection: names.Count == 0);
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
        // The layout can render while the stored state is read, and the content it shows until the landing decides
        // mustn't be remembered as open (see PersistContentOpenAsync).
        _isDecidingLanding = true;
        try
        {
            await RedirectLandingAsync();
        }
        finally
        {
            _isDecidingLanding = false;
        }
    }

    private async Task RedirectLandingAsync()
    {
        var isInitialLanding = _isInitialLanding;
        _isInitialLanding = false;
        List<string> resourceNames;
        try
        {
            // Entering the resources view restores the selection made earlier in this browser session. Without one,
            // such as when the dashboard opens, all resources stay selected and the overview summarizes the app.
            var lastSelected = await SessionStorage.GetAsync<List<string>>(BrowserStorageKeys.LastSelectedResources);
            resourceNames = lastSelected is { Success: true, Value: { } names }
                ? names.Where(IsKnownResourceName).ToList()
                : [];

            // The full-page list is where the dashboard opens, not where a reload of the overview goes: the overview of
            // all resources has the same URL. When this browser tab last showed the resource content, or a link such as
            // /graph asked for it (see GraphRedirect), the content stays open.
            if (isInitialLanding &&
                await SessionStorage.GetAsync<bool>(BrowserStorageKeys.ResourceContentOpen) is { Success: true, Value: true })
            {
                _storedContentOpen = true;
                isInitialLanding = false;
            }
        }
        catch (JSDisconnectedException)
        {
            return;
        }

        if (resourceNames.Count > 0)
        {
            NavigationManager.NavigateTo(GetTabUrl(ResourceTab.Overview, resourceNames), new NavigationOptions { ReplaceHistoryEntry = true });
        }
        else if (isInitialLanding && ViewportInformation.IsDesktop)
        {
            // The dashboard opens on the list of all resources, filling the page, with no view open: selecting
            // resources opens their overview, and a tab opens its view for all resources. Narrow layouts have no
            // full-page list, so they keep the overview.
            SetResourceListVisible(true);
            // Nothing was shown before the list, so there's no transition to animate.
            _animatePaneTransition = false;
        }
    }

    private bool IsKnownResourceName(string name) =>
        ResourceViewModel.TryGetResourceByName(name, _resourceByName, out _);

    /// <summary>
    /// Remembers for this browser tab whether the resource content is open or the full-page list replaces it, so a
    /// reload shows the same thing (see <see cref="TryRedirectLandingAsync"/>).
    /// </summary>
    /// <remarks>
    /// The full-page list is toggled from many places, including every navigation, so the state is stored after it's
    /// rendered rather than where it changes. It isn't stored while the landing decides what to show, or the content
    /// shown until then would be remembered as open, and the landing would read that back.
    /// </remarks>
    private async Task PersistContentOpenAsync()
    {
        if (!ViewportInformation.IsDesktop || !_isLoaded || _pendingLandingRedirect || _isDecidingLanding)
        {
            return;
        }

        var contentOpen = !_isPaneFullCanvas;
        if (_storedContentOpen == contentOpen)
        {
            return;
        }

        _storedContentOpen = contentOpen;
        try
        {
            await SessionStorage.SetAsync(BrowserStorageKeys.ResourceContentOpen, contentOpen);
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected. There is nothing to persist to.
        }
    }

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
        // Reuse the resource subscription's snapshot for signal availability and row lookups.
        // Looking up every row through GetResourceByCompositeName repeatedly scans all sources.
        var allTelemetryResources = TelemetryRepository.GetResources(includeUninstrumentedPeers: true);
        // Composite names aren't unique: ("api-a", "1") and ("api", "a-1") both map to "api-a-1".
        // Leave ambiguous names unmapped rather than attributing another source's signals to an app resource.
        _telemetryResourcesByName = allTelemetryResources
            .GroupBy(r => r.ResourceKey.GetCompositeName(), StringComparers.ResourceName)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparers.ResourceName);
        _resourceSignalAvailability = TelemetryRepository.GetResourceSignalAvailability();
        // Resources that send telemetry but aren't part of the app model (for example a browser app) are listed
        // in their own pane so they don't appear in Resources or Tags.
        var telemetryResources = allTelemetryResources.Where(r => !r.UninstrumentedPeer).ToList();
        var appModelKeys = new HashSet<ResourceKey>();
        foreach (var resource in _resourceByName.Values)
        {
            if (GetTelemetryResource(resource) is { } otlpResource)
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
        var positions = OrderStore.GetPositions(_resourceByName.Values.Where(r => !r.IsParameter));
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
        _resourceByName.Values
            .Where(r => r.IsParameter && _filter.FilterAllResourceKinds(r, _filter.TextFilter))
            .OrderBy(r => r.HasMissingParameterValueState() ? 0 : 1)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();

    /// <summary>
    /// Gets the filtered resources grouped by the selected property. Text search matches group names or resources.
    /// </summary>
    private List<ResourceGroup> GetResourceGroups()
    {
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
        try
        {
            await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneGrouping, grouping.ToString());
        }
        catch (JSDisconnectedException ex)
        {
            // Legacy Tags navigation also persists grouping while the circuit is initializing.
            Logger.LogDebug(ex, "Resource pane grouping persistence stopped because the circuit disconnected.");
        }
    }

    // Resources with multiple tags appear in each group; untagged resources initially appear last. Parameters are part of
    // the groups too, after the group's resources: an app tags a parameter like the resource that uses it, so the
    // parameter shows next to that resource.
    private List<ResourceGroup> GetTagGroups()
    {
        var textFilter = _filter.TextFilter;
        var resources = _resourceByName.Values
            .Where(r => _filter.FilterAllResourceKinds(r, textFilter: string.Empty))
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
        // Telemetry sources aren't resources, so they have no type, but they're listed with the resources. Their
        // option comes after the types, which include Parameter.
        new("types", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesResourceTypesHeader)], _filter.ResourceTypesToVisibility, r => [r.ResourceType],
            TrailingOptions:
            [
                new ResourcePaneFilter.FilterOption(
                    "telemetry-sources",
                    Loc[nameof(Resources.Layout.NavMenuTelemetrySourcesTab)],
                    () => _telemetryOnlyResources.Count,
                    () => _showTelemetryOnlyResources,
                    SetTelemetrySourcesShownAsync)
            ]),
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
        if (!OrderStore.TryMove(resource.PersistentKey, target.PersistentKey, after, _resourceByName.Values.Where(r => !r.IsParameter)))
        {
            Logger.LogDebug("Rejected resource reorder from '{ResourceName}' to '{TargetName}' because a resource has no position.", resourceName, targetName);
            await ToastService.ShowWarningToastAsync(Loc[nameof(Resources.Layout.ResourcePaneReorderUnavailable)]);
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
            Logger.LogDebug("Rejected group reorder from '{GroupKey}' to '{TargetKey}' because a group has no position.", groupKey, targetKey);
            await ToastService.ShowWarningToastAsync(Loc[nameof(Resources.Layout.ResourcePaneGroupReorderUnavailable)]);
            return false;
        }

        _orderStatus = string.Format(CultureInfo.CurrentCulture,
            Loc[after ? nameof(Resources.Layout.ResourcePaneMovedAfter) : nameof(Resources.Layout.ResourcePaneMovedBefore)],
            GetGroupName(group.Value), GetGroupName(target.Value));
        StateHasChanged();
        return true;
    }

    private async Task OnToggleCollapseAsync(string uniqueResourceName)
    {
        if (!_resourceByName.TryGetValue(uniqueResourceName, out var resource))
        {
            return;
        }

        if (!_collapsedResourceNames.Remove(resource.PersistentKey))
        {
            _collapsedResourceNames.Add(resource.PersistentKey);
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
        _isResourcePickerOpen = false;
        CancelPaneSelectionReveal();
        _animatedPaneNavigation = null;
        _animatePaneTransition = true;
        _isPaneCollapsed = _isPaneFullCanvas || !_isPaneCollapsed;
        _isPaneFullCanvas = false;
        ClearPaneActivity();
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneCollapsed, _isPaneCollapsed);
    }

    private void OpenDrawer() => _isDrawerOpen = true;

    private void CloseDrawer() => _isDrawerOpen = false;

    private void ToggleResourcePicker()
    {
        _isResourcePickerOpen = !_isResourcePickerOpen;
        _focusResourcePicker = _isResourcePickerOpen;
    }

    [JSInvokable]
    public Task OnResourcePickerClosedAsync() => InvokeAsync(() =>
    {
        if (_isDisposed || !_isResourcePickerOpen)
        {
            return;
        }

        _isResourcePickerOpen = false;
        StateHasChanged();
    });

    private void UpdatePaneMenuItems()
    {
        _paneMenuItems.Clear();

        // Collect the parent names once rather than scanning every resource for each resource.
        var parentNames = new HashSet<string>(StringComparers.ResourceName);
        foreach (var resource in _resourceByName.Values)
        {
            if (resource.GetResourcePropertyValue(KnownProperties.Resource.ParentName) is { Length: > 0 } parentName)
            {
                parentNames.Add(parentName);
            }
        }

        var resourcesWithChildren = _resourceByName.Values
            .Where(r => !r.IsResourceHidden(_filter.ShowHiddenResources) && parentNames.Contains(r.Name))
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

    // Tabs that show a resource's data rather than the resource itself.
    private bool IsDataTab => CurrentTab is ResourceTab.Console or ResourceTab.StructuredLogs or ResourceTab.Traces or ResourceTab.Metrics;

    private bool IsCompact => ViewPreferences.Density == ResourceListDensity.Compact;

    // The time a row shows, in the sidebar and in the full canvas's health column: when the resource stopped, once it
    // has, rather than when it started, since "Started 3 h ago" says little about a resource that exited a minute ago.
    // Older app hosts don't report a stop time, so a stopped resource without one still shows when it started.
    private (string Label, DateTime Time)? GetRowTime(ResourceViewModel resource) =>
        resource.IsStopped() && resource.StopTimeStamp is { } stopTimeStamp
            ? (Loc[nameof(Resources.Layout.ResourceHeaderStopped)], stopTimeStamp)
            : resource.StartTimeStamp is { } startTimeStamp
                ? (Loc[nameof(Resources.Layout.ResourceHeaderStarted)], startTimeStamp)
                : null;

    // The tooltip: only the exact time, for example "Nov 6, 2026, 2:30 PM PST", since the row already shows the age.
    private string GetRowTimeTooltip((string Label, DateTime Time) rowTime) => FormatHelpers.FormatTooltipDateTime(TimeProvider, rowTime.Time);

    // The accessible name, for example "Stopped 1m ago (Nov 6, 2026, 2:30 PM PST)". Unlike the tooltip, it contains the
    // short age the row shows, so the accessible name includes the visible text (WCAG 2.5.3, label in name:
    // https://www.w3.org/WAI/WCAG22/Understanding/label-in-name).
    private string GetRowTimeDescription((string Label, DateTime Time) rowTime, string tooltip) => $"{GetRowTimeText(rowTime)} ({tooltip})";

    // What a row shows: the verb, so a stopped resource doesn't read as running, and a short age ("Started 7m ago").
    private string GetRowTimeText((string Label, DateTime Time) rowTime) => $"{rowTime.Label} {GetShortAge(rowTime.Time)}";

    // The short age ("7m ago"), which every row repeats; the tooltip and accessible label add the exact time (see
    // GetRowTimeTooltip and GetRowTimeDescription).
    private string GetShortAge(DateTime time)
    {
        // Clamp so a time slightly in the future (clock skew between processes) reads as "now".
        var age = TimeProvider.GetUtcNow() - new DateTimeOffset(time);
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age switch
        {
            { TotalMinutes: < 1 } => Loc[nameof(Dashboard.Resources.Layout.ResourceStartedAgeNow)],
            { TotalHours: < 1 } => Loc[nameof(Dashboard.Resources.Layout.ResourceStartedAgeMinutes), (int)age.TotalMinutes],
            { TotalDays: < 1 } => Loc[nameof(Dashboard.Resources.Layout.ResourceStartedAgeHours), (int)age.TotalHours],
            _ => Loc[nameof(Dashboard.Resources.Layout.ResourceStartedAgeDays), (int)age.TotalDays]
        };
    }

    // Relative ages remain current even when the resource itself has no updates.
    private async Task RefreshAgesPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!_isPaneCollapsed || _isPaneFullCanvas)
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

    private static IEnumerable<CommandViewModel> GetFullPageRowCommands(ResourceViewModel resource) =>
        resource.IsParameter
            ? resource.Commands.Where(c =>
                string.Equals(c.Name, CommandViewModel.SetParameterCommand, StringComparisons.CommandName) &&
                c.State != CommandViewModelState.Hidden).Take(1)
            : GetHighlightedCommands(resource);

    private static IEnumerable<CommandViewModel> GetResourceRowCommands(ResourceViewModel resource)
    {
        string[] preferred = resource.KnownState switch
        {
            KnownResourceState.Running => [CommandViewModel.StopCommand, CommandViewModel.RestartCommand],
            KnownResourceState.NotStarted or KnownResourceState.Exited or KnownResourceState.Finished or KnownResourceState.FailedToStart =>
                [CommandViewModel.StartCommand],
            _ => []
        };
        return preferred
            .SelectMany(name => resource.Commands.Where(c => c.Name == name && c.State != CommandViewModelState.Hidden))
            .Concat(GetHighlightedCommands(resource))
            .DistinctBy(c => c.Name)
            .Take(DashboardUIHelpers.MaxHighlightedCommands);
    }

    private static IEnumerable<CommandViewModel> GetSidebarCommands(ResourceViewModel resource)
    {
        // A custom highlighted command is the resource author's primary action.
        // Keep both supported custom highlights before considering generic lifecycle shortcuts.
        var commands = resource.Commands
            .Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden &&
                c.Name is not (CommandViewModel.StartCommand or CommandViewModel.StopCommand or CommandViewModel.RestartCommand))
            .Take(DashboardUIHelpers.MaxHighlightedCommands)
            .ToArray();
        return commands.Length > 0 ? commands : GetResourceRowCommands(resource).Take(1);
    }

    private string? GetParentDescription(ResourceViewModel resource)
    {
        var parentName = resource.GetResourcePropertyValue(KnownProperties.Resource.ParentName);
        if (string.IsNullOrEmpty(parentName))
        {
            return null;
        }

        var parent = _resourceByName.TryGetValue(parentName, out var value) ? GetResourceName(value) : parentName;
        return string.Format(CultureInfo.CurrentCulture, Loc[nameof(Dashboard.Resources.Layout.ResourcePaneChildOf)], parent);
    }

    private string? GetRelationshipDescription(ResourceViewModel resource, string type, string label)
    {
        // Relationship targets use logical display names, e.g. "catalogservice",
        // rather than replica IDs such as "catalogservice-mmdgfsxd". Keep that
        // logical scope: a dependency is not a claim about a particular replica.
        var names = GetRelationshipNames(resource, type);
        return names.Length == 0 ? null : string.Format(CultureInfo.CurrentCulture, Loc[label], string.Join(", ", names));
    }

    private static string[] GetRelationshipNames(ResourceViewModel resource, string type) =>
        resource.Relationships
            .Where(r => r.Type == type && !StringComparers.ResourceName.Equals(r.ResourceName, resource.DisplayName))
            .Select(r => r.ResourceName)
            .Distinct(StringComparers.ResourceName)
            .Order(StringComparers.ResourceName)
            .ToArray();

    private static Dictionary<string, IReadOnlyList<bool>> GetTreeGuides(IReadOnlyList<ResourceGridViewModel> rows)
    {
        var guides = new Dictionary<string, IReadOnlyList<bool>>(StringComparers.ResourceName);
        var ancestors = new List<ResourceGridViewModel>();
        foreach (var row in rows)
        {
            if (ancestors.Count > row.Depth)
            {
                ancestors.RemoveRange(row.Depth, ancestors.Count - row.Depth);
            }

            var continuation = new bool[row.Depth];
            // Ancestor trunks continue through descendants only while that ancestor
            // has another sibling. The current level always draws its elbow.
            for (var level = 0; level < row.Depth; level++)
            {
                var child = level + 1 == row.Depth ? row : ancestors[level + 1];
                continuation[level] = ancestors[level].Children[^1] != child;
            }
            guides.Add(row.Resource.Name, continuation);
            ancestors.Add(row);
        }
        return guides;
    }

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
        var endpoints = GetResourceEndpointMenuItems(resource);
        if (endpoints.Count > 0)
        {
            items.Add(new MenuButtonItem { IsDivider = true });
            items.AddRange(endpoints);
        }
        return items;
    }

    private static List<MenuButtonItem> GetResourceEndpointMenuItems(ResourceViewModel resource) =>
        ResourceUrlHelpers.GetUrls(resource, includeInternalUrls: false, includeNonEndpointUrls: true)
            .Select(url =>
            {
                var address = DashboardUIHelpers.MaskQueryStringValues(url.OriginalUrlString);
                var text = url.DisplayName is { Length: > 0 } name ? $"{name}: {address}" : address;
                return url.Url is { } href
                    ? MenuButtonItem.CreateExternalLink(text, href, new Icons.Regular.Size16.Open(), tooltip: address)
                    : new MenuButtonItem { Text = text, Tooltip = address, IsDisabled = true };
            }).ToList();

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

    private Task ExecuteResourceCommandAsync(string uniqueResourceName, string commandName)
    {
        // The row's handler is shared across renders, so the resource and command are resolved when it runs: the
        // resource may have been replaced by an update since the handler was created.
        if (!_resourceByName.TryGetValue(uniqueResourceName, out var resource) ||
            resource.Commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparisons.CommandName)) is not { } command)
        {
            return Task.CompletedTask;
        }

        return ExecuteResourceCommandAsync(resource, command);
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

    internal string GetResourceName(ResourceViewModel resource) =>
        IsReplica(resource) ? resource.Name : resource.DisplayName;

    internal bool IsReplica(ResourceViewModel resource) =>
        GetDuplicateDisplayNames().Contains(resource.DisplayName);

    private HashSet<string> GetDuplicateDisplayNames()
    {
        // Renders and command completions (which continue on the thread pool) can both get here, while the resource
        // subscription updates the resources and then the version on another thread. The set and the version it was
        // built for are published together as one immutable snapshot, so concurrent rebuilds can't pair a set built
        // from older resources with a newer version, which would keep it stale until the next resource update. A
        // snapshot built while an update is in progress is labeled with the earlier version and rebuilt next time.
        var version = Volatile.Read(ref _resourceVersion);
        var snapshot = Volatile.Read(ref _duplicateDisplayNames);
        if (snapshot.Version != version)
        {
            var seen = new HashSet<string>(StringComparers.ResourceName);
            var duplicates = new HashSet<string>(StringComparers.ResourceName);
            foreach (var resource in _resourceByName.Values)
            {
                if (!seen.Add(resource.DisplayName))
                {
                    duplicates.Add(resource.DisplayName);
                }
            }

            snapshot = new DuplicateDisplayNames(version, duplicates);
            Volatile.Write(ref _duplicateDisplayNames, snapshot);
        }

        return snapshot.Names;
    }

    private sealed record DuplicateDisplayNames(int Version, HashSet<string> Names);

    private RowHandlers GetRowHandlers(ResourceViewModel resource)
    {
        if (!_rowHandlers.TryGetValue(resource.Name, out var handlers))
        {
            handlers = new RowHandlers(this, resource.Name);
            _rowHandlers[resource.Name] = handlers;
        }

        return handlers;
    }

    private EventCallback<MouseEventArgs> GetTelemetryRowClick(string telemetryResourceName)
    {
        if (!_telemetryRowClicks.TryGetValue(telemetryResourceName, out var click))
        {
            click = EventCallback.Factory.Create<MouseEventArgs>(this, e => OnRowClickAsync(e, telemetryResourceName));
            _telemetryRowClicks[telemetryResourceName] = click;
        }

        return click;
    }

    // The telemetry sources that have a row click handler, for tests.
    internal IReadOnlyCollection<string> TelemetryRowClickNames => _telemetryRowClicks.Keys;

    // Removes the handlers of resources and telemetry sources that no longer exist.
    private void PruneRowHandlers()
    {
        foreach (var name in _rowHandlers.Keys.Where(name => !_resourceByName.ContainsKey(name)).ToList())
        {
            _rowHandlers.Remove(name);
        }

        if (_telemetryRowClicks.Count > 0)
        {
            var telemetryNames = _telemetryOnlyResources.Select(resource => resource.Name).ToHashSet(StringComparers.ResourceName);
            foreach (var name in _telemetryRowClicks.Keys.Where(name => !telemetryNames.Contains(name)).ToList())
            {
                _telemetryRowClicks.Remove(name);
            }
        }
    }

    private string GetResourceTypeLabel(ResourceViewModel resource) => resource.GetResourceTypeDisplayName(ResourcesLoc);

    private string GetResourceAccessibilityDescription(ResourceViewModel resource, string stateText, int errorCount, string? unavailableReason)
    {
        List<string> descriptions = [stateText, GetResourceTypeLabel(resource)];
        if (unavailableReason is not null)
        {
            descriptions.Add(unavailableReason);
        }
        if (resource.HealthStatus is { } health)
        {
            descriptions.Add($"{ResourcesLoc[nameof(Resources.Resources.ResourcesDetailsHealthStateProperty)]}: {health.Humanize()}");
        }
        if (GetParentDescription(resource) is { } parent)
        {
            descriptions.Add(parent);
        }
        if (GetRelationshipDescription(resource, KnownRelationshipTypes.WaitFor, nameof(Resources.Layout.ResourcePaneDependsOn)) is { } dependencies)
        {
            descriptions.Add(dependencies);
        }
        if (GetRelationshipDescription(resource, KnownRelationshipTypes.Reference, nameof(Resources.Layout.ResourcePaneReferences)) is { } references)
        {
            descriptions.Add(references);
        }
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

    private bool HasMultipleReplicas(ResourceViewModel resource) => GetDuplicateDisplayNames().Contains(resource.DisplayName);

    private OtlpResource? GetTelemetryResource(ResourceViewModel resource) => _telemetryResourcesByName.GetValueOrDefault(resource.Name);

    private string? GetResourceUnavailableReason(ResourceViewModel resource)
    {
        // While the resource list fills the page, its content is closed and no tab is shown selected (see
        // RenderTabNavigation). CurrentTab is only kept for the next selection, so no row is unavailable for it.
        if (_isPaneFullCanvas)
        {
            return null;
        }

        if (resource.IsParameter)
        {
            return CurrentTab switch
            {
                ResourceTab.Console => Loc[nameof(Resources.Layout.ResourcePaneParameterNoConsoleLogs)].Value,
                ResourceTab.StructuredLogs => Loc[nameof(Resources.Layout.ResourcePaneParameterNoStructuredLogs)].Value,
                ResourceTab.Traces => Loc[nameof(Resources.Layout.ResourcePaneParameterNoTraces)].Value,
                ResourceTab.Metrics => Loc[nameof(Resources.Layout.ResourcePaneParameterNoMetrics)].Value,
                ResourceTab.Terminal => Loc[nameof(Resources.Layout.ResourcePaneParameterNoTerminal)].Value,
                _ => null
            };
        }

        return CurrentTab switch
        {
            ResourceTab.Parameters => Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value,
            ResourceTab.Terminal when !CanViewResourceTerminal(resource) => Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value,
            _ => GetTelemetryUnavailableReason(GetTelemetryResource(resource))
        };
    }

    // A telemetry-only row has no overview, console or terminal, and is unavailable for those tabs. Like the other rows,
    // it's never unavailable while the content is closed (see GetResourceUnavailableReason).
    private string? GetTelemetryRowUnavailableReason(OtlpResource resource) =>
        _isPaneFullCanvas ? null
        : CurrentTab is ResourceTab.Overview or ResourceTab.Console or ResourceTab.Terminal or ResourceTab.Parameters
            ? Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value
            : GetTelemetryUnavailableReason(resource);

    private string? GetTelemetryUnavailableReason(OtlpResource? resource)
    {
        var availability = resource is not null ? _resourceSignalAvailability.GetValueOrDefault(resource.ResourceKey) : default;

        return CurrentTab switch
        {
            ResourceTab.StructuredLogs when !availability.HasLogs => Loc[nameof(Resources.Layout.ResourcePaneNoStructuredLogs)].Value,
            ResourceTab.Traces when !availability.HasTraces => Loc[nameof(Resources.Layout.ResourcePaneNoTraces)].Value,
            ResourceTab.Metrics when !availability.HasMetrics => Loc[nameof(Resources.Layout.ResourcePaneNoMetrics)].Value,
            _ => null
        };
    }

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
    /// Gets the telemetry keys for the selected resources, or all sources in the current pane when there is
    /// no selection.
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

        // Resolve even a single selection here. A page opened before the resource's first signal can
        // still have its "all resources" key; returning null would widen the query when telemetry arrives.
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

    /// <summary>
    /// Gets the names the given resources report their telemetry under, or the current selection's when none are given:
    /// what metric pins are kept by (see <see cref="MetricPinStore"/>). Unlike <see cref="GetSelectionTelemetryKeys"/>,
    /// it includes resources that haven't sent telemetry yet, so a pin made now applies to them once they do. A
    /// resource's replicas report its name, its display name in the app model.
    /// </summary>
    internal IReadOnlyList<string> GetSelectionPinResourceNames(IReadOnlyCollection<string>? resourceNames)
    {
        if (resourceNames is null || resourceNames.Count == 0)
        {
            resourceNames = _selectedResourceNames;
        }

        IEnumerable<string> names;
        if (resourceNames.Count > 0)
        {
            names = resourceNames.Select(name =>
                ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource)
                    ? GetPinResourceName(resource)
                    : _telemetryOnlyResources.FirstOrDefault(r => string.Equals(r.Name, name, StringComparisons.ResourceName))?.Resource.ResourceKey.Name)
                .OfType<string>();
        }
        else if (IsTelemetryPane)
        {
            names = _telemetryOnlyResources.Select(r => r.Resource.ResourceKey.Name);
        }
        else
        {
            // All resources: the app's resources, and the telemetry sources while the pane lists them. Parameters don't
            // send telemetry.
            names = _resourceByName.Values.Where(r => !r.IsParameter).Select(GetPinResourceName);
            if (_showTelemetryOnlyResources)
            {
                names = names.Concat(_telemetryOnlyResources.Select(r => r.Resource.ResourceKey.Name));
            }
        }

        return [.. names.Distinct(StringComparers.ResourceName)];

        // The name the resource's telemetry has, or will have: its display name, which replicas share.
        string GetPinResourceName(ResourceViewModel resource) => GetTelemetryResource(resource)?.ResourceKey.Name ?? resource.DisplayName;
    }

    internal IReadOnlyList<ResourceKey> GetPaneTelemetryKeys()
    {
        if (IsTelemetryPane)
        {
            return _telemetryOnlyResources.Select(r => r.Resource.ResourceKey).Distinct().ToList();
        }

        // "All resources" covers the app's resources, and the telemetry sources while the pane lists them. Parameters
        // don't send telemetry.
        var keys = _resourceByName.Values.Where(r => !r.IsParameter).Select(GetTelemetryResource).OfType<OtlpResource>().Select(r => r.ResourceKey);
        if (_showTelemetryOnlyResources)
        {
            keys = keys.Concat(_telemetryOnlyResources.Select(r => r.Resource.ResourceKey));
        }

        return keys.Distinct().ToList();
    }

    /// <summary>
    /// Gets the title for the page when nothing is selected.
    /// </summary>
    private string GetScopeTitle() =>
        Loc[DashboardClient.IsEnabled ? nameof(Resources.Layout.ResourceHeaderAllResources) : nameof(Resources.Layout.TelemetryHeaderAllSources)];

    // Sets the filter's "Telemetry sources" option (see GetFilterGroups).
    private async Task SetTelemetrySourcesShownAsync(bool shown)
    {
        if (_showTelemetryOnlyResources == shown)
        {
            return;
        }

        _showTelemetryOnlyResources = shown;
        // Remembered in the browser, as the toggle this option replaced was. The value lists the kinds shown, and
        // telemetry sources is the only one that can still be hidden (see where it's read).
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneKinds, shown ? TelemetrySourcesKind : string.Empty);

        // "All resources" covers telemetry sources only while they're listed, so pages showing all resources refresh.
        ResourcesChanged?.Invoke();
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
    /// Gets the replicas that run now of the resource a missing replica belonged to. A replica's name is its resource's
    /// display name and a suffix ("catalogservice-wfpmezqc"), and restarting the app replaces it with a replica under
    /// a new suffix, so a page left open on the old name finds nothing. Resources whose name is their display name
    /// aren't replicas, and "catalog-shell" isn't a replica of "catalog": the suffix has no hyphen.
    /// </summary>
    internal List<ResourceViewModel> GetReplacementReplicas(string missingName) =>
        _resourceByName.Values
            .Where(resource => !string.Equals(resource.Name, resource.DisplayName, StringComparisons.ResourceName) &&
                missingName.Length > resource.DisplayName.Length + 1 &&
                missingName.StartsWith(resource.DisplayName + "-", StringComparisons.ResourceName) &&
                !missingName.AsSpan(resource.DisplayName.Length + 1).Contains('-'))
            .OrderBy(resource => resource.Name, StringComparers.ResourceName)
            .ToList();

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
    /// the resources support the current tab. Telemetry pages are kept for any selection but a single parameter.
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
        else if (CurrentTab is ResourceTab.Metrics or ResourceTab.Traces or ResourceTab.StructuredLogs &&
            selection is not [{ Resource: { IsParameter: true } }])
        {
            // Keep the chosen telemetry page so it can explain why the selection has no data.
            tab = CurrentTab;
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
        _isResourcePickerOpen &= e.ShiftKey || e.CtrlKey || e.MetaKey;
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
                if (_isPaneFullCanvas)
                {
                    // Closing a scoped detail can retain this selection and its filtered parent URL.
                    SetResourceListVisible(false);
                }

                // Selecting the only selected row again keeps it selected. Clearing the selection here would silently
                // widen the view to every resource on an accidental second click; "All resources" does that explicitly.
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

        NavigateToSelection(selection);
    }

    // A single selected resource is shown by its row alone, so a one-resource group doesn't duplicate the highlight.
    private bool IsResourceGroupSelected(List<string> groupResourceNames) =>
        groupResourceNames.Count > 1 &&
        groupResourceNames.Count == _selectedResourceNames.Count &&
        groupResourceNames.All(IsSelected);

    private async Task OnResourceGroupClick(MouseEventArgs e, List<string> groupResourceNames)
    {
        _isResourcePickerOpen &= e.ShiftKey || e.CtrlKey || e.MetaKey;
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
                // Like a row, selecting the selected group again keeps the selection.
                return;
            }

            selection = groupResourceNames;
        }

        _selectionAnchor = selection is [.., var last] ? last : null;
        NavigateToSelection(selection);
    }

    private async Task OnAllResourcesClickAsync()
    {
        _isResourcePickerOpen = false;
        var uri = NavigationManager.ToAbsoluteUri(NavigationManager.Uri);
        var path = NavigationManager.ToBaseRelativePath(uri.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(uri.Query);
        // A direct detail link can also have no resource selection. Preserve the URL only
        // for top-level tab content, not a trace detail or a meter/instrument view.
        var reopenCurrentContent = IsAllSelected &&
            string.Equals(path.TrimEnd('/'), GetTabUrl(CurrentTab, []).TrimStart('/'), StringComparisons.UrlPath) &&
            (CurrentTab != ResourceTab.Metrics || query["meter"] is null && query["instrument"] is null);
        _selectionAnchor = null;

        // Remember the cleared selection so entering the overview later doesn't select a resource again.
        await SetStoredSelectionAsync([]);

        // Select all also opens the summary when the full-canvas list already has the same URL.
        SetResourceListVisible(false);
        if (!reopenCurrentContent)
        {
            NavigationManager.NavigateTo(DashboardClient.IsEnabled && CurrentTab == ResourceTab.Overview ? DashboardUrls.ResourceListUrl() : GetSelectionUrl([]));
        }
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

        // "Select all" covers telemetry sources only while the pane lists them, as its telemetry scope does (see
        // GetPaneTelemetryKeys). Deselecting one resource must not add sources the filter hides.
        var names = GetAllResourceNames();
        if (DashboardClient.IsEnabled && _showTelemetryOnlyResources)
        {
            names.AddRange(_telemetryOnlyResources.Select(r => r.Name).Where(n => !names.Contains(n, StringComparers.ResourceName)));
        }

        return names;
    }

    private void NavigateToSelection(IReadOnlyList<string> resourceNames)
    {
        var url = GetSelectionUrl(resourceNames);
        _animatedPaneNavigation = _isPaneFullCanvas && resourceNames.Count == 1
            ? NavigationManager.ToAbsoluteUri(url).AbsoluteUri
            : null;
        _resourcePickerSelectionLocation = _isResourcePickerOpen ? NavigationManager.ToAbsoluteUri(url).AbsoluteUri : null;
        NavigationManager.NavigateTo(url);
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

    // The id of the hidden tab that's active while no tab is shown selected (see RenderTabNavigation). It doesn't
    // start with "tab-", so OnTabChanged ignores it.
    private const string NoActiveTabId = "resource-tab-none";

    private async Task OnTabChanged(FluentTab? newTab, bool isDesktop, string? renderedLocation)
    {
        // FluentTabs also raises this when a tab is disposed, for example when leaving the resources view, and
        // when it syncs with ActiveTabId after a navigation. Only a change made while this layout shows the current
        // URL is a user selecting a tab.
        // Switching between the live view and a recording re-creates the page, and the outgoing layout's tabs
        // raise changes while they're torn down, which would otherwise navigate to another tab.
        // A delayed event from the previous header must not navigate the newly selected resource, unless the user really
        // chose that tab: a click that lands just as a navigation finishes reaches the layout from the outgoing header.
        // The script records real activations, which the header's own change events while it's torn down never are.
        var isStale = !string.Equals(renderedLocation, _lastLocation, StringComparison.Ordinal)
            || !string.Equals(NavigationManager.Uri, _lastLocation, StringComparison.Ordinal);
        if (isStale && newTab?.Id is { } staleId && staleId.StartsWith("tab-", StringComparison.Ordinal) &&
            _jsModule is not null && !_isDisposed && isDesktop == ViewportInformation.IsDesktop)
        {
            try
            {
                if (await _jsModule.InvokeAsync<bool>("consumeRecentTabActivation", staleId) &&
                    Enum.TryParse<ResourceTab>(staleId["tab-".Length..], out var staleTab) &&
                    string.Equals(NavigationManager.Uri, _lastLocation, StringComparison.Ordinal))
                {
                    await OpenChosenTabAsync(staleTab);
                }
            }
            catch (JSDisconnectedException)
            {
            }
            return;
        }

        if (_isDisposed
            || isDesktop != ViewportInformation.IsDesktop
            || isStale
            || newTab?.Id is not { } id
            || !id.StartsWith("tab-", StringComparison.Ordinal)
            || !Enum.TryParse<ResourceTab>(id["tab-".Length..], out var tab)
            // Includes choosing the current tab while the content is closed: FluentTabs raises the same change when it
            // syncs with ActiveTabId, so it can't tell the user's choice apart. OpenTabFromFullCanvasAsync handles it.
            || tab == CurrentTab
            // A navigation started from the tab strip is still in flight. The layout re-renders when this handler
            // returns, before the router reaches the new location, and that render must not put the previous tab
            // back: the strip would report it as a change once the new page shows, and navigate back to it, forever.
            || _navigatingToTab is not null)
        {
            return;
        }

        // A newly mounted FluentTabs can report its initial first tab after ActiveTabId
        // has been applied. Only focused pointer/keyboard tab changes own navigation.
        if (_jsModule is null || GetTabs().FirstOrDefault(t => t.Tab == tab && t.IsVisible)?.Href is not { } href)
        {
            return;
        }
        // Keep the user's choice rendered while the browser focus check is in flight.
        _navigatingToTab = tab;
        bool isFocused;
        try
        {
            isFocused = await _jsModule.InvokeAsync<bool>("isResourceTabFocused", id);
        }
        catch (JSDisconnectedException)
        {
            return;
        }
        if (!isFocused || _isDisposed || isDesktop != ViewportInformation.IsDesktop || _navigatingToTab != tab ||
            !string.Equals(renderedLocation, _lastLocation, StringComparison.Ordinal) ||
            !string.Equals(NavigationManager.Uri, _lastLocation, StringComparison.Ordinal))
        {
            if (_navigatingToTab == tab && string.Equals(renderedLocation, _lastLocation, StringComparison.Ordinal))
            {
                _navigatingToTab = null;
            }
            return;
        }

        if (_closingToLocation is not null)
        {
            // A close is in flight: open this tab once it lands (see OnParametersSetAsync).
            _tabAfterClose = tab;
            return;
        }

        await NavigateToTabAsync(href);
    }

    // Opens a tab the user chose, for the current selection. While the full-page list is showing, choosing the tab that's
    // already current opens its content; otherwise it navigates to the tab.
    private async Task OpenChosenTabAsync(ResourceTab tab)
    {
        if (GetTabs().FirstOrDefault(t => t.Tab == tab && t.IsVisible)?.Href is not { } href ||
            (tab == CurrentTab && !_isPaneFullCanvas))
        {
            return;
        }

        if (_closingToLocation is not null)
        {
            _tabAfterClose = tab;
            return;
        }

        await NavigateToTabAsync(href);
    }

    /// <summary>
    /// Called by the layout's script when the user clicks a tab, or presses Enter or Space on it, while the resource list
    /// fills the page. No tab is shown selected then, so choosing the current tab must open its content. FluentTabs
    /// can't report that choice: it raises the same change when it syncs with ActiveTabId, for example right after
    /// closing the content with a tab still focused, which would reopen it. Other tabs open through OnTabChanged.
    /// </summary>
    [JSInvokable]
    public async Task OpenTabFromFullCanvasAsync(string tabId)
    {
        if (_isDisposed || !_isPaneFullCanvas
            || !tabId.StartsWith("tab-", StringComparison.Ordinal)
            || !Enum.TryParse<ResourceTab>(tabId["tab-".Length..], out var tab)
            || tab != CurrentTab
            || GetTabs().FirstOrDefault(t => t.Tab == tab && t.IsVisible)?.Href is not { } href)
        {
            return;
        }

        await NavigateToTabAsync(href);
    }

    private async Task NavigateToTabAsync(string href)
    {
        var persistExpandedPane = _isPaneFullCanvas && _isPaneCollapsed;
        if (_isPaneFullCanvas)
        {
            // Reveal the destination as soon as navigation starts, even if its page is
            // slow to render or the current tab's menu item doesn't change the URL.
            _isPaneCollapsed = false;
            SetResourceListVisible(false);
        }

        // Opening the current tab from the full-canvas list doesn't change the URL, so no new location will clear the
        // tab the strip shows while navigating (see OnParametersSetAsync). Clear it here instead.
        if (string.Equals(NavigationManager.ToAbsoluteUri(href).AbsoluteUri, NavigationManager.Uri, StringComparison.Ordinal))
        {
            _navigatingToTab = null;
        }

        NavigationManager.NavigateTo(href);
        if (persistExpandedPane)
        {
            await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ResourcePaneCollapsed, false);
        }
    }

    private IEnumerable<TabItem> GetTabs()
    {
        if (CurrentTab == ResourceTab.Parameters)
        {
            yield return CreateTab(ResourceTab.Parameters, Loc[nameof(Resources.Layout.NavMenuParametersTab)], new Icons.Regular.Size24.Key(), isVisible: true);
            yield break;
        }

        var isTelemetryOnly = SelectedTelemetryOnlyResource is not null;

        // Every tab is always rendered and hidden when it doesn't apply. Removing a FluentTab makes FluentTabs
        // activate its first tab, which would navigate away from the current page.
        var errorCount = _selectedItems.Sum(i => i.Resource is { } r ? GetUnviewedErrorCount(r) : 0);
        // Overview stays for a resource that isn't known, such as a replica that was replaced: its page says the
        // resource wasn't found, and the tab is the way back to it from the resource's other pages.
        yield return CreateTab(ResourceTab.Overview, Loc[nameof(Resources.Layout.ResourceTabOverview)], new Icons.Regular.Size24.Board(),
            isVisible: DashboardClient.IsEnabled && !isTelemetryOnly);
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
                // Like the tab strip, the menu shows no current view while the content is closed.
                Checked = tab.Tab == CurrentTab && !_isPaneFullCanvas,
                Tooltip = tab.IsAvailable ? null : Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value,
                OnClick = () => NavigateToTabAsync(tab.Href)
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
        _paneActivitySubscription?.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_ageRefreshTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_paneActivityTimerTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_paneActivityRefreshTask);
        _cts.Dispose();

        if (_jsModule is { } module)
        {
            try
            {
                await module.InvokeVoidAsync("unregisterPaneResizer", _layoutElement);
                await module.InvokeVoidAsync("unregisterResourceOrdering", _layoutElement);
                await module.InvokeVoidAsync("unregisterResourceContextMenu", _layoutElement);
                await module.InvokeVoidAsync("unregisterPaneActivityVisibility", _layoutElement);
                await module.InvokeVoidAsync("unregisterResourcePicker", _layoutElement);
                await module.InvokeVoidAsync("cancelPaneSelectionReveal", _layoutElement);
                await module.InvokeVoidAsync("unregisterTabOverflow", _tabOverflowRegistrationId);
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

    /// <summary>
    /// The event handlers of a resource's row, and of its actions in the resource overview. They're created once per
    /// resource and reused by every render, so Blazor sees unchanged handlers and skips re-rendering the row's buttons
    /// and menus. Each handler resolves the current resource by its unique name when it runs. The name a row shows,
    /// and selects, is its display name unless another resource shares it, and a display name can equal another
    /// resource's unique name, so it can't be used to find the resource.
    /// </summary>
    private sealed class RowHandlers
    {
        private readonly ResourcesLayout _layout;
        private readonly string _resourceName;
        private readonly Dictionary<string, EventCallback<MouseEventArgs>> _commands = new(StringComparers.CommandName);

        public RowHandlers(ResourcesLayout layout, string uniqueResourceName)
        {
            _layout = layout;
            _resourceName = uniqueResourceName;
            Click = EventCallback.Factory.Create<MouseEventArgs>(layout, e =>
                layout._resourceByName.TryGetValue(uniqueResourceName, out var resource)
                    ? layout.OnRowClickAsync(e, layout.GetResourceName(resource))
                    : Task.CompletedTask);
            ToggleCollapse = EventCallback.Factory.Create<MouseEventArgs>(layout, () => layout.OnToggleCollapseAsync(uniqueResourceName));
            MenuItems = () => layout._resourceByName.TryGetValue(uniqueResourceName, out var resource) ? layout.GetResourceMenuItems(resource) : [];
        }

        public EventCallback<MouseEventArgs> Click { get; }

        public EventCallback<MouseEventArgs> ToggleCollapse { get; }

        public Func<IList<MenuButtonItem>> MenuItems { get; }

        public EventCallback<MouseEventArgs> Command(string commandName)
        {
            if (!_commands.TryGetValue(commandName, out var callback))
            {
                callback = EventCallback.Factory.Create<MouseEventArgs>(_layout, () => _layout.ExecuteResourceCommandAsync(_resourceName, commandName));
                _commands[commandName] = callback;
            }

            return callback;
        }
    }
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

internal enum ResourcePaneGrouping
{
    None,
    Tags,
    ResourceType,
    State
}
