// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Summarizes an app model resource: its status, endpoints and relationships, a preview of recent console output and
/// telemetry, and the complete resource details. Each summary links to the tab that shows the full data.
/// </summary>
/// <remarks>
/// The instance is reused when navigating between resources, so the console and telemetry subscriptions follow
/// <see cref="Resource"/>.
/// </remarks>
public sealed partial class ResourceOverview : ComponentBase, IAsyncDisposable
{
    private const int ConsolePreviewLineCount = 10;

    private static readonly TimeSpan s_consoleRenderDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan s_consoleLoadingTimeout = TimeSpan.FromSeconds(1);

    private readonly object _consoleLock = new();
    private readonly List<ConsolePreviewLine> _consoleLines = new();
    private CancellationTokenSource? _consoleCts;
    private string? _subscribedResourceName;
    private ResourceViewModel? _telemetryResource;
    private OverviewTelemetryLoader _telemetryLoader = default!;

    // Tests replace this to hold a refresh at the point where it can be canceled, because how soon a real delay handles
    // its cancellation depends on thread pool timing.
    internal Func<TimeSpan, CancellationToken, Task> TelemetryRefreshDelayAsync { get; set; } = Task.Delay;
    internal Task TelemetryRefreshTask => _telemetryLoader.RefreshTask;
    private int _consoleRenderPending;
    private bool _hasConsoleLogs;
    private bool _consoleLogsLoaded;
    private IDisposable? _consoleLogsFiltersChangedSubscription;
    private int? _terminalFontSize;
    private readonly EventCallback<TerminalToolbarState> _terminalToolbarStateChanged;

    public ResourceOverview()
    {
        // The terminal reports toolbar state on every resize and connection change. A null receiver stops Blazor
        // from re-rendering the whole overview for each report; only a font change affects the window button.
        _terminalToolbarStateChanged = new EventCallback<TerminalToolbarState>(null, (Func<TerminalToolbarState, Task>)OnTerminalToolbarStateChangedAsync);
    }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    /// <summary>
    /// Gets or sets whether cards without content are hidden.
    /// </summary>
    [Parameter]
    public bool HideEmptyPanels { get; set; }

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
    public required IVolumePathLauncher VolumePathLauncher { get; init; }

    [Inject]
    public required TerminalDockState TerminalDockState { get; init; }

    [Inject]
    public required Microsoft.FluentUI.AspNetCore.Components.INotificationService ToastService { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ConsoleLogs> ConsoleLogsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.TerminalStrings> TerminalLoc { get; init; }

    [Inject]
    public required ILogger<ResourceOverview> Logger { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    private ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    // A recording's terminal endpoint would resolve through the live dashboard client, and recorded instance names can
    // still match a running AppHost after a dashboard-only restart. Showing it would open a writable connection to a
    // live process from a read-only run, so recordings show the console card instead, like the Terminals page.
    private bool CanShowTerminal(ResourceViewModel resource) =>
        !DashboardClient.IsReadOnly && ResourceSelectHelpers.HasUsableTerminal(resource);

    private string GetTerminalPageUrl(string resourceName) =>
        ResourcesLayout!.WithCurrentResourceViewState(DashboardUrls.TerminalsUrl(resourceName));

    private Task OnTerminalToolbarStateChangedAsync(TerminalToolbarState state)
    {
        int? fontSize = state.FontPx > 0 ? state.FontPx : null;
        if (fontSize == _terminalFontSize)
        {
            return Task.CompletedTask;
        }

        _terminalFontSize = fontSize;
        return InvokeAsync(StateHasChanged);
    }

    protected override async Task OnInitializedAsync()
    {
        _telemetryLoader = new OverviewTelemetryLoader(
            TelemetryRepository,
            Logger,
            InvokeAsync,
            StateHasChanged,
            (delay, cancellationToken) => TelemetryRefreshDelayAsync(delay, cancellationToken));

        TerminalDockState.Changed += OnTerminalDockStateChanged;

        // The console preview hides lines removed with the console logs page's "Remove" action, which are stored
        // as filters in session storage.
        await ConsoleLogsManager.EnsureInitializedAsync();
        _consoleLogsFiltersChangedSubscription = ConsoleLogsManager.OnFiltersChanged(() => InvokeAsync(() =>
        {
            StartConsoleSubscription(Resource.Name);
            StateHasChanged();
        }));
    }

    protected override void OnParametersSet()
    {
        if (!string.Equals(_subscribedResourceName, Resource.Name, StringComparisons.ResourceName))
        {
            StartConsoleSubscription(Resource.Name);
        }

        // A resource's telemetry can start or stop arriving without its name changing, so the key is looked up again
        // whenever the resource is updated.
        var telemetryKey = TelemetryRepository.GetResourceByCompositeName(Resource.Name)?.ResourceKey;
        if (!_telemetryLoader.SetKey(telemetryKey) && !ReferenceEquals(_telemetryResource, Resource))
        {
            _telemetryLoader.QueueRefresh();
        }
        _telemetryResource = Resource;

        UpdateDetails();
    }

    /// <summary>
    /// Replaces the console log subscription and clears the preview. Called when the resource changes, and when
    /// console log filters change so removed lines disappear from the preview.
    /// </summary>
    private void StartConsoleSubscription(string resourceName)
    {
        _subscribedResourceName = resourceName;
        _consoleCts?.Cancel();
        _consoleCts?.Dispose();
        _consoleCts = new CancellationTokenSource();

        lock (_consoleLock)
        {
            _consoleLines.Clear();
            _hasConsoleLogs = false;
            _consoleLogsLoaded = false;
        }

        _ = SubscribeConsoleLogsAsync(resourceName, _consoleCts.Token);
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
                    // Cancellation can happen after a batch is delivered but before it
                    // acquires this lock. Never append it to the next resource's preview.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
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
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
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
            if (cancellationToken.IsCancellationRequested || _consoleLogsLoaded)
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
            .Select(r => GetGraphNode(r.Resource, r.Type, isCenter: false))
            .ToList();

    private RelationshipGraphNode GetGraphNode(ResourceViewModel resource, string type, bool isCenter)
    {
        var name = GetResourceName(resource);
        return new RelationshipGraphNode(name, type, isCenter ? null : DashboardUrls.ResourceOverviewUrl(name),
            IsReplica: ResourcesLayout?.IsReplica(resource) == true);
    }

    private Task ExecuteResourceCommandAsync(ResourceViewModel resource, CommandViewModel command)
        => DashboardCommandExecutor.ExecuteAsync(resource, command, GetResourceName);

    public ValueTask DisposeAsync()
    {
        TerminalDockState.Changed -= OnTerminalDockStateChanged;

        _telemetryLoader?.Dispose();
        _consoleCts?.Cancel();
        _consoleCts?.Dispose();
        _consoleLogsFiltersChangedSubscription?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void OnTerminalDockStateChanged()
        => _ = InvokeAsync(StateHasChanged);

    private sealed record ConsolePreviewLine(int LineNumber, string Content, DateTimeOffset? Timestamp, bool IsError);

    private sealed record RelatedResource(ResourceViewModel Resource, string Type, bool IsIncoming);
}
