// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

/// <summary>
/// Summarizes the selected resource: its status, endpoints and relationships, a preview of recent console output and
/// telemetry, and the complete resource details. Each summary links to the tab that shows the full data.
/// When several resources are selected, it displays a summary card for each of them instead.
/// </summary>
public sealed partial class ResourceOverview : ComponentBase, IAsyncDisposable
{
    private const int ConsolePreviewLineCount = 10;
    private const int RecentItemCount = 5;
    private const int MetricsPreviewCount = 8;

    // Telemetry can arrive many times per second. Coalesce updates so the overview queries the repository at most
    // a couple of times per second.
    private static readonly TimeSpan s_telemetryRefreshDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_consoleRenderDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_consoleLoadingTimeout = TimeSpan.FromSeconds(1);

    private readonly object _consoleLock = new();
    private readonly List<ConsolePreviewLine> _consoleLines = new();
    private CancellationTokenSource? _resourceCts;
    private string? _subscribedResourceName;
    private ResourceKey? _telemetryKey;
    private Subscription? _logsSubscription;
    private Subscription? _tracesSubscription;
    private Subscription? _metricsSubscription;
    private int _telemetryRefreshPending;
    private int _consoleRenderPending;
    private bool _hasConsoleLogs;
    private bool _consoleLogsLoaded;
    private bool _telemetryLoaded;
    private OverviewTelemetry? _telemetry;
    private ResourcesLayout? _subscribedLayout;
    private IDisposable? _consoleLogsFiltersChangedSubscription;

    [Parameter]
    public string? ResourceName { get; set; }

    [CascadingParameter]
    public ResourcesLayout? ResourcesLayout { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ConsoleLogsManager ConsoleLogsManager { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required IVolumePathLauncher VolumePathLauncher { get; init; }

    [Inject]
    public required Microsoft.FluentUI.AspNetCore.Components.INotificationService ToastService { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Columns> ColumnsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Layout> LayoutLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ConsoleLogs> ConsoleLogsLoc { get; init; }

    [Inject]
    public required ILogger<ResourceOverview> Logger { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    private ResourceViewModel? Resource => ResourcesLayout?.SelectedResource;

    protected override async Task OnInitializedAsync()
    {
        // The console preview hides lines removed with the console logs page's "Remove" action, which are stored
        // as filters in session storage.
        await ConsoleLogsManager.EnsureInitializedAsync();
        _consoleLogsFiltersChangedSubscription = ConsoleLogsManager.OnFiltersChanged(() => InvokeAsync(() =>
        {
            if (_subscribedResourceName is { } resourceName)
            {
                StartConsoleSubscription(resourceName);
            }

            StateHasChanged();
        }));
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_subscribedLayout, ResourcesLayout))
        {
            _subscribedLayout?.ResourcesChanged -= OnResourcesChanged;
            _subscribedLayout = ResourcesLayout;
            _subscribedLayout?.ResourcesChanged += OnResourcesChanged;
        }

        EnsureSubscriptions();
        UpdateDetails();
    }

    private void OnResourcesChanged()
    {
        EnsureSubscriptions();
        UpdateDetails();

        // The telemetry resource can appear after the app model resource, for example when a project starts
        // sending telemetry. Refresh so the telemetry cards pick it up.
        QueueTelemetryRefresh();
        StateHasChanged();
    }

    /// <summary>
    /// Starts console log and telemetry subscriptions for the selected resource, replacing subscriptions for a
    /// previously selected resource. The page instance is reused when navigating between resources.
    /// </summary>
    private void EnsureSubscriptions()
    {
        var resource = Resource;
        var otlpResource = resource is not null ? TelemetryRepository.GetResourceByCompositeName(resource.Name) : null;

        if (string.Equals(_subscribedResourceName, resource?.Name, StringComparisons.ResourceName) &&
            Equals(_telemetryKey, otlpResource?.ResourceKey))
        {
            return;
        }

        var resourceChanged = !string.Equals(_subscribedResourceName, resource?.Name, StringComparisons.ResourceName);
        _subscribedResourceName = resource?.Name;

        if (resourceChanged)
        {
            StartConsoleSubscription(resource?.Name);
        }

        _logsSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        _logsSubscription = _tracesSubscription = _metricsSubscription = null;
        _telemetry = null;
        // Cleared whenever the telemetry key changes so the telemetry cards show their loading skeleton again
        // instead of flashing the previous resource's stale data or popping in abruptly once the new data arrives.
        _telemetryLoaded = false;
        _telemetryKey = otlpResource?.ResourceKey;

        if (_telemetryKey is { } key)
        {
            _logsSubscription = TelemetryRepository.OnNewLogs(key, SubscriptionType.Other, OnTelemetryChangedAsync);
            _tracesSubscription = TelemetryRepository.OnNewTraces(key, SubscriptionType.Other, OnTelemetryChangedAsync);
            _metricsSubscription = TelemetryRepository.OnNewMetrics(key, SubscriptionType.Other, OnTelemetryChangedAsync);
            QueueTelemetryRefresh();
        }
    }

    private Task OnTelemetryChangedAsync()
    {
        QueueTelemetryRefresh();
        return Task.CompletedTask;
    }

    private void QueueTelemetryRefresh()
    {
        if (_telemetryKey is null || Interlocked.Exchange(ref _telemetryRefreshPending, 1) == 1)
        {
            return;
        }

        var cancellationToken = _resourceCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_telemetryRefreshDelay, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);

                if (_telemetryKey is not { } key)
                {
                    return;
                }

                var telemetry = await LoadTelemetryAsync(key, cancellationToken).ConfigureAwait(false);
                await InvokeAsync(() =>
                {
                    // Ignore results for a resource that is no longer selected.
                    if (Equals(_telemetryKey, key))
                    {
                        _telemetry = telemetry;
                        _telemetryLoaded = true;
                        StateHasChanged();
                    }
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _telemetryRefreshPending, 0);
                Logger.LogWarning(ex, "Error loading telemetry summary for the resource overview.");
            }
        }, CancellationToken.None);
    }

    private async Task<OverviewTelemetry> LoadTelemetryAsync(ResourceKey key, CancellationToken cancellationToken)
    {
        var repository = TelemetryRepository;

        var allLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, [], 0, 0), cancellationToken).ConfigureAwait(false);
        List<TelemetryFilter> errorFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Error) }];
        List<TelemetryFilter> warningFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.Equals, Value = nameof(LogLevel.Warning) }];
        List<TelemetryFilter> attentionFilters = [new FieldTelemetryFilter { Field = nameof(OtlpLogEntry.Severity), Condition = FilterCondition.GreaterThanOrEqual, Value = nameof(LogLevel.Warning) }];

        var errorLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, errorFilters, 0, 0), cancellationToken).ConfigureAwait(false);
        var warningLogs = await repository.GetLogSummariesAsync(CreateLogsContext(key, warningFilters, 0, 0), cancellationToken).ConfigureAwait(false);

        // Results are ordered oldest first, so read the last page to get the most recent entries.
        var attentionCount = errorLogs.TotalItemCount + warningLogs.TotalItemCount;
        var recentAttentionLogs = attentionCount > 0
            ? (await repository.GetLogSummariesAsync(CreateLogsContext(key, attentionFilters, Math.Max(0, attentionCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).Items
            : [];

        // A healthy resource has no warnings or errors to highlight, so show its latest logs instead of an empty card.
        var recentLogs = attentionCount == 0 && allLogs.TotalItemCount > 0
            ? (await repository.GetLogSummariesAsync(CreateLogsContext(key, [], Math.Max(0, allLogs.TotalItemCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).Items
            : [];

        var allTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest(key, [], 0, 0), cancellationToken).ConfigureAwait(false);
        List<TelemetryFilter> failedTraceFilters = [new FieldTelemetryFilter { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) }];
        var failedTraces = await repository.GetTraceSummariesAsync(CreateTracesRequest(key, failedTraceFilters, 0, 0), cancellationToken).ConfigureAwait(false);
        var traceCount = allTraces.PagedResult.TotalItemCount;
        var recentTraces = traceCount > 0
            ? (await repository.GetTraceSummariesAsync(CreateTracesRequest(key, [], Math.Max(0, traceCount - RecentItemCount), RecentItemCount), cancellationToken).ConfigureAwait(false)).PagedResult.Items
            : [];

        var instruments = repository.GetInstrumentSummaries(key)
            .OrderBy(i => i.Parent.Name, StringComparer.Ordinal)
            .ThenBy(i => i.Name, StringComparer.Ordinal)
            .ToList();

        return new OverviewTelemetry
        {
            LogCount = allLogs.TotalItemCount,
            ErrorLogCount = errorLogs.TotalItemCount,
            WarningLogCount = warningLogs.TotalItemCount,
            RecentAttentionLogs = recentAttentionLogs.AsEnumerable().Reverse().ToList(),
            RecentLogs = recentLogs.AsEnumerable().Reverse().ToList(),
            TraceCount = traceCount,
            FailedTraceCount = failedTraces.PagedResult.TotalItemCount,
            RecentTraces = recentTraces.AsEnumerable().Reverse().ToList(),
            Instruments = instruments
        };

        static GetLogsContext CreateLogsContext(ResourceKey key, List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [key],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };

        static GetTracesRequest CreateTracesRequest(ResourceKey key, List<TelemetryFilter> filters, int startIndex, int count) => new()
        {
            ResourceKeys = [key],
            StartIndex = startIndex,
            Count = count,
            Filters = filters
        };
    }

    /// <summary>
    /// Replaces the console log subscription and clears the preview. Called when the selected resource changes, and
    /// when console log filters change so removed lines disappear from the preview.
    /// </summary>
    private void StartConsoleSubscription(string? resourceName)
    {
        _resourceCts?.Cancel();
        _resourceCts?.Dispose();
        _resourceCts = null;

        lock (_consoleLock)
        {
            _consoleLines.Clear();
            _hasConsoleLogs = false;
            _consoleLogsLoaded = false;
        }

        if (resourceName is not null)
        {
            _resourceCts = new CancellationTokenSource();
            _ = SubscribeConsoleLogsAsync(resourceName, _resourceCts.Token);
        }
    }

    private async Task SubscribeConsoleLogsAsync(string resourceName, CancellationToken cancellationToken)
    {
        // The subscription doesn't yield anything for a resource without console logs. Stop displaying the
        // loading indicator after a short delay so the card shows that there are no logs.
        _ = MarkConsoleLogsLoadedAfterDelayAsync(cancellationToken);

        try
        {
            // Subscribe through the dashboard client, like the console logs page. With a live app host, it streams
            // the resource's logs from the app host and stores them. The resource repository only has the lines
            // that a dashboard client subscription already stored.
            await foreach (var batch in DashboardClient.SubscribeConsoleLogs(resourceName, cancellationToken).ConfigureAwait(false))
            {
                var filterDate = ConsoleLogsManager.GetFilterDate(resourceName);
                lock (_consoleLock)
                {
                    foreach (var line in batch)
                    {
                        var previewLine = CreatePreviewLine(line);
                        if (IsHidden(previewLine, filterDate))
                        {
                            continue;
                        }

                        InsertConsoleLine(previewLine);
                    }

                    _hasConsoleLogs |= batch.Count > 0;
                    _consoleLogsLoaded = true;
                }

                QueueConsoleRender(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error reading console logs for the resource overview.");
        }

        lock (_consoleLock)
        {
            _consoleLogsLoaded = true;
        }
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets whether a line is hidden because it was removed from the console logs page, or because it was written
    /// while console logs were paused. Matches the lines the console logs page displays.
    /// </summary>
    private bool IsHidden(ConsolePreviewLine line, DateTime? filterDate)
    {
        if (line.Timestamp is not { } timestamp)
        {
            // Without a timestamp, a line can't be matched to a pause interval. It's a new line if it's received
            // while paused.
            return PauseManager.ConsoleLogsPaused;
        }

        var utcTimestamp = timestamp.UtcDateTime;
        if (filterDate is not null && utcTimestamp <= filterDate)
        {
            return true;
        }

        foreach (var pause in PauseManager.ConsoleLogPauseIntervals)
        {
            if (utcTimestamp >= pause.Start && (pause.End is null || utcTimestamp <= pause.End))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Pauses or resumes console logs. The pause is shared with the console logs page, which displays the pause and
    /// the number of lines that were skipped.
    /// </summary>
    private void OnConsolePausedChanged(bool isPaused)
    {
        PauseManager.SetConsoleLogsPaused(isPaused, TimeProvider.GetUtcNow().UtcDateTime);
    }

    private string? ConsolePauseText => PauseManager.ConsoleLogPauseIntervals.LastOrDefault() is { End: null } pause
        ? string.Format(
            CultureInfo.CurrentCulture,
            ConsoleLogsLoc[nameof(Dashboard.Resources.ConsoleLogs.PauseInProgressText)],
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, pause.Start))
        : null;

    private async Task MarkConsoleLogsLoadedAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(s_consoleLoadingTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_consoleLock)
        {
            if (_consoleLogsLoaded)
            {
                return;
            }

            _consoleLogsLoaded = true;
        }

        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    /// <summary>
    /// Resources can write a large backlog when the subscription starts. Renders are throttled while it's replayed so
    /// the preview doesn't re-render for every batch. The render runs after the delay so the last batch is displayed.
    /// </summary>
    private void QueueConsoleRender(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _consoleRenderPending, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(s_consoleRenderDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                Interlocked.Exchange(ref _consoleRenderPending, 0);
            }

            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    /// <summary>
    /// Converts a raw console line into plain text for the preview. Lines usually start with an RFC 3339 timestamp
    /// and can contain ANSI color sequences, for example:
    /// <c>2025-01-01T10:00:00.1234567Z \u001b[32minfo\u001b[0m: Now listening on: http://localhost:5000</c>.
    /// </summary>
    private static ConsolePreviewLine CreatePreviewLine(ResourceLogLine line)
    {
        var content = line.Content;
        DateTimeOffset? timestamp = null;
        if (TimestampParser.TryParseConsoleTimestamp(content, out var result))
        {
            content = result.Value.ModifiedText;
            timestamp = result.Value.Timestamp;
        }

        return new ConsolePreviewLine(line.LineNumber, AnsiParser.StripControlSequences(content), timestamp, line.IsErrorMessage);
    }

    /// <summary>
    /// Inserts a line in timestamp order and keeps the most recent lines. Lines from different sources, such as the
    /// app host's <c>[sys]</c> lines and the process output, can arrive out of order. The console logs page sorts them
    /// by timestamp too.
    /// </summary>
    private void InsertConsoleLine(ConsolePreviewLine line)
    {
        var index = _consoleLines.Count;
        if (line.Timestamp is { } timestamp)
        {
            while (index > 0 && _consoleLines[index - 1].Timestamp is { } previous && previous > timestamp)
            {
                index--;
            }
        }

        _consoleLines.Insert(index, line);
        if (_consoleLines.Count > ConsolePreviewLineCount)
        {
            _consoleLines.RemoveAt(0);
        }
    }

    private List<ConsolePreviewLine> GetConsoleLines()
    {
        lock (_consoleLock)
        {
            return _consoleLines.ToList();
        }
    }

    private sealed record EndpointGroup(DisplayedUrl Primary, DisplayedUrl? Secondary);

    /// <summary>
    /// Resources commonly expose the same logical endpoint twice: once reachable from outside the container
    /// (the public address) and once reachable only from the host/other containers (the internal address). Both
    /// urls share the same display name, so showing them as two separate rows just duplicates the row's label and
    /// scheme badge. Collapse them into a single row, preferring the public url as the clickable link and keeping
    /// the internal url as supplementary text.
    /// </summary>
    private static List<EndpointGroup> GroupEndpoints(IReadOnlyList<DisplayedUrl> urls)
    {
        var groups = new List<EndpointGroup>();
        var consumedIndexes = new HashSet<int>();

        for (var i = 0; i < urls.Count; i++)
        {
            if (!consumedIndexes.Add(i))
            {
                continue;
            }

            var primary = urls[i];
            DisplayedUrl? secondary = null;

            for (var j = i + 1; j < urls.Count; j++)
            {
                if (consumedIndexes.Contains(j))
                {
                    continue;
                }

                var candidate = urls[j];
                if (string.Equals(candidate.Name, primary.Name, StringComparison.Ordinal) &&
                    string.Equals(candidate.DisplayName ?? candidate.Text, primary.DisplayName ?? primary.Text, StringComparison.Ordinal))
                {
                    secondary = candidate;
                    consumedIndexes.Add(j);
                    break;
                }
            }

            if (secondary is not null && primary.IsInternal && !secondary.IsInternal)
            {
                (primary, secondary) = (secondary, primary);
            }

            groups.Add(new EndpointGroup(primary, secondary));
        }

        return groups;
    }

    private string GetResourceName(ResourceViewModel resource) => ResourcesLayout?.GetResourceName(resource) ?? resource.Name;

    private IEnumerable<RelatedResource> GetRelatedResources(ResourceViewModel resource)
    {
        if (ResourcesLayout is null)
        {
            return [];
        }

        var resources = ResourcesLayout.ResourceByName.Values.Where(r => !r.IsResourceHidden(showHiddenResources: false)).ToList();
        var related = new Dictionary<string, RelatedResource>(StringComparers.ResourceName);

        // Relationships reference other resources by display name, so every replica of the target is related.
        foreach (var relationship in resource.Relationships)
        {
            foreach (var match in resources.Where(r => string.Equals(r.DisplayName, relationship.ResourceName, StringComparisons.ResourceName) && r != resource))
            {
                related.TryAdd(match.Name, new RelatedResource(match, relationship.Type, IsIncoming: false));
            }
        }

        foreach (var other in resources.Where(r => r != resource))
        {
            if (other.Relationships.FirstOrDefault(r => string.Equals(r.ResourceName, resource.DisplayName, StringComparisons.ResourceName)) is { } relationship)
            {
                related.TryAdd(other.Name, new RelatedResource(other, relationship.Type, IsIncoming: true));
            }
        }

        return related.Values
            .OrderBy(r => r.IsIncoming)
            .ThenBy(r => GetResourceName(r.Resource), StringComparers.ResourceName);
    }

    private List<RelationshipGraphNode> GetGraphNodes(IEnumerable<RelatedResource> relatedResources, bool incoming) =>
        relatedResources
            .Where(r => r.IsIncoming == incoming)
            .Select(r =>
            {
                var name = GetResourceName(r.Resource);
                return new RelationshipGraphNode(name, r.Type, DashboardUrls.ResourceOverviewUrl(name));
            })
            .ToList();

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
        => DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);

    public ValueTask DisposeAsync()
    {
        _subscribedLayout?.ResourcesChanged -= OnResourcesChanged;

        _resourceCts?.Cancel();
        _resourceCts?.Dispose();
        _logsSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _metricsSubscription?.Dispose();
        _consoleLogsFiltersChangedSubscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ConsolePreviewLine(int LineNumber, string Content, DateTimeOffset? Timestamp, bool IsError);

    private sealed record SelectionGroup(string Title, List<string> Names);

    // Common app model types come first in a fixed order; any other types follow alphabetically.
    private static readonly string[] s_selectionGroupOrder = [KnownResourceTypes.Project, KnownResourceTypes.Container, KnownResourceTypes.Executable];

    private List<SelectionGroup> GetSelectionGroups(IEnumerable<string> names)
    {
        var telemetryOnlyTitle = LayoutLoc[nameof(Dashboard.Resources.Layout.ResourcePaneTelemetryOnly)].Value;
        return names
            .GroupBy(name => ResourcesLayout is not null && ResourceViewModel.TryGetResourceByName(name, ResourcesLayout.ResourceByName, out var resource)
                ? resource.ResourceType
                : telemetryOnlyTitle, StringComparer.Ordinal)
            .OrderBy(g => g.Key == telemetryOnlyTitle ? 1 : 0)
            .ThenBy(g => Array.IndexOf(s_selectionGroupOrder, g.Key) is var index and >= 0 ? index : s_selectionGroupOrder.Length)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SelectionGroup(g.Key, g.ToList()))
            .ToList();
    }

    private sealed record RelatedResource(ResourceViewModel Resource, string Type, bool IsIncoming);

    private sealed class OverviewTelemetry
    {
        public required int LogCount { get; init; }
        public required int ErrorLogCount { get; init; }
        public required int WarningLogCount { get; init; }
        public required List<LogSummary> RecentAttentionLogs { get; init; }
        public required List<LogSummary> RecentLogs { get; init; }
        public required int TraceCount { get; init; }
        public required int FailedTraceCount { get; init; }
        public required List<TraceSummary> RecentTraces { get; init; }
        public required List<OtlpInstrumentSummary> Instruments { get; init; }
    }

    private enum OverviewCard
    {
        Status,
        Endpoints,
        Relationships,
        Console,
        Logs,
        Traces,
        Metrics,
        Properties,
        Environment,
        Volumes,
        Health
    }

    /// <summary>
    /// Packs cards into rows of a 12 column grid in page order. Each card requests a span based on its content, and the
    /// spare columns at the end of a row are shared between that row's cards so rows never have trailing gaps.
    /// Medium widths use a separate two-per-row packing where wide cards take the full row.
    /// </summary>
    private sealed class OverviewCardLayout
    {
        private const int Columns = 12;

        private readonly Dictionary<OverviewCard, int> _spans;
        private readonly Dictionary<OverviewCard, int> _mediumSpans;

        private OverviewCardLayout(Dictionary<OverviewCard, int> spans, Dictionary<OverviewCard, int> mediumSpans)
        {
            _spans = spans;
            _mediumSpans = mediumSpans;
        }

        public static OverviewCardLayout Create(IReadOnlyList<(OverviewCard Card, int Span)> cards)
        {
            return new OverviewCardLayout(
                Pack(cards),
                Pack(cards.Select(c => (c.Card, c.Span >= 7 ? Columns : Columns / 2)).ToList()));
        }

        public string Style(OverviewCard card)
        {
            return string.Create(CultureInfo.InvariantCulture, $"--overview-span: {_spans.GetValueOrDefault(card, Columns)}; --overview-span-md: {_mediumSpans.GetValueOrDefault(card, Columns)};");
        }

        private static Dictionary<OverviewCard, int> Pack(IReadOnlyList<(OverviewCard Card, int Span)> cards)
        {
            var result = new Dictionary<OverviewCard, int>();
            var row = new List<(OverviewCard Card, int Span)>();
            var used = 0;

            foreach (var (card, requested) in cards)
            {
                var span = Math.Clamp(requested, 1, Columns);
                if (used + span > Columns)
                {
                    CompleteRow();
                }
                row.Add((card, span));
                used += span;
            }
            CompleteRow();

            return result;

            void CompleteRow()
            {
                var spare = Columns - used;
                for (var i = 0; i < row.Count; i++)
                {
                    result[row[i].Card] = row[i].Span + (spare / row.Count) + (i < spare % row.Count ? 1 : 0);
                }
                row.Clear();
                used = 0;
            }
        }
    }
}
