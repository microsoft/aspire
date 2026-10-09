// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Landing page that summarizes the app: overall health, telemetry totals, a tile per resource, and recent problems.
/// </summary>
/// <remarks>
/// The root URL used to host the resources page. Links created by older tools, such as
/// <c>/?resource=api</c> or <c>/?view=Graph</c>, are redirected to the equivalent view.
/// </remarks>
public sealed partial class Home : ComponentBase, IAsyncDisposable
{
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
    private static readonly List<TelemetryFilter> s_errorLogFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Error) }];
    private static readonly List<TelemetryFilter> s_warningLogFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.Equals, Value = nameof(LogLevel.Warning) }];
    private static readonly FieldTelemetryFilter s_failedTraceFilter = new() { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) };
    private static readonly string s_failedTracesUrl = DashboardUrls.TracesUrl(filters: TelemetryFilterFormatter.SerializeFiltersToString([s_failedTraceFilter]));

    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly CancellationTokenSource _cts = new();
    private Task? _resourceSubscriptionTask;
    private Subscription? _logsSubscription;
    private Subscription? _tracesSubscription;
    private Subscription? _metricsSubscription;
    private Dictionary<ResourceKey, int>? _unviewedErrorCounts;
    private HomeTelemetry? _telemetry;
    private int _telemetryRefreshPending;
    private bool _isLoaded;

    [SupplyParameterFromQuery(Name = "resource")]
    public string? LegacyResourceName { get; set; }

    [SupplyParameterFromQuery(Name = "view")]
    public string? LegacyViewKind { get; set; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Columns> ColumnsLoc { get; init; }

    [Inject]
    public required ILogger<Home> Logger { get; init; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    protected override async Task OnInitializedAsync()
    {
        if (TryGetLegacyRedirectUrl(LegacyResourceName, LegacyViewKind) is { } redirectUrl)
        {
            NavigationManager.NavigateTo(redirectUrl, new NavigationOptions { ReplaceHistoryEntry = true });
            return;
        }

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

                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        });
    }

    internal static string? TryGetLegacyRedirectUrl(string? resourceName, string? viewKind)
    {
        if (string.Equals(viewKind, nameof(Resources.ResourceViewKind.Graph), StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.GraphUrl();
        }

        if (string.Equals(viewKind, nameof(Resources.ResourceViewKind.Parameters), StringComparison.OrdinalIgnoreCase))
        {
            return DashboardUrls.ParametersUrl();
        }

        if (!string.IsNullOrEmpty(resourceName))
        {
            return DashboardUrls.ResourceOverviewUrl(resourceName);
        }

        return null;
    }

    private Task OnTelemetryChanged()
    {
        QueueTelemetryRefresh();
        return Task.CompletedTask;
    }

    private void QueueTelemetryRefresh()
    {
        if (Interlocked.Exchange(ref _telemetryRefreshPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_telemetryRefreshDelay, _cts.Token).ConfigureAwait(false);
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);

                var telemetry = await LoadTelemetryAsync(_cts.Token).ConfigureAwait(false);
                var unviewedErrorCounts = TelemetryRepository.GetResourceUnviewedErrorLogsCount();
                await InvokeAsync(() =>
                {
                    _telemetry = telemetry;
                    _unviewedErrorCounts = unviewedErrorCounts;
                    StateHasChanged();
                }).ConfigureAwait(false);
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
        foreach (var resource in telemetryResources)
        {
            foreach (var instrument in repository.GetInstrumentSummaries(resource.ResourceKey))
            {
                instrumentCount++;
                meterNames.Add(instrument.Parent.Name);
            }
        }

        var activity = await LoadActivityAsync(repository, telemetryResources, errorCount, cancellationToken).ConfigureAwait(false);

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

        static GetLogsContext CreateLogsContext(List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };

        static GetTracesRequest CreateTracesRequest(List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };
    }

    /// <summary>
    /// Aggregates recent incoming requests (server and consumer spans) into fixed time buckets, plus per-resource
    /// and per-endpoint breakdowns used by the activity chart and insight cards.
    /// </summary>
    private async Task<HomeActivity> LoadActivityAsync(ITelemetryRepository repository, List<OtlpResource> telemetryResources, int errorLogCount, CancellationToken cancellationToken)
    {
        // Align the window to bucket boundaries so bars don't shift between refreshes.
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var windowEnd = new DateTime(((now.Ticks / s_activityBucketSize.Ticks) + 1) * s_activityBucketSize.Ticks, DateTimeKind.Utc);
        var windowStart = windowEnd - (s_activityBucketSize * ActivityBucketCount);

        var spanTotal = (await repository.GetSpansAsync(CreateSpansRequest(0, 0), cancellationToken).ConfigureAwait(false)).PagedResult.TotalItemCount;
        var spanCount = Math.Min(spanTotal, MaxActivitySpans);
        var spans = spanCount > 0
            ? (await repository.GetSpansAsync(CreateSpansRequest(spanTotal - spanCount, spanCount), cancellationToken).ConfigureAwait(false)).PagedResult.Items
            : [];

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
            if (span.Kind is not (OtlpSpanKind.Server or OtlpSpanKind.Consumer) || span.StartTime < windowStart || span.StartTime >= windowEnd)
            {
                continue;
            }

            var index = (int)((span.StartTime - windowStart).Ticks / s_activityBucketSize.Ticks);
            var isError = span.Status == OtlpSpanStatusCode.Error;
            var durationMs = span.Duration.TotalMilliseconds;
            (isError ? errorBuckets : okBuckets)[index]++;
            durations.Add(durationMs);

            var resource = span.Source.Resource;
            if (!byResource.TryGetValue(resource.ResourceKey, out var resourceActivity))
            {
                resourceActivity = new ResourceActivity(GetName(resource));
                byResource[resource.ResourceKey] = resourceActivity;
            }
            resourceActivity.Buckets[index]++;
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
            var errorLogs = await repository.GetLogSummariesAsync(new GetLogsContext { ResourceKeys = [], StartIndex = errorLogCount - logCount, Count = logCount, Filters = s_errorLogFilters }, cancellationToken).ConfigureAwait(false);
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

        static GetSpansRequest CreateSpansRequest(int startIndex, int count) => new()
        {
            ResourceKeys = [],
            StartIndex = startIndex,
            Count = count,
            Filters = []
        };
    }

    // Prefer the low-cardinality route template ("GET /api/catalog/{id}") over the span name.
    // See https://opentelemetry.io/docs/specs/semconv/http/http-spans/
    private static string GetEndpointLabel(OtlpSpan span)
    {
        var route = span.Attributes.GetValue("http.route");
        if (string.IsNullOrEmpty(route))
        {
            return span.Name;
        }

        var method = span.Attributes.GetValue("http.request.method") ?? span.Attributes.GetValue("http.method");
        return string.IsNullOrEmpty(method) ? route : $"{method} {route}";
    }

    private static double Percentile(List<double> values, double percentile)
    {
        values.Sort();
        var index = (int)Math.Ceiling(percentile * values.Count) - 1;
        return values[Math.Clamp(index, 0, values.Count - 1)];
    }

    private int[]? GetResourceActivityBuckets(ResourceViewModel resource)
    {
        return _telemetry?.Activity is { } activity &&
            TelemetryRepository.GetResourceByCompositeName(resource.Name) is { } otlpResource &&
            activity.ByResource.TryGetValue(otlpResource.ResourceKey, out var resourceActivity) &&
            resourceActivity.Requests > 0
            ? resourceActivity.Buckets
            : null;
    }

    private List<ResourceViewModel> GetResources()
    {
        return _resourceByName.Values
            .Where(r => !r.IsParameter && !r.IsResourceHidden(showHiddenResources: false))
            .OrderBy(r => NeedsAttention(r) ? 0 : 1)
            .ThenBy(r => r.ResourceType)
            .ThenBy(r => r, ResourceViewModelNameComparer.Instance)
            .ToList();
    }

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

    private string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    /// <summary>
    /// Groups resources by the state buckets shown in the health bar. Every resource is counted in exactly one bucket.
    /// </summary>
    private HealthSummary GetHealthSummary(List<ResourceViewModel> resources)
    {
        int running = 0, pending = 0, attention = 0, inactive = 0;
        DateTime? startedAt = null;
        foreach (var resource in resources)
        {
            if (NeedsAttention(resource))
            {
                attention++;
            }
            else
            {
                switch (ResourcesLayout.GetStateClass(resource))
                {
                    case "state-success":
                        running++;
                        break;
                    case "state-pending":
                        pending++;
                        break;
                    default:
                        inactive++;
                        break;
                }
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

    private static DisplayedUrl? GetPrimaryUrl(ResourceViewModel resource)
    {
        return ResourceUrlHelpers.GetUrls(resource).FirstOrDefault(u => u.Url is not null);
    }

    private string GetLogResourceName(OtlpResource resource) => OtlpHelpers.GetResourceName(resource, TelemetryRepository.GetResources());

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _logsSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        _cts.Dispose();
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
        public int[] Buckets { get; } = new int[ActivityBucketCount];
        public int Requests { get; set; }
        public int ErrorSpans { get; set; }
        public int ErrorLogs { get; set; }
        public int TotalErrors => ErrorSpans + ErrorLogs;
    }

    private sealed record EndpointInsight(string ResourceName, string Label, int Count, TimeSpan P95);

    private sealed class HomeTelemetry
    {
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
