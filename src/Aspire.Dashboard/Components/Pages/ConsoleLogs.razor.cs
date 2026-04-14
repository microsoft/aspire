// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Shared.ConsoleLogs;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Pages;

public sealed partial class ConsoleLogs : ComponentBase, IComponentWithTelemetry, IAsyncDisposable, IPageWithSessionAndUrlState<ConsoleLogs.ConsoleLogsViewModel, ConsoleLogs.ConsoleLogsPageState>
{
    [DebuggerDisplay("Resource = {Resource.Name}, IsCancellationRequested = {CancellationToken.IsCancellationRequested}")]
    private sealed class ConsoleLogsSubscription
    {
        private static int s_subscriptionId;

        private readonly CancellationTokenSource _cts = new();
        private readonly int _subscriptionId = Interlocked.Increment(ref s_subscriptionId);
        private readonly ILogger _logger;

        public ResourceViewModel Resource { get; }
        public Task? SubscriptionTask { get; set; }
        private long _cancelTimestamp;

        public CancellationToken CancellationToken => _cts.Token;
        public int SubscriptionId => _subscriptionId;

        public ConsoleLogsSubscription(ResourceViewModel resource, ILogger logger)
        {
            Resource = resource;
            _logger = logger;
            _cts = new();

            _cts.Token.Register(static state =>
            {
                // The canceled TCS lets us know that the subscription has been canceled without waiting for all other cancellation logic to finish running.
                var s = (ConsoleLogsSubscription)state!;
                s._logger.LogDebug("Canceling subscription {SubscriptionId} to {ResourceName}.", s.SubscriptionId, s.Resource.Name);
            }, this);
        }

        public void Cancel()
        {
            _cancelTimestamp = Stopwatch.GetTimestamp();
            _cts.Cancel();
            _logger.LogDebug("Canceling subscription for resource {ResourceName}.", Resource.Name);
        }

        public TimeSpan GetCancelElapsedTime() => Stopwatch.GetElapsedTime(_cancelTimestamp);
    }

    [Inject]
    public required IOptions<DashboardOptions> Options { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ILocalStorage LocalStorage { get; init; }

    [Inject]
    public required ISessionStorage SessionStorage { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required ILogger<ConsoleLogs> Logger { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.ConsoleLogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> ResourcesLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.AIAssistant> AIAssistantLoc { get; init; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.AIPrompts> AIPromptsLoc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required DashboardCommandExecutor DashboardCommandExecutor { get; init; }

    [Inject]
    public required ConsoleLogsManager ConsoleLogsManager { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required PauseManager PauseManager { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required IconResolver IconResolver { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    [Inject]
    public required ResourceMenuBuilder ResourceMenuBuilder { get; init; }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; init; }

    [Parameter]
    public string? ResourceName { get; set; }

    private record struct LogEntryToWrite(string ResourceName, LogEntry LogEntry, int? LineNumber);

    private readonly CancellationTokenSource _resourceSubscriptionCts = new();
    private readonly ConcurrentDictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private readonly Channel<LogEntryToWrite> _logEntryChannel = Channel.CreateUnbounded<LogEntryToWrite>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private ImmutableList<SelectViewModel<ResourceTypeDetails>>? _resources;
    private CancellationToken _resourceSubscriptionToken;
    private Task? _resourceSubscriptionTask;
    private Task? _logEntryChannelReaderTask;
    private readonly ConcurrentDictionary<string, ConsoleLogsSubscription> _consoleLogsSubscriptions = new(StringComparers.ResourceName);
    private bool _isSubscribedToAll;
    internal LogEntries _logEntries = null!;
    private readonly object _updateLogsLock = new object();
    private AIContext? _aiContext;
    private LogViewer? _logViewerRef;

    // UI
    private SelectViewModel<ResourceTypeDetails> _allResource = null!;
    private AspirePageContentLayout? _contentLayout;
    private readonly List<CommandViewModel> _highlightedCommands = new();
    private readonly List<MenuButtonItem> _logsMenuItems = new();
    private readonly List<MenuButtonItem> _resourceMenuItems = new();

    // State
    private bool _showHiddenResources;
    private bool _showTimestamp;
    private bool _isTimestampUtc;
    private bool _noWrapLogs;
    public ConsoleLogsViewModel PageViewModel { get; set; } = null!;
    private IDisposable? _consoleLogsFiltersChangedSubscription;

    public string BasePath => DashboardUrls.ConsoleLogBasePath;
    public string SessionStorageKey => BrowserStorageKeys.ConsoleLogsPageState;

    protected override async Task OnInitializedAsync()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _resourceSubscriptionToken = _resourceSubscriptionCts.Token;
        _logEntries = new(Options.Value.Frontend.MaxConsoleLogCount);
        _allResource = new() { Id = null, Name = ControlsStringsLoc[nameof(ControlsStrings.LabelAll)] };
        PageViewModel = new ConsoleLogsViewModel { SelectedResource = _allResource, Status = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLoadingResources)] };
        _aiContext = CreateAIContext();
        _logEntryChannelReaderTask = StartLogEntryChannelReaderTask();

        _consoleLogsFiltersChangedSubscription = ConsoleLogsManager.OnFiltersChanged(async () =>
        {
            var isAllSelected = IsAllSelected();
            var selectedResourceName = PageViewModel.SelectedResource.Id?.InstanceId;

            await SubscribeAsync(isAllSelected, selectedResourceName);
        });

        var consoleSettingsResult = await LocalStorage.GetUnprotectedAsync<ConsoleLogConsoleSettings>(BrowserStorageKeys.ConsoleLogConsoleSettings);
        if (consoleSettingsResult.Value is { } consoleSettings)
        {
            _showTimestamp = consoleSettings.ShowTimestamp;
            _isTimestampUtc = consoleSettings.IsTimestampUtc;
            _noWrapLogs = consoleSettings.NoWrapLogs;
        }

        var showHiddenResources = await SessionStorage.GetAsync<bool>(BrowserStorageKeys.ResourcesShowHiddenResources);
        if (showHiddenResources.Success)
        {
            _showHiddenResources = showHiddenResources.Value;
        }

        await ConsoleLogsManager.EnsureInitializedAsync();

        var loadingTcs = new TaskCompletionSource();

        await TrackResourceSnapshotsAsync();

        // Wait for resource to be selected. If selected resource isn't available after a few seconds then stop waiting.
        try
        {
            await loadingTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), _resourceSubscriptionToken);
            Logger.LogDebug("Loading task completed.");
        }
        catch (OperationCanceledException)
        {
            Logger.LogDebug("Load task canceled.");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Load timeout while waiting for resource {ResourceName}.", ResourceName);
            SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLogsNotYetAvailable));
        }

        async Task TrackResourceSnapshotsAsync()
        {
            if (!DashboardClient.IsEnabled)
            {
                return;
            }

            var (snapshot, subscription) = await DashboardClient.SubscribeResourcesAsync(_resourceSubscriptionToken);

            Logger.LogDebug("Received initial resource snapshot with {ResourceCount} resources.", snapshot.Length);

            foreach (var resource in snapshot)
            {
                var added = _resourceByName.TryAdd(resource.Name, resource);
                Debug.Assert(added, "Should not receive duplicate resources in initial snapshot data.");
            }

            UpdateResourcesList();

            // Set loading task result if the selected resource is already in the snapshot or there is no selected resource.
            if (ResourceName != null)
            {
                if (ResourceViewModel.TryGetResourceByName(ResourceName, _resourceByName, out var selectedResource))
                {
                    SetSelectedResourceOption(selectedResource);
                }
            }
            else
            {
                Logger.LogDebug("All resources selected.");
                loadingTcs.TrySetResult();
            }

            _resourceSubscriptionTask = Task.Run(async () =>
            {
                await foreach (var changes in subscription.WithCancellation(_resourceSubscriptionToken).ConfigureAwait(false))
                {
                    foreach (var (changeType, resource) in changes)
                    {
                        await OnResourceChanged(changeType, resource);

                        // the initial snapshot we obtain is [almost] never correct (it's always empty)
                        // we still want to select the user's initial queried resource on page load,
                        // so if there is no selected resource when we
                        // receive an added resource, and that added resource name == ResourceName,
                        // we should mark it as selected
                        if (ResourceName is not null && PageViewModel.SelectedResource is null && changeType == ResourceViewModelChangeType.Upsert && string.Equals(ResourceName, resource.Name, StringComparisons.ResourceName))
                        {
                            SetSelectedResourceOption(resource);
                        }
                    }

                    await InvokeAsync(() =>
                    {
                        // The selected resource may have changed, so update resource action buttons.
                        // Update inside in the render's sync context so the buttons don't change while the UI is rendering.
                        UpdateMenuButtons();

                        StateHasChanged();
                    });
                }
            });
        }

        void SetSelectedResourceOption(ResourceViewModel resource)
        {
            PageViewModel.SelectedResource = GetSelectedOption();

            Logger.LogDebug("Selected console resource from name {ResourceName}.", ResourceName);
            loadingTcs.TrySetResult();
        }
    }

    private async Task StartLogEntryChannelReaderTask()
    {
        await foreach (var batch in _logEntryChannel.GetBatchesAsync(minReadInterval: TimeSpan.FromMilliseconds(100), cancellationToken: _resourceSubscriptionToken))
        {
            lock (_updateLogsLock)
            {
                foreach (var (resourceName, logEntry, lineNumber) in batch)
                {
                    // Console logs are filtered in the UI by the timestamp of the log entry.
                    var timestampFilterDate = ConsoleLogsManager.GetFilterDate(resourceName);

                    if (lineNumber != null)
                    {
                        // Set the base line number using the reported line number of the first log line.
                        _logEntries.BaseLineNumber ??= lineNumber;
                    }

                    // Check if log entry is not displayed because of remove.
                    if (logEntry.Timestamp is not null && timestampFilterDate is not null && !(logEntry.Timestamp > timestampFilterDate))
                    {
                        continue;
                    }

                    // Check if log entry is not displayed because of pause.
                    if (_logEntries.ProcessPauseFilters(logEntry))
                    {
                        continue;
                    }

                    _logEntries.InsertSorted(logEntry);
                }
            }

            await InvokeAsync(_logViewerRef.SafeRefreshDataAsync);
        }
    }

    private SelectViewModel<ResourceTypeDetails> GetSelectedOption()
    {
        Debug.Assert(_resources is not null);
        return _resources.GetResource(Logger, ResourceName, canSelectGrouping: true, fallbackViewModel: _allResource);
    }

    private void SetStatus(ConsoleLogsViewModel viewModel, string statusName)
    {
        Logger.LogDebug("Setting status to '{StatusName}'.", statusName);
        viewModel.Status = Loc[statusName];
    }

    protected override async Task OnParametersSetAsync()
    {
        Logger.LogDebug("Initializing console logs view model.");
        if (await this.InitializeViewModelAsync())
        {
            return;
        }

        UpdateMenuButtons();

        // Determine if we're subscribing to "All" resources or a specific resource
        var isAllSelected = IsAllSelected();
        var selectedResourceName = PageViewModel.SelectedResource.Id?.InstanceId;

        // Check if subscription needs to change
        var needsNewSubscription = false;

        if (isAllSelected != _isSubscribedToAll)
        {
            Logger.LogDebug("Switching to or from 'All' mode");
            needsNewSubscription = true;
        }
        else if (!string.IsNullOrEmpty(selectedResourceName) && !_consoleLogsSubscriptions.ContainsKey(selectedResourceName))
        {
            Logger.LogDebug("Switching to different single resource: {ResourceName}", selectedResourceName);
            needsNewSubscription = true;
        }

        if (needsNewSubscription)
        {
            await SubscribeAsync(isAllSelected, selectedResourceName);
        }

        UpdateTelemetryProperties();
    }

    private async Task SubscribeAsync(bool isAllSelected, string? selectedResourceName)
    {
        Logger.LogDebug("Subscription change needed. IsAllSelected: {IsAllSelected}, SelectedResource: {SelectedResource}", isAllSelected, selectedResourceName);
        _aiContext?.ContextHasChanged();

        // Cancel all existing subscriptions
        await CancelAllSubscriptionsAsync();

        // Clear log entries for new subscription
        Logger.LogDebug("Creating new log entries collection.");
        lock (_updateLogsLock)
        {
            _logEntries.Clear(keepActivePauseEntries: false);
        }

        await InvokeAsync(_logViewerRef.SafeRefreshDataAsync);

        if (isAllSelected)
        {
            // Subscribe to all available resources
            _isSubscribedToAll = true;
            await SubscribeToAllResourcesAsync();
        }
        else if (selectedResourceName is not null && _resourceByName.TryGetValue(selectedResourceName, out var resource))
        {
            // Subscribe to single resource
            _isSubscribedToAll = false;
            await SubscribeToSingleResourceAsync(resource);
        }
        else
        {
            Logger.LogDebug("Unexpected state. Unknown resource '{ResourceName}' selected.", selectedResourceName);
        }
    }

    private bool IsAllSelected()
    {
        return PageViewModel?.SelectedResource is not null && PageViewModel.SelectedResource == _allResource;
    }

    private void UpdateMenuButtons()
    {
        _highlightedCommands.Clear();
        _logsMenuItems.Clear();
        _resourceMenuItems.Clear();

        var selectedResource = GetSelectedResource();

        // Only show the "Hide hidden resources" menu item when viewing all resources
        // Use IsAllSelected() instead of _isSubscribedToAll because UpdateMenuButtons()
        // can be called before the subscription is established
        if (IsAllSelected())
        {
            CommonMenuItems.AddToggleHiddenResourcesMenuItem(
                _logsMenuItems,
                ControlsStringsLoc,
                _showHiddenResources,
                _resourceByName.Values,
                SessionStorage,
                EventCallback.Factory.Create<bool>(this, async
                value =>
                {
                    _showHiddenResources = value;
                    UpdateResourcesList();
                    UpdateMenuButtons();

                    await this.RefreshIfMobileAsync(_contentLayout);
                }));
        }

        _logsMenuItems.Add(new()
        {
            OnClick = () => ToggleTimestampAsync(showTimestamp: !_showTimestamp, isTimestampUtc: _isTimestampUtc),
            Text = _showTimestamp ? Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsTimestampHide)] : Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsTimestampShow)],
            Icon = new Icons.Regular.Size16.CalendarClock()
        });

        _logsMenuItems.Add(new()
        {
            OnClick = () => ToggleTimestampAsync(showTimestamp: _showTimestamp, isTimestampUtc: !_isTimestampUtc),
            Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsTimestampShowUtc)],
            Icon = _isTimestampUtc ? new Icons.Regular.Size16.CheckboxChecked() : new Icons.Regular.Size16.CheckboxUnchecked(),
            IsDisabled = !_showTimestamp
        });

        _logsMenuItems.Add(new()
        {
            OnClick = () => ToggleWrapLogsAsync(noWrapLogs: !_noWrapLogs),
            Text = _noWrapLogs ? Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsWrapLogs)] : Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsNoWrapLogs)],
            Icon = _noWrapLogs ? new Icons.Regular.Size16.TextWrap() : new Icons.Regular.Size16.TextWrapOff()
        });

        if (selectedResource != null)
        {
            if (ViewportInformation.IsDesktop)
            {
                _highlightedCommands.AddRange(selectedResource.Commands.Where(c => c.IsHighlighted && c.State != CommandViewModelState.Hidden).Take(DashboardUIHelpers.MaxHighlightedCommands));
            }

            ResourceMenuBuilder.AddMenuItems(
                _resourceMenuItems,
                selectedResource,
                _resourceByName,
                EventCallback.Factory.Create(this, () =>
                {
                    NavigationManager.NavigateTo(DashboardUrls.ResourcesUrl(resource: selectedResource.Name));
                    return Task.CompletedTask;
                }),
                EventCallback.Factory.Create<CommandViewModel>(this, ExecuteResourceCommandAsync),
                (resource, command) => DashboardCommandExecutor.IsExecuting(resource.Name, command.Name),
                showViewDetails: true,
                showConsoleLogsItem: false,
                showUrls: true);
        }
    }

    private ResourceViewModel? GetSelectedResource()
    {
        var name = PageViewModel?.SelectedResource.Id?.InstanceId;
        if (name == null)
        {
            return null;
        }
        _resourceByName.TryGetValue(name, out var resource);
        return resource;
    }

    private async Task ToggleTimestampAsync(bool showTimestamp, bool isTimestampUtc)
    {
        _showTimestamp = showTimestamp;
        _isTimestampUtc = isTimestampUtc;
        await UpdateConsoleLogSettingsAsync();
    }

    private async Task ToggleWrapLogsAsync(bool noWrapLogs)
    {
        _noWrapLogs = noWrapLogs;
        await UpdateConsoleLogSettingsAsync();
    }

    private async Task UpdateConsoleLogSettingsAsync()
    {
        await LocalStorage.SetUnprotectedAsync(BrowserStorageKeys.ConsoleLogConsoleSettings, new ConsoleLogConsoleSettings(_showTimestamp, _isTimestampUtc, _noWrapLogs));
        UpdateMenuButtons();
        StateHasChanged();
        await this.RefreshIfMobileAsync(_contentLayout);
    }

    private async Task ExecuteResourceCommandAsync(CommandViewModel command)
    {
        var selectedResource = GetSelectedResource();
        if (selectedResource is null)
        {
            Logger.LogWarning("No resource selected for command execution.");
            return;
        }

        await DashboardCommandExecutor.ExecuteAsync(selectedResource, command, GetResourceName);
    }

    private async Task CancelAllSubscriptionsAsync()
    {
        if (_consoleLogsSubscriptions.IsEmpty)
        {
            return;
        }

        var subscriptionsToCancel = _consoleLogsSubscriptions.Values.ToList();
        _consoleLogsSubscriptions.Clear();

        foreach (var subscription in subscriptionsToCancel)
        {
            subscription.Cancel();
        }

        // Wait for all subscriptions to finish
        var tasks = subscriptionsToCancel
            .Where(s => s.SubscriptionTask is not null)
            .Select(s => TaskHelpers.WaitIgnoreCancelAsync(s.SubscriptionTask))
            .ToArray();

        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks);
        }
    }

    private async Task SubscribeToAllResourcesAsync()
    {
        var availableResources = _resourceByName.Values
            .Where(r => !r.IsResourceHidden(_showHiddenResources))
            .ToList();

        Logger.LogDebug("Subscribing to {ResourceCount} resources for 'All' view.", availableResources.Count);

        if (availableResources.Count == 0)
        {
            Logger.LogDebug("No resources available to subscribe to for 'All' view - will show empty logs.");
            SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLoadingResources));
            await InvokeAsync(StateHasChanged);
            return;
        }

        // Set status to indicate we're starting to watch logs
        SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsWatchingLogs));
        await InvokeAsync(StateHasChanged);

        foreach (var resource in availableResources)
        {
            await SubscribeToSingleResourceAsync(resource);
        }

        Logger.LogDebug("Successfully created {SubscriptionCount} subscriptions for 'All' view.", _consoleLogsSubscriptions.Count);
    }

    private Task SubscribeToSingleResourceAsync(ResourceViewModel resource)
    {
        var resourceName = resource.Name;

        if (_consoleLogsSubscriptions.ContainsKey(resourceName))
        {
            Logger.LogDebug("Already subscribed to resource {ResourceName}.", resourceName);
            return Task.CompletedTask;
        }

        var subscription = new ConsoleLogsSubscription(resource, Logger);
        Logger.LogDebug("Creating new subscription {SubscriptionId} for resource {ResourceName}.", subscription.SubscriptionId, resourceName);

        // Add the subscription to the dictionary before starting the task
        if (_consoleLogsSubscriptions.TryAdd(resourceName, subscription))
        {
            LoadLogsForResource(subscription);
            Logger.LogDebug("Started log subscription task for resource {ResourceName}.", resourceName);
        }
        else
        {
            Logger.LogWarning("Failed to add subscription for resource {ResourceName} - may already exist.", resourceName);
        }

        return Task.CompletedTask;
    }

    private string GetResourceName(ResourceViewModel resource) => ResourceViewModel.GetResourceName(resource, _resourceByName);

    internal static ImmutableList<SelectViewModel<ResourceTypeDetails>> GetConsoleLogResourceSelectViewModels(
        ConcurrentDictionary<string, ResourceViewModel> resourcesByName,
        SelectViewModel<ResourceTypeDetails> allResourceViewModel,
        string resourceUnknownStateText,
        bool showHiddenResources,
        out SelectViewModel<ResourceTypeDetails>? optionToSelect)
    {
        var builder = ImmutableList.CreateBuilder<SelectViewModel<ResourceTypeDetails>>();

        foreach (var grouping in resourcesByName
            .Where(r => !r.Value.IsResourceHidden(showHiddenResources))
            .OrderBy(c => c.Value, ResourceViewModelNameComparer.Instance)
            .GroupBy(r => r.Value.DisplayName, StringComparers.ResourceName))
        {
            string resourceName;

            if (grouping.Count() > 1)
            {
                resourceName = grouping.Key;

                builder.Add(new SelectViewModel<ResourceTypeDetails>
                {
                    Id = ResourceTypeDetails.CreateResourceGrouping(resourceName, true),
                    Name = resourceName
                });
            }
            else
            {
                resourceName = grouping.First().Value.DisplayName;
            }

            foreach (var resource in grouping.Select(g => g.Value).OrderBy(r => r, ResourceViewModelNameComparer.Instance))
            {
                builder.Add(ToOption(resource, grouping.Count() > 1, resourceName));
            }
        }

        // If there are multiple resources, add "All" option.
        // If there is one resource then it is automatically selected.
        // If there are no resources, default to "All" (which will show no logs but is ready for when resources appear).
        if (builder.Count == 1)
        {
            optionToSelect = builder.Single();
        }
        else
        {
            builder.Insert(0, allResourceViewModel);
            optionToSelect = null;
        }

        return builder.ToImmutableList();

        SelectViewModel<ResourceTypeDetails> ToOption(ResourceViewModel resource, bool isReplica, string resourceName)
        {
            var id = isReplica
                ? ResourceTypeDetails.CreateReplicaInstance(resource.Name, resourceName)
                : ResourceTypeDetails.CreateSingleton(resource.Name, resourceName);

            return new SelectViewModel<ResourceTypeDetails>
            {
                Id = id,
                Name = GetDisplayText()
            };

            string GetDisplayText()
            {
                var resourceName = ResourceViewModel.GetResourceName(resource, resourcesByName);

                if (resource.HasNoState())
                {
                    return $"{resourceName} ({resourceUnknownStateText})";
                }

                if (resource.IsRunningState())
                {
                    return resourceName;
                }

                return $"{resourceName} ({resource.State})";
            }
        }
    }

    private void UpdateResourcesList()
    {
        _resources = GetConsoleLogResourceSelectViewModels(_resourceByName, _allResource, Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsUnknownState)], _showHiddenResources, out var optionToSelect);

        if (optionToSelect is not null)
        {
            PageViewModel.SelectedResource = optionToSelect;
        }
    }

    private void LoadLogsForResource(ConsoleLogsSubscription subscription)
    {
        Logger.LogDebug("Starting log subscription {SubscriptionId}.", subscription.SubscriptionId);
        var consoleLogsTask = Task.Run(async () =>
        {
            subscription.CancellationToken.ThrowIfCancellationRequested();

            Logger.LogDebug("Subscribing to console logs with subscription {SubscriptionId} to resource {ResourceName}.", subscription.SubscriptionId, subscription.Resource.Name);

            var logSubscription = DashboardClient.SubscribeConsoleLogs(subscription.Resource.Name, subscription.CancellationToken);

            // For "All" subscriptions, only update status once when starting
            if (_isSubscribedToAll && _consoleLogsSubscriptions.Count == 1)
            {
                SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsWatchingLogs));
                await InvokeAsync(StateHasChanged);
            }
            // For single resource subscriptions, always update status
            else if (!_isSubscribedToAll)
            {
                SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsWatchingLogs));
                await InvokeAsync(StateHasChanged);
            }

            var hasError = false;
            try
            {
                lock (_updateLogsLock)
                {
                    var pauseIntervals = PauseManager.ConsoleLogPauseIntervals;
                    Logger.LogDebug("Adding {PauseIntervalsCount} pause intervals on initial logs load.", pauseIntervals.Length);

                    foreach (var priorPause in pauseIntervals)
                    {
                        _logEntryChannel.Writer.TryWrite(new LogEntryToWrite(subscription.Resource.Name, LogEntry.CreatePause(GetResourceName(subscription.Resource), priorPause.Start, priorPause.End), LineNumber: null));
                    }
                }

                var resourcePrefix = ResourceViewModel.GetResourceName(subscription.Resource, _resourceByName);

                var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);
                await foreach (var batch in logSubscription.ConfigureAwait(false))
                {
                    subscription.CancellationToken.ThrowIfCancellationRequested();

                    if (batch.Count is 0)
                    {
                        continue;
                    }

                    foreach (var (lineNumber, content, isErrorOutput) in batch)
                    {
                        var logEntry = logParser.CreateLogEntry(content, isErrorOutput, resourcePrefix);

                        _logEntryChannel.Writer.TryWrite(new LogEntryToWrite(subscription.Resource.Name, logEntry, lineNumber));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // If the subscription is being canceled then error could be transient from cancellation. Ignore errors during cancellation.
                if (!subscription.CancellationToken.IsCancellationRequested)
                {
                    hasError = true;
                    Logger.LogError(ex, "Error watching logs for resource {ResourceName}.", subscription.Resource.Name);

                    // For single resource subscriptions or first subscription in "All" mode, update status
                    if (!_isSubscribedToAll)
                    {
                        SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsErrorWatchingLogs));
                        await InvokeAsync(StateHasChanged);
                    }
                }
            }
            finally
            {
                // Remove the subscription from tracking
                _consoleLogsSubscriptions.TryRemove(subscription.Resource.Name, out _);

                // If the subscription is being canceled then a new one could be starting.
                // Don't set the status when finishing because overwrite the status from the new subscription.
                // Also don't overwrite error status if an error occurred.
                if (!subscription.CancellationToken.IsCancellationRequested && !_isSubscribedToAll && !hasError)
                {
                    SetStatus(PageViewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsFinishedWatchingLogs));
                    await InvokeAsync(StateHasChanged);
                }

                Logger.LogDebug("Subscription {SubscriptionId} finished watching logs for resource {ResourceName}. Cancel duration: {Duration}", subscription.SubscriptionId, subscription.Resource.Name, subscription.GetCancelElapsedTime());
            }
        });

        subscription.SubscriptionTask = consoleLogsTask;
    }

    private async Task HandleSelectedOptionChangedAsync()
    {
        await this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
    }

    private async Task OnResourceChanged(ResourceViewModelChangeType changeType, ResourceViewModel resource)
    {
        if (changeType == ResourceViewModelChangeType.Upsert)
        {
            _resourceByName[resource.Name] = resource;
            UpdateResourcesList();

            // If we're subscribed to all resources and this is a new resource, subscribe to it
            if (_isSubscribedToAll && !_consoleLogsSubscriptions.ContainsKey(resource.Name) &&
                !resource.IsResourceHidden(_showHiddenResources))
            {
                await SubscribeToSingleResourceAsync(resource);
            }
        }
        else if (changeType == ResourceViewModelChangeType.Delete)
        {
            var removed = _resourceByName.TryRemove(resource.Name, out _);
            Debug.Assert(removed, "Cannot remove unknown resource.");

            // Cancel subscription for the deleted resource
            if (_consoleLogsSubscriptions.TryRemove(resource.Name, out var subscription))
            {
                // Fire and forget
                _ = Task.Run(async () =>
                {
                    subscription.Cancel();
                    if (subscription.SubscriptionTask is { } task)
                    {
                        await task;
                    }
                });
            }

            if (string.Equals(PageViewModel.SelectedResource.Id?.InstanceId, resource.Name, StringComparisons.ResourceName))
            {
                // The selected resource was deleted
                PageViewModel.SelectedResource = _allResource;
                await HandleSelectedOptionChangedAsync();
            }

            UpdateResourcesList();
        }
    }

    private async Task DownloadLogsAsync()
    {
        // Write all log entry content to a stream as UTF8 chars. Strip control sequences from log lines.
        var stream = new MemoryStream();
        lock (_updateLogsLock)
        {
            LogEntrySerializer.WriteLogEntriesToStream(_logEntries.GetEntries(), stream);
        }
        stream.Seek(0, SeekOrigin.Begin);

        await JS.DownloadFileAsync(GetFileName(), stream);
    }

    private string GetFileName()
    {
        var fileNamePrefix = _isSubscribedToAll
            ? "AllResources"
            : string.Join("_", PageViewModel.SelectedResource.Id!.InstanceId!.Split(Path.GetInvalidFileNameChars()));

        return $"{fileNamePrefix}-{TimeProvider.GetLocalNow().ToString("yyyyMMddhhmmss", CultureInfo.InvariantCulture)}.txt";
    }

    private async Task ClearConsoleLogs(ResourceKey? key)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        var newFilters = key is null
            ? ConsoleLogsFilters.CreateClearAll(now)
            : ConsoleLogsManager.Filters.WithResourceCleared(key.Value.ToString(), now);

        // Save filters to session storage so they're persisted when navigating to and from the console logs page.
        // This makes remove behavior persistent which matches removing telemetry.
        await ConsoleLogsManager.UpdateFiltersAsync(newFilters);
    }

    private void OnPausedChanged(bool isPaused)
    {
        Logger.LogDebug("Console logs paused new value: {IsPausedNewValue}", isPaused);

        var timestamp = DateTime.UtcNow;
        PauseManager.SetConsoleLogsPaused(isPaused, timestamp);

        if (PageViewModel.SelectedResource != null)
        {
            lock (_updateLogsLock)
            {
                if (isPaused)
                {
                    foreach (var subscription in _consoleLogsSubscriptions.Values)
                    {
                        Logger.LogDebug("Inserting new pause log entry for {Resource} starting at {StartTimestamp}.", subscription.Resource.Name, timestamp);
                        _logEntryChannel.Writer.TryWrite(new LogEntryToWrite(subscription.Resource.Name, LogEntry.CreatePause(GetResourceName(subscription.Resource), timestamp), LineNumber: null));
                    }
                }
                else
                {
                    var entries = _logEntries.GetEntries();
                    foreach (var subscription in _consoleLogsSubscriptions.Values)
                    {
                        var resourcePrefix = GetResourceName(subscription.Resource);
                        var lastResourceEntry = entries.LastOrDefault(e => e.ResourcePrefix == resourcePrefix);

                        if (lastResourceEntry?.Pause is { } pause)
                        {
                            Logger.LogDebug("Updating pause log entry for {Resource} starting at {StartTimestamp} with end of {EndTimestamp}.", subscription.Resource.Name, pause.StartTime, timestamp);
                            pause.EndTime = timestamp;
                        }
                    }
                }
            }

            _ = InvokeAsync(_logViewerRef.SafeRefreshDataAsync);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _aiContext?.Dispose();

        _consoleLogsFiltersChangedSubscription?.Dispose();

        _resourceSubscriptionCts.Cancel();
        _resourceSubscriptionCts.Dispose();
        await TaskHelpers.WaitIgnoreCancelAsync(_resourceSubscriptionTask);
        await TaskHelpers.WaitIgnoreCancelAsync(_logEntryChannelReaderTask);

        await CancelAllSubscriptionsAsync();
        TelemetryContext.Dispose();
    }

    public class ConsoleLogsViewModel
    {
        public required string Status { get; set; }
        public required SelectViewModel<ResourceTypeDetails> SelectedResource { get; set; }
    }

    public record ConsoleLogsPageState(string? SelectedResource);

    public record ConsoleLogConsoleSettings(bool ShowTimestamp, bool IsTimestampUtc, bool NoWrapLogs);

    public Task UpdateViewModelFromQueryAsync(ConsoleLogsViewModel viewModel)
    {
        if (_resources is not null)
        {
            if (ResourceName is not null)
            {
                viewModel.SelectedResource = GetSelectedOption();
                viewModel.Status ??= Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLogsNotYetAvailable)];
                return Task.CompletedTask;
            }
            else if (TryGetSingleResource() is { } r)
            {
                // If there is no resource selected and there is only one resource available, select it.
                viewModel.SelectedResource = _resources.GetResource(Logger, r.Name, canSelectGrouping: false, fallbackViewModel: _allResource);
                return this.AfterViewModelChangedAsync(_contentLayout, waitToApplyMobileChange: false);
            }
        }

        viewModel.SelectedResource = _allResource;
        SetStatus(viewModel, nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsLoadingResources));
        return Task.CompletedTask;

        ResourceViewModel? TryGetSingleResource()
        {
            var actualResources = _resourceByName.Values.Where(r => !r.IsResourceHidden(showHiddenResources: _showHiddenResources)).ToList();
            return actualResources.Count == 1 ? actualResources[0] : null;
        }
    }

    public string GetUrlFromSerializableViewModel(ConsoleLogsPageState serializable)
    {
        return DashboardUrls.ConsoleLogsUrl(serializable.SelectedResource);
    }

    public ConsoleLogsPageState ConvertViewModelToSerializable()
    {
        var selectedResourceName = GetSelectedResource() is { } selectedResource
            ? GetResourceName(selectedResource)
            : null;
        return new ConsoleLogsPageState(selectedResourceName);
    }

    private AIContext CreateAIContext()
    {
        return AIContextProvider.AddNew(nameof(ConsoleLogs), c =>
        {
            c.BuildIceBreakers = (builder, context) =>
            {
                if (GetSelectedResource() is { } selectedResource)
                {
                    builder.ConsoleLogs(context, selectedResource);
                }
                else
                {
                    builder.ConsoleLogs(context);
                }
            };
        });
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.ConsoleLogs);

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.ConsoleLogsShowTimestamp, new AspireTelemetryProperty(_showTimestamp, AspireTelemetryPropertyType.UserSetting))
        ], Logger);
    }
}
