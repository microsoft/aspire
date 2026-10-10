// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Humanizer;
using System.Globalization;
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
    private static readonly Icon s_checkmarkIcon = new Icons.Regular.Size16.Checkmark();
    internal const int MinimumPaneWidthPx = 200;
    internal const int MaximumPaneWidthPx = 560;
    internal const int DefaultPaneWidthPx = 290;

    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly HashSet<string> _collapsedResourceNames = new(StringComparers.ResourceName);
    private readonly HashSet<string> _collapsedTagGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<MenuButtonItem> _resourceMenuItems = [];
    private readonly List<MenuButtonItem> _paneMenuItems = [];
    private readonly List<CommandViewModel> _highlightedCommands = [];
    private readonly ResourcesPage.ResourcesViewModel _filter = new() { SelectedViewKind = ResourcesPage.ResourceViewKind.Table };

    private Task? _resourceSubscriptionTask;
    private Subscription? _logsSubscription;
    private Subscription? _telemetryResourcesSubscription;
    private Dictionary<ResourceKey, int>? _unviewedErrorCounts;
    private List<TelemetryOnlyResource> _telemetryOnlyResources = [];
    private string? _collapsedResourceNamesKey;
    private bool _isLoaded;
    private bool _isPaneCollapsed;
    private bool _isDrawerOpen;
    private bool _pendingLandingRedirect;
    private bool _hasLocation;
    private string? _lastLocation;
    private IReadOnlyList<string> _selectedResourceNames = [];
    private List<SelectedResourceItem> _selectedItems = [];
    private string? _selectionAnchor;
    private int? _paneWidth;
    private ElementReference _layoutElement;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<ResourcesLayout>? _selfRef;
    private bool _isResizerRegistered;

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
    public required ResourcePaneState PaneState { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Resources> ResourcesLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.StructuredLogs> StructuredLogsLoc { get; init; }

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
    /// Gets the app model resource that matches <see cref="SelectedResourceName"/>, if any.
    /// </summary>
    internal ResourceViewModel? SelectedResource { get; private set; }

    /// <summary>
    /// Gets the app model resources of a multi-resource selection. Selected resources that only send telemetry are
    /// not included.
    /// </summary>
    internal IEnumerable<ResourceViewModel> SelectedResources => _selectedItems.Select(i => i.Resource).OfType<ResourceViewModel>();

    internal ConcurrentDictionary<string, ResourceViewModel> ResourceByName => _resourceByName;

    /// <summary>
    /// Raised on the renderer's synchronization context after resources change, so pages that display data owned by
    /// the layout (such as the resource overview) can refresh.
    /// </summary>
    internal event Action? ResourcesChanged;

    protected override async Task OnInitializedAsync()
    {
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
            PaneState.SetMode(storedPaneMode);
        }
        await ApplyPaneModeFromLocationAsync(_lastLocation);
        TagStore.Changed += OnTagsChanged;

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

        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(_cts.Token);
        foreach (var resource in snapshot)
        {
            UpsertResource(resource);
        }

        _isLoaded = true;
        UpdateTelemetryOnlyResources();
        UpdateSelection();
        UpdatePaneMenuItems();
        await TryRedirectLandingAsync();

        _resourceSubscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        UpsertResource(resource);
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                    }
                }

                await InvokeAsync(async () =>
                {
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
        if (!resource.IsParameter)
        {
            _filter.ResourceTypesToVisibility.TryAdd(resource.ResourceType, true);
        }
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
        var previousSelection = _selectedResourceNames;
        UpdateFromLocation(location);
        await ApplyPaneModeFromLocationAsync(location);
        _isDrawerOpen = false;
        await TryRedirectLandingAsync();
        await PersistSelectedResourcesAsync();

        if (!previousSelection.SequenceEqual(_selectedResourceNames, StringComparers.ResourceName))
        {
            // Pages that render the selection, such as the overview, aren't always given new parameters when only
            // the selection changes, so notify them.
            ResourcesChanged?.Invoke();
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var showResizer = ViewportInformation.IsDesktop && !_isPaneCollapsed;
        if (showResizer == _isResizerRegistered)
        {
            return;
        }

        try
        {
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", $"./{Assets["Components/Layout/ResourcesLayout.razor.js"]}");
            if (showResizer)
            {
                _selfRef ??= DotNetObjectReference.Create(this);
                await _jsModule.InvokeVoidAsync("registerPaneResizer", _layoutElement, _selfRef, MinimumPaneWidthPx, MaximumPaneWidthPx);
            }
            else
            {
                await _jsModule.InvokeVoidAsync("unregisterPaneResizer", _layoutElement);
            }

            _isResizerRegistered = showResizer;
        }
        catch (JSDisconnectedException)
        {
            // The circuit disconnected while rendering. There is nothing to register.
        }
    }

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
        var parsedLocation = ParseLocation(NavigationManager.ToBaseRelativePath(location));
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
        // resources are already shown keeps them.
        _pendingLandingRedirect = DashboardClient.IsEnabled && parsedLocation.Tab == ResourceTab.Overview && parsedLocation.SelectedResourceNames.Count == 0 && !wasAllResources;
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
    internal string GetTabUrl(ResourceTab tab) => AddPaneToUrl(GetTabUrl(tab, _selectedResourceNames, RouteResourceName));

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
            DashboardUrls.ConsoleLogBasePath => ResourceTab.Console,
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
        if (QueryHelpers.ParseQuery(query).TryGetValue(DashboardUrls.ResourceSelectionQueryName, out var queryValues))
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

        if (DashboardClient.IsEnabled && _selectedItems.Any(i => i.TelemetryOnly is not null) &&
            ParsePaneMode(NavigationManager.ToBaseRelativePath(NavigationManager.Uri)) is null)
        {
            PaneState.SetMode(ResourcePaneMode.Telemetry);
        }

        UpdateResourceMenuItems();
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
            var lastSelected = await SessionStorage.GetAsync<List<string>>(BrowserStorageKeys.LastSelectedResources);
            if (lastSelected is { Success: true, Value: { } names })
            {
                // An empty list means the user cleared the selection, so the overview asks them to select resources.
                resourceNames = names.Where(IsKnownResourceName).ToList();
                if (names.Count > 0 && resourceNames.Count == 0 && GetPaneRows().FirstOrDefault() is { } fallbackRow)
                {
                    resourceNames.Add(GetResourceName(fallbackRow.Resource));
                }
            }
            else
            {
                resourceNames = GetPaneRows().FirstOrDefault() is { } firstRow ? [GetResourceName(firstRow.Resource)] : [];
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
        IsTelemetryPane ? _telemetryOnlyResources.Where(r => r.Name.Contains(_filter.TextFilter, StringComparisons.UserTextSearch)) : [];

    internal IEnumerable<ResourceGridViewModel> GetPaneRows()
    {
        if (IsTelemetryPane)
        {
            return [];
        }

        var filteredResources = _resourceByName.Values
            .Where(_filter.Filter)
            .Select(r => new ResourceGridViewModel { Resource = r })
            .OrderBy(r => r.Resource.ResourceType)
            .ThenBy(r => r.Resource, ResourceViewModelNameComparer.Instance)
            .ToList();

        // Nested resources are placed under their parent after sorting so children keep their order.
        return ResourceGridViewModel.OrderNestedResources(filteredResources, r => _collapsedResourceNames.Contains(r.PersistentKey))
            .Where(r => !r.IsHidden);
    }

    /// <summary>
    /// Gets the resource list grouped by tag. A resource is listed under each of its tags, and resources without tags
    /// are listed last. The text filter matches either the tag, which lists all of its resources, or the resources.
    /// </summary>
    internal List<ResourceTagGroup> GetTagGroups()
    {
        var textFilter = _filter.TextFilter;
        var resources = _resourceByName.Values
            .Where(r => _filter.Filter(r, textFilter: string.Empty))
            .OrderBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();

        var tagsByResource = TagStore.GetTagsByResource();
        var groups = new List<ResourceTagGroup>();
        foreach (var tag in TagStore.GetAllTags())
        {
            var tagMatchesFilter = textFilter.Length == 0 || tag.Contains(textFilter, StringComparisons.UserTextSearch);
            var taggedResources = resources
                .Where(r => tagsByResource.TryGetValue(r.DisplayName, out var tags) && tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                .Where(r => tagMatchesFilter || r.MatchesFilter(textFilter))
                .ToList();

            if (taggedResources.Count > 0)
            {
                groups.Add(new ResourceTagGroup(tag, taggedResources));
            }
        }

        var untaggedResources = resources
            .Where(r => !tagsByResource.ContainsKey(r.DisplayName))
            .Where(r => textFilter.Length == 0 || r.MatchesFilter(textFilter))
            .ToList();

        if (untaggedResources.Count > 0)
        {
            groups.Add(new ResourceTagGroup(Tag: null, untaggedResources));
        }

        return groups;
    }

    private bool IsTagGroupCollapsed(ResourceTagGroup group) => _collapsedTagGroups.Contains(GetTagGroupKey(group));

    private void ToggleTagGroup(ResourceTagGroup group)
    {
        var key = GetTagGroupKey(group);
        if (!_collapsedTagGroups.Remove(key))
        {
            _collapsedTagGroups.Add(key);
        }
    }

    // Tags can't be empty, so the empty string identifies the group of untagged resources.
    private static string GetTagGroupKey(ResourceTagGroup group) => group.Tag ?? string.Empty;

    private void OnTagsChanged() => _ = InvokeAsync(StateHasChanged);

    private List<StateCount> GetStateCounts()
    {
        return _resourceByName.Values
            .Where(r => !r.IsParameter && !r.IsResourceHidden(_filter.ShowHiddenResources))
            .GroupBy(r => r.State ?? string.Empty, StringComparers.ResourceState)
            .Select(g => new StateCount(g.Key, g.Count(), g.First()))
            .OrderByDescending(s => s.Count)
            .ThenBy(s => s.State, StringComparers.ResourceState)
            .ToList();
    }

    private bool IsStateChipSelected(string state)
    {
        var visibleStates = _filter.ResourceStatesToVisibility.Where(kvp => kvp.Value).Select(kvp => kvp.Key).ToList();
        return visibleStates.Count == 1 && string.Equals(visibleStates[0], state, StringComparisons.ResourceState);
    }

    private void ToggleStateChip(string state)
    {
        // Clicking a state chip shows only that state. Clicking the selected chip again shows every state.
        var showOnlyThisState = !IsStateChipSelected(state);
        foreach (var key in _filter.ResourceStatesToVisibility.Keys)
        {
            _filter.ResourceStatesToVisibility[key] = !showOnlyThisState || string.Equals(key, state, StringComparisons.ResourceState);
        }
    }

    private List<MenuButtonItem> GetFilterMenuItems()
    {
        var items = new List<MenuButtonItem>();
        AddFilterGroup(items, "types", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesResourceTypesHeader)], _filter.ResourceTypesToVisibility);
        AddFilterGroup(items, "states", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesResourceStatesHeader)], _filter.ResourceStatesToVisibility);
        AddFilterGroup(items, "health", ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourcesDetailsHealthStateProperty)], _filter.ResourceHealthStatusesToVisibility);
        return items;
    }

    private void AddFilterGroup(List<MenuButtonItem> items, string groupId, string header, ConcurrentDictionary<string, bool> values)
    {
        if (items.Count > 0)
        {
            items.Add(new MenuButtonItem { IsDivider = true, RenderKey = $"{groupId}-divider" });
        }

        items.Add(new MenuButtonItem
        {
            IsGroupHeader = true,
            Text = header,
            Id = $"resource-filter-{groupId}-header",
            RenderKey = $"{groupId}-header"
        });

        // The menu is regenerated after every toggle and the toggled item is refocused by id, so ids
        // must be stable across regenerations. Values can contain characters that aren't valid in an
        // id, so use the item's sorted position instead.
        // OrderBy doesn't use thread safe APIs on ConcurrentDictionary. Call ToArray first.
        var sortedValues = values.ToArray().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToList();

        // A menu checkbox can't show an indeterminate state, so "All" is checked only when every value
        // is visible. Checking it shows every value; unchecking it hides every value.
        items.Add(CreateFilterMenuItem(
            id: $"resource-filter-{groupId}-all",
            renderKey: $"{groupId}-all",
            text: ControlsStringsLoc[nameof(ControlsStrings.LabelAll)],
            isChecked: sortedValues.All(pair => pair.Value),
            onCheckedChanged: isChecked =>
            {
                foreach (var key in values.Keys)
                {
                    values[key] = isChecked;
                }
            }));

        for (var i = 0; i < sortedValues.Count; i++)
        {
            var (key, isVisible) = sortedValues[i];
            items.Add(CreateFilterMenuItem(
                id: $"resource-filter-{groupId}-{i}",
                renderKey: $"{groupId}:{key}",
                text: string.IsNullOrEmpty(key) ? ResourcesLoc[nameof(Dashboard.Resources.Resources.ResourceFilterOptionEmpty)] : key,
                isChecked: isVisible,
                onCheckedChanged: isChecked => values[key] = isChecked));
        }
    }

    private MenuButtonItem CreateFilterMenuItem(string id, string renderKey, string text, bool isChecked, Action<bool> onCheckedChanged)
    {
        return new MenuButtonItem
        {
            Id = id,
            RenderKey = renderKey,
            Text = text,
            Icon = s_checkmarkIcon,
            Role = MenuItemRole.Checkbox,
            Checked = isChecked,
            OnCheckedChanged = value =>
            {
                onCheckedChanged(value);

                // The toggle comes from the menu component, so re-render the pane to apply the filter.
                StateHasChanged();
                return Task.CompletedTask;
            }
        };
    }

    private bool NoFiltersSet =>
        _filter.ResourceTypesToVisibility.Values.All(v => v) &&
        _filter.ResourceStatesToVisibility.Values.All(v => v) &&
        _filter.ResourceHealthStatusesToVisibility.Values.All(v => v);

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
                UpdatePaneMenuItems();
            }));
    }

    private void UpdateResourceMenuItems()
    {
        _highlightedCommands.Clear();
        _resourceMenuItems.Clear();

        if (SelectedResource is not { } resource)
        {
            return;
        }

        _highlightedCommands.AddRange(resource.Commands
            .Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden)
            .Take(DashboardUIHelpers.MaxHighlightedCommands));

        ResourceMenuBuilder.AddMenuItems(
            _resourceMenuItems,
            resource,
            _resourceByName,
            onViewDetails: EventCallback.Empty,
            EventCallback.Factory.Create<CommandViewModel>(this, command => ExecuteResourceCommandAsync(resource, command)),
            (r, command) => DashboardCommandExecutor.IsExecuting(r.Name, command.Name),
            showViewDetails: false,
            showTerminalItem: true,
            showConsoleLogsItem: false,
            showUrls: false);
    }

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
        => DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);

    internal string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

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

    internal IReadOnlyList<ResourceKey> GetPaneTelemetryKeys() => IsTelemetryPane
        ? _telemetryOnlyResources.Select(r => r.Resource.ResourceKey).Distinct().ToList()
        : _resourceByName.Values.Select(GetTelemetryResource).OfType<OtlpResource>().Select(r => r.ResourceKey).Distinct().ToList();

    private string GetPaneCountText(int count) => string.Format(CultureInfo.CurrentCulture,
        Loc[IsTelemetryPane
            ? count == 1 ? nameof(Resources.Layout.TelemetryPaneCountSingular) : nameof(Resources.Layout.TelemetryPaneCountPlural)
            : count == 1 ? nameof(Resources.Layout.ResourcePaneCountSingular) : nameof(Resources.Layout.ResourcePaneCountPlural)], count);

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
        if (IsTelemetryPane && tab is ResourceTab.Overview or ResourceTab.Console)
        {
            return false;
        }

        if (selection.Count == 0)
        {
            // Standalone keeps Metrics reachable without a selection so the page can prompt for a service.
            // AppHost mode only offers Metrics once resources have been selected.
            return tab is not ResourceTab.Metrics || IsTelemetryPane;
        }

        // With several selected resources, a tab is available when any of them supports it.
        return selection.Any(item => IsTabAvailable(tab, item));
    }

    private bool IsTabAvailable(ResourceTab tab, SelectedResourceItem item)
    {
        if (item.Resource is { } resource)
        {
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
                ResourceTab.Console => DashboardUrls.ConsoleLogsUrl(),
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
            ResourceTab.Console => DashboardUrls.ConsoleLogsUrl(resourceName),
            ResourceTab.StructuredLogs => DashboardUrls.StructuredLogsUrl(resourceName),
            ResourceTab.Traces => DashboardUrls.TracesUrl(resourceName),
            ResourceTab.Metrics => DashboardUrls.MetricsUrl(resourceName),
            _ => throw new InvalidOperationException($"Unexpected tab: {tab}")
        };
    }

    /// <summary>
    /// Gets the URL that selects the given resources on the current tab, falling back to the overview when none of
    /// the resources support the current tab.
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
        else if (!IsTelemetryPane && (selection.Any(i => i.Resource is not null) || selection.Count == 0))
        {
            tab = ResourceTab.Overview;
        }
        else
        {
            // Resources that only send telemetry don't have an overview.
            tab = ResourceTab.StructuredLogs;
        }

        return AddPaneToUrl(GetTabUrl(tab, resourceNames, RouteResourceName));
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
            var toggled = _selectedResourceNames.ToList();
            if (toggled.RemoveAll(n => string.Equals(n, resourceName, StringComparisons.ResourceName)) == 0)
            {
                toggled.Add(resourceName);
            }

            selection = toggled;
            _selectionAnchor = resourceName;
        }
        else
        {
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

    // A single selected resource is shown by its row alone, so a one-resource tag doesn't duplicate the highlight.
    private bool IsTagGroupSelected(List<string> groupResourceNames) =>
        groupResourceNames.Count > 1 &&
        groupResourceNames.Count == _selectedResourceNames.Count &&
        groupResourceNames.All(IsSelected);

    private void OnTagGroupClick(MouseEventArgs e, List<string> groupResourceNames)
    {
        if (groupResourceNames.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> selection;
        if (e.CtrlKey || e.MetaKey)
        {
            // Like rows, a modifier click toggles the tag's resources in the current selection.
            var toggled = _selectedResourceNames.ToList();
            if (groupResourceNames.All(IsSelected))
            {
                toggled.RemoveAll(n => groupResourceNames.Contains(n, StringComparers.ResourceName));
            }
            else
            {
                toggled.AddRange(groupResourceNames.Where(n => !IsSelected(n)));
            }

            selection = toggled;
        }
        else
        {
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
        NavigationManager.NavigateTo(GetSelectionUrl([]));
    }

    /// <summary>
    /// Gets the names of the resources that "All resources" stands for, in the order of the resource list. The pane's
    /// filters don't apply because they only narrow down the list.
    /// </summary>
    internal List<string> GetAllResourceNames()
    {
        if (IsTelemetryPane)
        {
            return _telemetryOnlyResources.Select(r => r.Name).ToList();
        }

        var names = _resourceByName.Values
            .Where(r => !r.IsParameter && !r.IsResourceHidden(_filter.ShowHiddenResources))
            .OrderBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .Select(GetResourceName)
            .ToList();

        return names;
    }

    private List<string> GetVisibleRowNames()
    {
        List<string> names;
        if (!IsTelemetryPane && PaneState.Mode == ResourcePaneMode.Tags)
        {
            // A resource with several tags is listed several times. Ranges use its first position.
            var showAllGroups = ViewportInformation.IsDesktop && _isPaneCollapsed;
            names = GetTagGroups()
                .Where(g => showAllGroups || !IsTagGroupCollapsed(g))
                .SelectMany(g => g.Resources)
                .Select(GetResourceName)
                .Distinct(StringComparers.ResourceName)
                .ToList();
        }
        else
        {
            names = GetPaneRows().Select(r => GetResourceName(r.Resource)).ToList();
        }

        names.AddRange(GetVisibleTelemetryResources().Select(r => r.Name));
        return names;
    }

    private bool IsSelected(string resourceName) => _selectedResourceNames.Contains(resourceName, StringComparers.ResourceName);

    private string? GetRowAriaCurrent(bool isSelected) => isSelected ? (IsMultiSelection ? "true" : "page") : null;

    private static string GetTabId(ResourceTab tab) => $"tab-{tab}";

    private void OnTabChanged(FluentTab? newTab)
    {
        // FluentTabs also raises this when a tab is disposed, for example when leaving the resources view, and
        // when it syncs with ActiveTabId after a navigation. Only a change made while this layout shows the current
        // URL is a user selecting a tab.
        if (!string.Equals(NavigationManager.Uri, _lastLocation, StringComparison.Ordinal)
            || newTab?.Id is not { } id
            || !id.StartsWith("tab-", StringComparison.Ordinal)
            || !Enum.TryParse<ResourceTab>(id["tab-".Length..], out var tab)
            || tab == CurrentTab)
        {
            return;
        }

        // Tabs map to pages, so a tab change is a navigation. The tab URL keeps the current resource selection.
        if (GetTabs().FirstOrDefault(t => t.Tab == tab && t.IsVisible)?.Href is { } href)
        {
            NavigationManager.NavigateTo(href);
        }
    }

    private IEnumerable<TabItem> GetTabs()
    {
        var resource = SelectedResource;
        var isTelemetryOnly = SelectedTelemetryOnlyResource is not null;
        var isMultiSelection = IsMultiSelection;

        // Every tab is always rendered and hidden when it doesn't apply. Removing a FluentTab makes FluentTabs
        // activate its first tab, which would navigate away from the current page.
        var errorCount = _selectedItems.Sum(i => i.Resource is { } r ? GetUnviewedErrorCount(r) : 0);
        yield return CreateTab(ResourceTab.Overview, Loc[nameof(Resources.Layout.ResourceTabOverview)], new Icons.Regular.Size24.Board(),
            isVisible: !IsTelemetryPane && (resource is not null || isMultiSelection || _selectedItems.Count == 0));
        yield return CreateTab(ResourceTab.Console, Loc[nameof(Resources.Layout.NavMenuConsoleLogsTab)], new Icons.Regular.Size24.SlideText(),
            isVisible: !IsTelemetryPane && !isTelemetryOnly);
        yield return CreateTab(ResourceTab.StructuredLogs, StructuredLogsLoc[nameof(Resources.StructuredLogs.StructuredLogsHeader)], new Icons.Regular.Size24.SlideTextSparkle(),
            isVisible: true, errorCount);
        yield return CreateTab(ResourceTab.Traces, Loc[nameof(Resources.Layout.NavMenuTracesTab)], new Icons.Regular.Size24.GanttChart(),
            isVisible: true);
        yield return CreateTab(ResourceTab.Metrics, Loc[nameof(Resources.Layout.NavMenuMetricsTab)], new Icons.Regular.Size24.ChartMultiple(),
            isVisible: IsTelemetryPane || _selectedItems.Count > 0 || CurrentTab == ResourceTab.Metrics);

        TabItem CreateTab(ResourceTab tab, string text, Icon icon, bool isVisible, int errorCount = 0)
        {
            var isAvailable = IsTabAvailable(tab);
            return new TabItem(tab, text, icon, isVisible, isAvailable ? GetTabUrl(tab) : null, errorCount);
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
                Text = tab.Text,
                StartIcon = tab.Icon,
                Icon = s_checkmarkIcon,
                Role = MenuItemRole.Radio,
                Checked = tab.Tab == CurrentTab,
                IsDisabled = tab.Href is null,
                Tooltip = tab.Href is null ? Loc[nameof(Resources.Layout.ResourceTabUnavailable)].Value : null,
                OnClick = tab.Href is { } href
                    ? () => { NavigationManager.NavigateTo(href); return Task.CompletedTask; }
                    : null
            });
        }

        return items;
    }

    internal static string GetStateClass(ResourceViewModel resource) => resource.KnownState switch
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
        _cts.Cancel();
        TagStore.Changed -= OnTagsChanged;
        _logsSubscription?.Dispose();
        _telemetryResourcesSubscription?.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        _cts.Dispose();

        if (_jsModule is { } module)
        {
            try
            {
                await module.InvokeVoidAsync("unregisterPaneResizer", _layoutElement);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is gone, so the browser has already discarded the handlers.
            }

            await JSInteropHelpers.SafeDisposeAsync(module);
        }

        _selfRef?.Dispose();
    }

    internal enum ResourceTab
    {
        Overview,
        Console,
        StructuredLogs,
        Traces,
        Metrics
    }

    internal sealed record ResourceLocation(ResourceTab Tab, string? RouteResourceName, IReadOnlyList<string> SelectedResourceNames, bool KeepSelection);

    private sealed record TelemetryOnlyResource(string Name, OtlpResource Resource);

    private sealed record SelectedResourceItem(string Name, ResourceViewModel? Resource, TelemetryOnlyResource? TelemetryOnly);

    internal sealed record ResourceTagGroup(string? Tag, List<ResourceViewModel> Resources);

    private sealed record StateCount(string State, int Count, ResourceViewModel Example);

    private sealed record TabItem(ResourceTab Tab, string Text, Icon Icon, bool IsVisible, string? Href, int ErrorCount);
}
