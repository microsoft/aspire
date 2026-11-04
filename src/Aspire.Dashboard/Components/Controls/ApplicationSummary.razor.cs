// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Model.ResourceGraph;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Summarizes the app: overall health, telemetry totals, a tile per resource, and recent problems. The resource
/// overview shows it when all resources are selected.
/// </summary>
public sealed partial class ApplicationSummary : ComponentBase, IAsyncDisposable
{
    private const string GraphContainerId = "overviewResourcesGraphContainer";
    private const string ResourceContextMenuHeaderId = "application-summary-resource-context-menu-header";
    private const int RecentItemCount = 5;
    private const int InsightItemCount = 4;
    internal const int ActivityBucketCount = 30;
    // Bounds the work done on each refresh; the activity window only needs the most recent spans.
    private const int MaxActivitySpans = 5000;
    private const int MaxActivityErrorLogs = 500;
    internal static readonly TimeSpan s_activityBucketSize = TimeSpan.FromSeconds(30);

    // Metrics arrive every few seconds from each resource, so coalesce telemetry notifications to bound the
    // number of repository queries the page runs.
    private static readonly TimeSpan s_telemetryRefreshDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_ageRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly List<TelemetryFilter> s_errorLogFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Error) }];
    private static readonly List<TelemetryFilter> s_warningLogFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.Equals, Value = nameof(LogLevel.Warning) }];
    private static readonly FieldTelemetryFilter s_failedTraceFilter = new() { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) };
    private static readonly string s_failedTracesUrl = DashboardUrls.TracesUrl(filters: TelemetryFilterFormatter.SerializeFiltersToString([s_failedTraceFilter]));

    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private Task? _resourceSubscriptionTask;
    private Task? _ageRefreshTask;
    private Subscription? _logsSubscription;
    private Subscription? _tracesSubscription;
    private Subscription? _metricsSubscription;
    private Dictionary<ResourceKey, int>? _unviewedErrorCounts;
    private HomeTelemetry? _telemetry;
    private int _telemetryRefreshPending;
    private bool _isLoaded;
    private IReadOnlyList<string>? _scopedResourceNames;
    private DotNetObjectReference<ApplicationSummaryInterop>? _resourcesInteropReference;
    private IJSObjectReference? _jsModule;
    private bool _graphInitializing;
    private bool _graphInitialized;
    private bool _isDisposing;
    private AspireMenu? _contextMenu;
    private bool _contextMenuOpen;
    private readonly List<MenuButtonItem> _contextMenuItems = [];
    /// <summary>
    /// Gets or sets the resources to summarize, by name. <see langword="null"/> summarizes every resource.
    /// </summary>
    [Parameter]
    public IReadOnlyList<string>? ResourceNames { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Columns> ColumnsLoc { get; init; }

    [Inject]
    public required ILogger<ApplicationSummary> Logger { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required ResourceMenuBuilder ResourceMenuBuilder { get; init; }

    [Inject]
    public required DashboardViewPreferences ViewPreferences { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Dialogs> DialogsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Layout> LayoutLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;
    private IReadOnlyList<IconToggleOption<GroupOverview>> OverviewOptions =>
        new IconToggleOption<GroupOverview>[]
        {
            new(GroupOverview.Cards, new Icons.Regular.Size20.Grid(), DialogsLoc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewCards)]),
            new(GroupOverview.Summary, new Icons.Regular.Size20.Home(), DialogsLoc[nameof(Dashboard.Resources.Dialogs.SettingsDialogGroupOverviewSummary)]),
            new(GroupOverview.Graph, new Icons.Regular.Size20.ShareAndroid(), Loc[nameof(Dashboard.Resources.Resources.GraphPageTitle)])
        }
        .Where(option => option.Value != GroupOverview.Graph || ViewPreferences.IsGraphEnabled)
        .ToList();

    private IconToggleOption<GroupOverview> GetCurrentOverviewOption() =>
        OverviewOptions.First(option => option.Value == ViewPreferences.GroupOverview);

    private int GetMissingParameterCount() =>
        _resourceByName.Values.Count(resource => resource.HasMissingParameterValueState() && IsInScope(resource));

    private string GetMissingParametersUrl() =>
        DashboardUrls.AddResourceSelection(
            DashboardUrls.ResourceListUrl(),
            _resourceByName.Values
                .Where(resource => resource.HasMissingParameterValueState() && IsInScope(resource))
                .OrderBy(resource => resource.DisplayName, StringComparers.ResourceName)
                .Select(resource => resource.Name));

    private IList<MenuButtonItem> GetOverviewMenuItems() =>
        OverviewOptions.Select(option => new MenuButtonItem
        {
            Text = option.Text,
            StartIcon = option.Icon,
            Role = MenuItemRole.Radio,
            Checked = option.Value == ViewPreferences.GroupOverview,
            OnClick = () => ViewPreferences.SetGroupOverviewAsync(option.Value),
            RenderKey = option.Value
        }).ToList();

    private string CardDensityClass => ViewPreferences.Density switch
    {
        ResourceListDensity.Spacious => "resource-card-grid-spacious",
        ResourceListDensity.Compact => "resource-card-grid-compact",
        _ => "resource-card-grid-comfortable"
    };

    private string? GetResourceStartAge(ResourceViewModel resource) =>
        resource.StartTimeStamp is { } startTimeStamp
            ? DashboardRunSelect.FormatRunAge(new DateTimeOffset(startTimeStamp), TimeProvider.GetUtcNow(), LayoutLoc)
            : null;

    // The card shows how long ago the resource started. The tooltip adds the exact time: "5 minutes ago (10:34:56 AM)".
    private string? GetResourceStartTimeTooltip(ResourceViewModel resource)
    {
        if (resource.StartTimeStamp is not { } startTimeStamp)
        {
            return null;
        }

        // The resource server's time stamp has second precision, so no milliseconds.
        return $"{GetResourceStartAge(resource)} ({FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, startTimeStamp)})";
    }

    protected override async Task OnInitializedAsync()
    {
        ViewPreferences.Changed += OnViewPreferencesChanged;
        await ViewPreferences.EnsureLoadedAsync();

        if (!DashboardClient.IsEnabled)
        {
            return;
        }

        _unviewedErrorCounts = TelemetryRepository.GetResourceUnviewedErrorLogsCount();
        _logsSubscription = TelemetryRepository.OnNewLogs(null, SubscriptionType.Other, OnTelemetryChanged);
        _tracesSubscription = TelemetryRepository.OnNewTraces(null, SubscriptionType.Other, OnTelemetryChanged);
        _metricsSubscription = TelemetryRepository.OnNewMetrics(null, SubscriptionType.Other, OnTelemetryChanged);
        QueueTelemetryRefresh();

        var (snapshot, subscription) = await DataSource.ResourceRepository.SubscribeResourcesAsync(_cts.Token);
        foreach (var resource in snapshot)
        {
            _resourceByName[resource.Name] = resource;
        }
        _isLoaded = true;

        _ageRefreshTask = RefreshAgesPeriodicallyAsync(_cts.Token);

        _resourceSubscriptionTask = Task.Run(async () =>
        {
            await foreach (var changes in subscription.WithCancellation(_cts.Token).ConfigureAwait(false))
            {
                foreach (var (changeType, resource) in changes)
                {
                    if (changeType == ResourceViewModelChangeType.Upsert)
                    {
                        _resourceByName[resource.Name] = resource;
                    }
                    else if (changeType == ResourceViewModelChangeType.Delete)
                    {
                        _resourceByName.TryRemove(resource.Name, out _);
                    }
                }

                await InvokeAsync(async () =>
                {
                    StateHasChanged();
                    await UpdateResourceGraphResourcesAsync();
                }).ConfigureAwait(false);
            }
        });
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_isDisposing || ViewPreferences.GroupOverview != GroupOverview.Graph || _graphInitialized || _graphInitializing)
        {
            return;
        }

        _graphInitializing = true;
        try
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", $"/{Assets["js/app-resourcegraph.js"]}");
            _resourcesInteropReference = DotNetObjectReference.Create(new ApplicationSummaryInterop(this));

            var graphIcons = new GraphIconsDto(
                new GraphMenuIconDto(
                    Path: ResourceGraphMapper.GetIconPathData(new Icons.Regular.Size16.Settings()),
                    LabelFormat: Loc[nameof(Dashboard.Resources.Resources.ResourcesGraphResourceActionsButton)].Value),
                new GraphHiddenIconDto(
                    Path: ResourceGraphMapper.GetIconPathData(new Icons.Regular.Size16.EyeOff()),
                    Tooltip: ColumnsLoc[nameof(Dashboard.Resources.Columns.HiddenResourceIconTooltip)].Value));

            await _jsModule.InvokeVoidAsync("initializeResourcesGraph", _resourcesInteropReference, graphIcons);
            _graphInitialized = true;
            await UpdateResourceGraphResourcesAsync();
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Application overview graph rendering stopped because the circuit disconnected.");
        }
        catch (ObjectDisposedException ex) when (_isDisposing)
        {
            Logger.LogDebug(ex, "Application overview graph rendering stopped because the component was disposed.");
        }
        finally
        {
            _graphInitializing = false;
        }
    }

    protected override void OnParametersSet()
    {
        // The overview passes the current selection. When it changes, the telemetry shown is for other resources.
        if (_scopedResourceNames is null != ResourceNames is null ||
            (_scopedResourceNames is not null && ResourceNames is not null && !_scopedResourceNames.SequenceEqual(ResourceNames, StringComparers.ResourceName)))
        {
            var isFirstSet = _scopedResourceNames is null && !_isLoaded;
            _scopedResourceNames = ResourceNames?.ToList();
            if (!isFirstSet && DashboardClient.IsEnabled)
            {
                _telemetry = null;
                QueueTelemetryRefresh();
            }
        }
    }

    private Task OnTelemetryChanged()
    {
        QueueTelemetryRefresh();
        return Task.CompletedTask;
    }

    private void QueueTelemetryRefresh()
    {
        // Single-flight: the gate (1) is held through delay, load and publish. Notifications that
        // arrive while a refresh is running set the dirty flag (2) so exactly one trailing refresh runs.
        if (Interlocked.CompareExchange(ref _telemetryRefreshPending, 1, 0) != 0)
        {
            Interlocked.CompareExchange(ref _telemetryRefreshPending, 2, 1);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    await Task.Delay(s_telemetryRefreshDelay, _cts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _telemetryRefreshPending, 1);

                    var telemetry = await LoadTelemetryAsync(_cts.Token).ConfigureAwait(false);
                    var unviewedErrorCounts = TelemetryRepository.GetResourceUnviewedErrorLogsCount();
                    await InvokeAsync(() =>
                    {
                        _telemetry = telemetry;
                        _unviewedErrorCounts = unviewedErrorCounts;
                        StateHasChanged();
                    }).ConfigureAwait(false);
                }
                while (Interlocked.CompareExchange(ref _telemetryRefreshPending, 0, 1) == 2);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);
                Logger.LogWarning(ex, "Error loading telemetry for the home page.");
            }
        }, CancellationToken.None);
    }

    private async Task<HomeTelemetry> LoadTelemetryAsync(CancellationToken cancellationToken)
    {
        var repository = TelemetryRepository;

        // Telemetry queries take resource keys, where an empty list means every resource. A selection of resources
        // that send no telemetry has nothing to summarize.
        var resourceKeys = GetScopedResourceKeys();
        if (resourceKeys is { Count: 0 })
        {
            return HomeTelemetry.Empty;
        }
        var queryKeys = resourceKeys ?? [];

        var allLogs = await repository.GetLogSummariesAsync(CreateLogsContext([], 0, 0), cancellationToken).ConfigureAwait(false);
        var warningLogs = await repository.GetLogSummariesAsync(CreateLogsContext(s_warningLogFilters, 0, 0), cancellationToken).ConfigureAwait(false);
        var errorLogs = await repository.GetLogSummariesAsync(CreateLogsContext(s_errorLogFilters, 0, 0), cancellationToken).ConfigureAwait(false);

        // Results are ordered oldest first, so read the last page to get the most recent entries.
        var errorCount = errorLogs.TotalItemCount;
        var recentErrors = errorCount > 0
            ? (await repository.GetLogSummariesAsync(CreateLogsContext(s_errorLogFilters, Math.Max(0, errorCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).Items
            : [];

        var allTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest([], 0, 0), cancellationToken).ConfigureAwait(false);
        var failedTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest([s_failedTraceFilter], 0, 0), cancellationToken).ConfigureAwait(false);
        var failedTraceCount = failedTraces.PagedResult.TotalItemCount;
        var recentFailedTraces = failedTraceCount > 0
            ? (await repository.GetTraceSummariesAsync(CreateTracesRequest([s_failedTraceFilter], Math.Max(0, failedTraceCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).PagedResult.Items
            : [];

        var telemetryResources = repository.GetResources();
        var instrumentCount = 0;
        var meterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in telemetryResources.Where(r => resourceKeys is null || resourceKeys.Contains(r.ResourceKey)))
        {
            foreach (var instrument in repository.GetInstrumentSummaries(resource.ResourceKey))
            {
                instrumentCount++;
                meterNames.Add(instrument.Parent.Name);
            }
        }

        var activity = await LoadActivityAsync(repository, telemetryResources, queryKeys, errorCount, cancellationToken).ConfigureAwait(false);

        return new HomeTelemetry
        {
            Activity = activity,
            LogCount = allLogs.TotalItemCount,
            WarningLogCount = warningLogs.TotalItemCount,
            ErrorLogCount = errorCount,
            RecentErrors = recentErrors.AsEnumerable().Reverse().ToList(),
            TraceCount = allTraces.PagedResult.TotalItemCount,
            FailedTraceCount = failedTraceCount,
            RecentFailedTraces = recentFailedTraces.AsEnumerable().Reverse().ToList(),
            InstrumentCount = instrumentCount,
            MeterCount = meterNames.Count
        };

        GetLogsContext CreateLogsContext(List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = queryKeys,
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };

        GetTracesRequest CreateTracesRequest(List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = queryKeys,
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };
    }

    /// <summary>
    /// Aggregates recent incoming requests (server and consumer spans) into fixed time buckets, plus per-resource
    /// and per-endpoint breakdowns used by the activity chart and insight cards.
    /// </summary>
    private async Task<HomeActivity> LoadActivityAsync(ITelemetryRepository repository, List<OtlpResource> telemetryResources, List<ResourceKey> resourceKeys, int errorLogCount, CancellationToken cancellationToken)
    {
        // Align the window to bucket boundaries so bars don't shift between refreshes.
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var windowEnd = new DateTime(((now.Ticks / s_activityBucketSize.Ticks) + 1) * s_activityBucketSize.Ticks, DateTimeKind.Utc);
        var windowStart = windowEnd - (s_activityBucketSize * ActivityBucketCount);

        // Only the window's request spans, with the few fields used here. This refreshes about once a second while
        // telemetry arrives, so it must not materialize traces.
        var requestSpans = await repository.GetRequestSpansAsync(new GetRequestSpansRequest
        {
            ResourceKeys = resourceKeys,
            StartTime = windowStart,
            EndTime = windowEnd,
            MaxCount = MaxActivitySpans
        }, cancellationToken).ConfigureAwait(false);
        var spans = requestSpans.Items;

        var resourceNames = new Dictionary<ResourceKey, string>();
        string GetName(OtlpResource resource)
        {
            if (!resourceNames.TryGetValue(resource.ResourceKey, out var name))
            {
                name = OtlpHelpers.GetResourceName(resource, telemetryResources);
                resourceNames[resource.ResourceKey] = name;
            }
            return name;
        }

        var okBuckets = new int[ActivityBucketCount];
        var errorBuckets = new int[ActivityBucketCount];
        var durations = new List<double>();
        var byResource = new Dictionary<ResourceKey, ResourceActivity>();
        var byEndpoint = new Dictionary<(ResourceKey, string), List<double>>();

        foreach (var span in spans)
        {
            var index = (int)((span.StartTime - windowStart).Ticks / s_activityBucketSize.Ticks);
            var isError = span.Status == OtlpSpanStatusCode.Error;
            var durationMs = span.Duration.TotalMilliseconds;
            (isError ? errorBuckets : okBuckets)[index]++;
            durations.Add(durationMs);

            var resource = span.Resource;
            if (!byResource.TryGetValue(resource.ResourceKey, out var resourceActivity))
            {
                resourceActivity = new ResourceActivity(GetName(resource));
                byResource[resource.ResourceKey] = resourceActivity;
            }
            resourceActivity.Requests++;
            if (isError)
            {
                resourceActivity.ErrorSpans++;
            }

            var endpointKey = (resource.ResourceKey, GetEndpointLabel(span));
            if (!byEndpoint.TryGetValue(endpointKey, out var endpointDurations))
            {
                endpointDurations = [];
                byEndpoint[endpointKey] = endpointDurations;
            }
            endpointDurations.Add(durationMs);
        }

        // Error logs count toward a resource's hotspot score alongside failed requests.
        var logCount = Math.Min(errorLogCount, MaxActivityErrorLogs);
        if (logCount > 0)
        {
            var errorLogs = await repository.GetLogSummariesAsync(new GetLogsContext { ResourceKeys = resourceKeys, StartIndex = errorLogCount - logCount, Count = logCount, Filters = s_errorLogFilters }, cancellationToken).ConfigureAwait(false);
            foreach (var log in errorLogs.Items)
            {
                if (log.TimeStamp < windowStart)
                {
                    continue;
                }

                if (!byResource.TryGetValue(log.Resource.ResourceKey, out var resourceActivity))
                {
                    resourceActivity = new ResourceActivity(GetName(log.Resource));
                    byResource[log.Resource.ResourceKey] = resourceActivity;
                }
                resourceActivity.ErrorLogs++;
            }
        }

        var totalRequests = okBuckets.Sum() + errorBuckets.Sum();
        var totalErrors = errorBuckets.Sum();
        var windowMinutes = s_activityBucketSize.TotalMinutes * ActivityBucketCount;
        if (requestSpans.TotalItemCount > spans.Count && spans.Count > 0)
        {
            // Only the newest MaxActivitySpans were sampled, so the rate must be computed over the
            // interval those spans actually cover rather than the full window.
            var oldestSampled = spans.Min(s => s.StartTime);
            if (oldestSampled > windowStart)
            {
                windowMinutes = Math.Max(s_activityBucketSize.TotalMinutes, (windowEnd - oldestSampled).TotalMinutes);
            }
        }

        return new HomeActivity
        {
            WindowStart = windowStart,
            OkBuckets = okBuckets,
            ErrorBuckets = errorBuckets,
            TotalRequests = totalRequests,
            RequestsPerMinute = totalRequests / windowMinutes,
            ErrorRate = totalRequests == 0 ? null : (double)totalErrors / totalRequests,
            P95 = durations.Count == 0 ? null : TimeSpan.FromMilliseconds(Percentile(durations, 0.95)),
            ByResource = byResource,
            SlowestEndpoints = byEndpoint
                .Select(e => new EndpointInsight(byResource[e.Key.Item1].Name, e.Key.Item2, e.Value.Count, TimeSpan.FromMilliseconds(Percentile(e.Value, 0.95))))
                .OrderByDescending(e => e.P95)
                .Take(InsightItemCount)
                .ToList(),
            ErrorHotspots = byResource.Values
                .Where(r => r.TotalErrors > 0)
                .OrderByDescending(r => r.TotalErrors)
                .Take(InsightItemCount)
                .ToList(),
            BusiestResources = byResource.Values
                .Where(r => r.Requests > 0)
                .OrderByDescending(r => r.Requests)
                .Take(InsightItemCount)
                .ToList()
        };
    }

    // Prefer the low-cardinality route template ("GET /api/catalog/{id}") over the span name.
    // See https://opentelemetry.io/docs/specs/semconv/http/http-spans/
    private static string GetEndpointLabel(RequestSpanSummary span)
    {
        var route = span.HttpRoute;
        if (string.IsNullOrEmpty(route))
        {
            return span.Name;
        }

        var method = span.HttpMethod;
        return string.IsNullOrEmpty(method) ? route : $"{method} {route}";
    }

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    /// <summary>
    /// Gets the telemetry keys of the summarized resources, or <see langword="null"/> when every resource is summarized.
    /// </summary>
    private List<ResourceKey>? GetScopedResourceKeys()
    {
        if (_scopedResourceNames is not { } names)
        {
            return null;
        }

        var keys = new List<ResourceKey>();
        foreach (var name in names)
        {
            // Selections name resources as the resource list shows them, which can be a display name or a replica's name.
            var resourceName = ResourceViewModel.TryGetResourceByName(name, _resourceByName, out var resource) ? resource.Name : name;
            if (TelemetryRepository.GetResourceByCompositeName(resourceName) is { } telemetryResource)
            {
                keys.Add(telemetryResource.ResourceKey);
            }
        }

        return keys;
    }

    private bool IsInScope(ResourceViewModel resource) => _scopedResourceNames is not { } names ||
        names.Contains(GetResourceName(resource), StringComparers.ResourceName) ||
        names.Contains(resource.Name, StringComparers.ResourceName);

    private string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    // Like the resources grid, every row reserves room for as many highlighted commands as the busiest resource has, so
    // the action columns line up. At most two are shown; the others are in the menu.
    private static int GetMaxHighlightedCount(List<ResourceViewModel> resources) => Math.Min(
        resources.Select(r => r.Commands.Count(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden)).DefaultIfEmpty(0).Max(),
        DashboardUIHelpers.MaxHighlightedCommands);

    private bool HasMultipleReplicas(ResourceViewModel resource) =>
        _resourceByName.Values.Count(r => !r.IsResourceHidden(showHiddenResources: false) && string.Equals(r.DisplayName, resource.DisplayName, StringComparisons.ResourceName)) >= 2;

    private void NavigateToResource(ResourceViewModel resource) => NavigationManager.NavigateTo(DashboardUrls.ResourceOverviewUrl(GetResourceName(resource)));

    private void OnResourceCardKeyDown(KeyboardEventArgs args, ResourceViewModel resource)
    {
        if (args.Key is "Enter" or " ")
        {
            NavigateToResource(resource);
        }
    }

    private Task ExecuteCommandAsync(ResourceViewModel resource, CommandViewModel command) =>
        DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);

    private async Task ShowContextMenuAsync(string id, int clientX, int clientY)
    {
        if (_contextMenu is not { } contextMenu || !_resourceByName.TryGetValue(id, out var resource))
        {
            return;
        }

        _contextMenuItems.Clear();
        _contextMenuItems.Add(new MenuButtonItem
        {
            Id = ResourceContextMenuHeaderId,
            IsHeader = true,
            Text = GetResourceName(resource),
            Icon = ResourceIconHelpers.GetIconForResource(IconResolver, resource, IconSize.Size16)
        });

        ResourceMenuBuilder.AddMenuItems(
            _contextMenuItems,
            resource,
            _resourceByName,
            EventCallback.Factory.Create(this, () =>
            {
                NavigateToResource(resource);
                return Task.CompletedTask;
            }),
            EventCallback.Factory.Create<CommandViewModel>(this, command => ExecuteCommandAsync(resource, command)),
            (menuResource, command) => DashboardCommandExecutor.IsExecuting(menuResource.Name, command.Name),
            showViewDetails: true,
            showTerminalItem: true,
            showConsoleLogsItem: true,
            showUrls: true);

        await contextMenu.OpenAsync(clientX, clientY);
        StateHasChanged();
    }

    private async Task ContextMenuOpenChangedAsync(bool open)
    {
        _contextMenuOpen = open;
        if (_jsModule is not null)
        {
            await _jsModule.InvokeVoidAsync("updateResourcesGraphContextMenu", open);
        }

        await InvokeAsync(StateHasChanged);
    }

    private Task ExecuteCardCommandAsync(ResourceViewModel resource, CommandViewModel command)
    {
        if (string.Equals(command.Name, CommandViewModel.SetParameterCommand, StringComparisons.CommandName))
        {
            NavigateToResource(resource);
            return Task.CompletedTask;
        }

        return ExecuteCommandAsync(resource, command);
    }

    private List<ResourceViewModel> GetResources()
    {
        return _resourceByName.Values
            .Where(r => !r.IsParameter && !r.IsResourceHidden(showHiddenResources: false) && IsInScope(r))
            .OrderBy(r => NeedsAttention(r) ? 0 : 1)
            .ThenBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();
    }

    private List<ResourceViewModel> GetCardResources()
    {
        return _resourceByName.Values
            .Where(r => !r.IsResourceHidden(showHiddenResources: false) && IsInScope(r))
            .OrderBy(r => r.IsParameter ? 1 : 0)
            .ThenBy(r => NeedsAttention(r) ? 0 : 1)
            .ThenBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();
    }

    private static (string? Value, bool IsSensitive, bool IsUnresolved) GetParameterValue(ResourceViewModel resource)
    {
        var isUnresolved = !resource.IsRunningState();
        if (resource.Properties.TryGetValue(KnownProperties.Parameter.Value, out var property))
        {
            var value = isUnresolved ? null : property.Value.HasStringValue ? property.Value.StringValue : null;
            return (value, property.IsValueSensitive, isUnresolved);
        }

        return (null, false, isUnresolved);
    }

    private async Task UpdateResourceGraphResourcesAsync()
    {
        if (ViewPreferences.GroupOverview != GroupOverview.Graph || !_graphInitialized || _jsModule is null)
        {
            return;
        }

        try
        {
            await _jsModule.InvokeVoidAsync("updateResourcesGraph", GetResourceGraphResources());
        }
        catch (JSDisconnectedException ex)
        {
            Logger.LogDebug(ex, "Application overview graph update stopped because the circuit disconnected.");
        }
        catch (ObjectDisposedException ex) when (_isDisposing)
        {
            Logger.LogDebug(ex, "Application overview graph update stopped because the component was disposed.");
        }
    }

    private List<ResourceDto> GetResourceGraphResources()
    {
        var resources = GetResources();
        return resources
            .Select(resource => ResourceGraphMapper.MapResource(resource, resources, _resourceByName, ColumnsLoc, showHiddenResources: false, IconResolver))
            .ToList();
    }

    private Task ExportResourceGraphAsync()
    {
        return TextVisualizerDialog.OpenDialogAsync(new OpenTextVisualizerDialogOptions
        {
            DialogService = DialogService,
            ValueDescription = Loc[nameof(Dashboard.Resources.Resources.ResourcesGraphExportMermaidButton)],
            MarkdownDescription = Loc[nameof(Dashboard.Resources.Resources.ResourcesGraphExportMermaidDescription)],
            Value = ResourceGraphMermaidExporter.Export(GetResourceGraphResources()),
            DownloadFileName = "resources.mmd",
            FixedFormat = DashboardUIHelpers.PlaintextFormat
        });
    }

    /// <summary>
    /// Gets the health state bucket of a resource, as counted by the health bar.
    /// </summary>
    private HealthBucket GetHealthBucket(ResourceViewModel resource) => NeedsAttention(resource)
        ? HealthBucket.Attention
        : ResourcesLayout.GetStateClass(resource) switch
        {
            "state-success" => HealthBucket.Running,
            "state-pending" => HealthBucket.Pending,
            _ => HealthBucket.Inactive
        };

    private bool NeedsAttention(ResourceViewModel resource)
    {
        return ResourcesLayout.GetStateClass(resource) is "state-error" or "state-warning" || GetUnviewedErrorCount(resource) > 0;
    }

    private int GetUnviewedErrorCount(ResourceViewModel resource)
    {
        return _unviewedErrorCounts is not null &&
            TelemetryRepository.GetResourceByCompositeName(resource.Name) is { } otlpResource &&
            _unviewedErrorCounts.TryGetValue(otlpResource.ResourceKey, out var count)
            ? count
            : 0;
    }

    /// <summary>
    /// Groups resources by the state buckets shown in the health bar. Every resource is counted in exactly one bucket.
    /// </summary>
    private HealthSummary GetHealthSummary(List<ResourceViewModel> resources)
    {
        int running = 0, pending = 0, attention = 0, inactive = 0;
        DateTime? startedAt = null;
        foreach (var resource in resources)
        {
            switch (GetHealthBucket(resource))
            {
                case HealthBucket.Attention:
                    attention++;
                    break;
                case HealthBucket.Running:
                    running++;
                    break;
                case HealthBucket.Pending:
                    pending++;
                    break;
                default:
                    inactive++;
                    break;
            }

            if (resource.CreationTimeStamp is { } created && (startedAt is null || created < startedAt))
            {
                startedAt = created;
            }
        }

        return new HealthSummary(resources.Count, running, pending, attention, inactive, startedAt);
    }

    private string GetHeadline(HealthSummary summary)
    {
        if (summary.Total == 0)
        {
            return Loc[nameof(Dashboard.Resources.Resources.ResourcesNoResources)];
        }

        if (summary.Attention > 0)
        {
            return Loc[nameof(Dashboard.Resources.Resources.HomeHeadlineAttention), summary.Attention, summary.Total];
        }

        if (summary.Pending > 0)
        {
            return Loc[nameof(Dashboard.Resources.Resources.HomeHeadlinePending), summary.Pending, summary.Total];
        }

        return summary.Running == summary.Total
            ? Loc[nameof(Dashboard.Resources.Resources.HomeHeadlineAllRunning), summary.Total]
            : Loc[nameof(Dashboard.Resources.Resources.HomeHeadlineHealthy)];
    }

    private static string GetHeroStateClass(HealthSummary summary) => summary switch
    {
        { Attention: > 0 } => "state-error",
        { Pending: > 0 } => "state-pending",
        { Total: > 0 } => "state-success",
        _ => "state-neutral"
    };

    private static string FormatPercent(int value, int total) => total == 0
        ? "0%"
        : string.Create(CultureInfo.InvariantCulture, $"{value * 100.0 / total:0.##}%");

    private static string FormatCount(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private string GetLogResourceName(OtlpResource resource) => OtlpHelpers.GetResourceName(resource, TelemetryRepository.GetResources());

    // The settings panel changes what the overview shows while it's displayed.
    private void OnViewPreferencesChanged() => _ = InvokeAsync(async () =>
    {
        StateHasChanged();
        await UpdateResourceGraphResourcesAsync();
    });

    // The cards show how long ago a resource started, so re-render now and then to keep that current.
    private async Task RefreshAgesPeriodicallyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_ageRefreshInterval, TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _isDisposing = true;
        ViewPreferences.Changed -= OnViewPreferencesChanged;
        _cts.Cancel();
        _logsSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_ageRefreshTask);
        _resourcesInteropReference?.Dispose();
        if (_jsModule is not null)
        {
            await _jsModule.DisposeAsync().AsTask().ConfigureAwait(false);
        }
        _cts.Dispose();
    }

    private sealed class ApplicationSummaryInterop(ApplicationSummary summary)
    {
        [JSInvokable]
        public Task SelectResource(string id) => summary.SelectResourceAsync(id);

        [JSInvokable]
        public Task ResourceContextMenu(string id, int clientX, int clientY, string? _) =>
            summary.ShowContextMenuAsync(id, clientX, clientY);
    }

    private async Task SelectResourceAsync(string id)
    {
        if (_resourceByName.TryGetValue(id, out var resource))
        {
            await InvokeAsync(() => NavigateToResource(resource));
        }
    }

    private enum HealthBucket
    {
        Attention,
        Pending,
        Running,
        Inactive
    }

    private readonly record struct HealthSummary(int Total, int Running, int Pending, int Attention, int Inactive, DateTime? StartedAt);

    private sealed class HomeActivity
    {
        public required DateTime WindowStart { get; init; }
        public required int[] OkBuckets { get; init; }
        public required int[] ErrorBuckets { get; init; }
        public required int TotalRequests { get; init; }
        public required double RequestsPerMinute { get; init; }
        public required double? ErrorRate { get; init; }
        public required TimeSpan? P95 { get; init; }
        public required Dictionary<ResourceKey, ResourceActivity> ByResource { get; init; }
        public required List<EndpointInsight> SlowestEndpoints { get; init; }
        public required List<ResourceActivity> ErrorHotspots { get; init; }
        public required List<ResourceActivity> BusiestResources { get; init; }
    }

    private sealed class ResourceActivity(string name)
    {
        public string Name { get; } = name;
        public int Requests { get; set; }
        public int ErrorSpans { get; set; }
        public int ErrorLogs { get; set; }
        public int TotalErrors => ErrorSpans + ErrorLogs;
    }

    private sealed record EndpointInsight(string ResourceName, string Label, int Count, TimeSpan P95);

    private sealed class HomeTelemetry
    {
        public static HomeTelemetry Empty { get; } = new()
        {
            Activity = new HomeActivity
            {
                WindowStart = DateTime.UtcNow,
                OkBuckets = new int[ActivityBucketCount],
                ErrorBuckets = new int[ActivityBucketCount],
                TotalRequests = 0,
                RequestsPerMinute = 0,
                ErrorRate = null,
                P95 = null,
                ByResource = [],
                SlowestEndpoints = [],
                ErrorHotspots = [],
                BusiestResources = []
            },
            LogCount = 0,
            WarningLogCount = 0,
            ErrorLogCount = 0,
            RecentErrors = [],
            TraceCount = 0,
            FailedTraceCount = 0,
            RecentFailedTraces = [],
            InstrumentCount = 0,
            MeterCount = 0
        };

        public required HomeActivity Activity { get; init; }
        public required int LogCount { get; init; }
        public required int WarningLogCount { get; init; }
        public required int ErrorLogCount { get; init; }
        public required List<LogSummary> RecentErrors { get; init; }
        public required int TraceCount { get; init; }
        public required int FailedTraceCount { get; init; }
        public required List<TraceSummary> RecentFailedTraces { get; init; }
        public required int InstrumentCount { get; init; }
        public required int MeterCount { get; init; }
    }
}
