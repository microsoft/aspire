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
    private static readonly TimeSpan s_noLogsMessageDelay = TimeSpan.FromSeconds(1.5);

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
    private CancellationTokenSource? _showNoLogsMessageCts;
    private Task? _showNoLogsMessageDelayTask;
    private AIContext? _aiContext;
    private LogViewer? _logViewerRef;
    private Controls.TerminalView? _terminalViewRef;
    private bool _selectedResourceHasTerminal;
    private string? _terminalResourceName;
    private int _terminalReplicaIndex;
    private Controls.TerminalToolbarState? _terminalToolbarState;
    private IReadOnlyList<Controls.TerminalSizePreset> _terminalSizePresets = Array.Empty<Controls.TerminalSizePreset>();

    // View toggle for terminal resources. The page surfaces both LogViewer
    // and TerminalView in MainSection (both stay mounted so flipping does
    // not tear down the PTY or the log subscription) and uses CSS to hide
    // the inactive one. For non-terminal resources only LogViewer is shown
    // and these fields are unused.
    private ConsoleLogsView _activeView = ConsoleLogsView.Console;
    // Set true the first time the user explicitly picks a view from the
    // dropdown. Once true we suppress all auto-switching (PTY attach / exit)
    // so we never yank the user away from a view they're actively reading.
    // Reset on resource change.
    private bool _userPickedView;
    // Tracks the last toolbar status seen for the current terminal so we can
    // detect the connecting → connected transition. That transition is the
    // unambiguous "PTY attached" edge that drives the auto-switch to the
    // Terminal view, and it works for both initial connect and the WS
    // reconnect that follows a resource stop+start cycle.
    private string? _lastTerminalStatus;
    // Tracks whether the currently-selected terminal resource is in a stopped
    // KnownResourceState (Exited / Finished / FailedToStart). We flip the
    // page back to Console on the running → stopped edge so that when the
    // user manually stops the resource — the producer is killed by DCP and
    // never emits an HMP1 Exit message, so client.onExit on the JS side
    // never fires — the page still leaves Terminal and shows the resource's
    // stop/exit log lines.
    private bool _selectedTerminalResourceStopped;
    // Set when client.onExit fires on the JS side, cleared the next time we
    // observe a genuine "connecting" toolbar snapshot. While this is true the
    // auto-switch-to-Terminal path is suppressed so a late in-flight
    // primary/viewer snapshot arriving after onExit — the JS side may have
    // already queued a notifyToolbar callback before the exit propagated —
    // can't be mistaken for a fresh attach and yank the user back to Terminal
    // right after we just flipped to Console. A real reconnect always goes
    // through the WebSocket lifecycle and emits "connecting" first, which is
    // what clears this gate and re-arms the attach edge.
    private bool _terminalExitedAwaitingReattach;
    // Tracks the view that was rendered to the DOM on the previous render
    // pass. When the active view flips back to Terminal we need to nudge
    // xterm.js to relayout because the wrapper's display:none → visible
    // transition may not trigger ResizeObserver in every browser.
    private ConsoleLogsView? _lastRenderedView;

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
    private bool _showNoLogsMessage;
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
            var hasNewLogEntry = false;
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
                    hasNewLogEntry = true;
                }
            }

            await InvokeAsync(async () =>
            {
                if (hasNewLogEntry)
                {
                    _showNoLogsMessage = false;
                }

                await _logViewerRef.SafeRefreshDataAsync();
            });
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

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // After a layout transition (e.g. mobile→desktop viewport flip moves
        // the toolbar back inline from the mobile filter dialog) the toolbar
        // RenderFragment re-evaluates against the page's current state. If
        // _terminalToolbarState was cleared during the transition but the
        // JS terminal is still alive in MainSection, the toolbar would not
        // re-render until the JS side happens to push a new snapshot — and
        // JS suppresses no-op pushes via change detection. Ask JS to re-push
        // so the toolbar controls reappear.
        if (_selectedResourceHasTerminal &&
            _terminalViewRef is { } terminalView &&
            _terminalToolbarState is null)
        {
            await terminalView.RefreshToolbarStateAsync();
        }

        // Detect a view-flip TO Terminal and prod xterm to relayout. The
        // wrapper element transitions from display:none to visible on this
        // render and ResizeObserver is not guaranteed to fire for that
        // box-tree change. Without this nudge xterm can stay sized to its
        // pre-hide dimensions until the next external resize.
        if (_selectedResourceHasTerminal &&
            _activeView == ConsoleLogsView.Terminal &&
            _lastRenderedView != ConsoleLogsView.Terminal &&
            _terminalViewRef is { } terminalForLayout)
        {
            await terminalForLayout.RefreshLayoutAsync();
        }

        _lastRenderedView = _activeView;
    }

    private async Task SubscribeAsync(bool isAllSelected, string? selectedResourceName)
    {
        Logger.LogDebug("Subscription change needed. IsAllSelected: {IsAllSelected}, SelectedResource: {SelectedResource}", isAllSelected, selectedResourceName);
        _aiContext?.ContextHasChanged();

        // Detect whether the selected resource has terminal support
        _selectedResourceHasTerminal = false;
        _terminalResourceName = null;
        _terminalReplicaIndex = 0;
        // Drop any prior terminal's toolbar state so we don't briefly render
        // the wrong badge/dims/dropdown for the new resource while the JS
        // terminal is initializing and pushing its first snapshot.
        _terminalToolbarState = null;
        // Reset the view-toggle latch for the new resource so the next
        // PTY-attach can auto-switch and the user has a fresh slate to
        // pick a view. Default the view to Console so any pre-PTY hosting
        // messages (e.g. WaitFor) are visible immediately on selection.
        _userPickedView = false;
        _lastTerminalStatus = null;
        _selectedTerminalResourceStopped = false;
        _terminalExitedAwaitingReattach = false;
        _activeView = ConsoleLogsView.Console;

        if (!isAllSelected && selectedResourceName is not null &&
            _resourceByName.TryGetValue(selectedResourceName, out var selectedResource) &&
            selectedResource.HasTerminal() &&
            selectedResource.TryGetTerminalReplicaInfo(out var replicaIndex, out _))
        {
            _selectedResourceHasTerminal = true;
            _terminalResourceName = selectedResource.DisplayName;
            _terminalReplicaIndex = replicaIndex;
            // Seed the stopped tracker from the current snapshot so the
            // running → stopped edge detection below in OnResourceChanged
            // fires only on genuine state transitions, not on the very
            // first snapshot we observe after subscribing to a resource
            // that was already stopped when the page loaded.
            _selectedTerminalResourceStopped = selectedResource.IsStopped();
            Logger.LogDebug("Resource '{ResourceName}' has terminal at replica {ReplicaIndex}", selectedResourceName, replicaIndex);
            // Intentionally fall through to the normal subscription path so
            // the resource's console log stream is collected even while the
            // user is on the Terminal view. The Console view in the View
            // dropdown shows these logs and they're needed for pre-PTY
            // hosting messages (WaitFor, startup failures) and post-PTY
            // exit messages — flipping to the Terminal view should never
            // cause us to miss anything from the console stream.
        }

        // Cancel all existing subscriptions
        await CancelAllSubscriptionsAsync();

        // Clear log entries for new subscription
        Logger.LogDebug("Creating new log entries collection.");
        lock (_updateLogsLock)
        {
            _logEntries.Clear(keepActivePauseEntries: false);
        }
        ResetNoLogsMessage();

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

        if (!_consoleLogsSubscriptions.IsEmpty)
        {
            StartNoLogsMessageDelay();
        }

        // Rebuild the options menu now that _selectedResourceHasTerminal
        // reflects the new selection. The earlier UpdateMenuButtons() call
        // in OnParametersSetAsync ran before this method updated the flag,
        // so without this the menu keeps the previous resource's shape —
        // e.g. the Console/Terminal view toggle would linger on a resource
        // that has no WithTerminal(), and be missing on the reverse switch.
        UpdateMenuButtons();
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

        // View toggle (Console / Terminal): only meaningful for terminal-
        // enabled resources; keeps the menu identical to today for the common
        // no-terminal case.
        if (_selectedResourceHasTerminal)
        {
            _logsMenuItems.Add(new()
            {
                OnClick = () => HandleViewChangedAsync(nameof(ConsoleLogsView.Console)),
                Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsViewConsoleOption)],
                Icon = new Icons.Regular.Size16.TextBulletListLtr()
            });

            _logsMenuItems.Add(new()
            {
                OnClick = () => HandleViewChangedAsync(nameof(ConsoleLogsView.Terminal)),
                Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.ConsoleLogsViewTerminalOption)],
                Icon = new Icons.Regular.Size16.Prompt()
            });

            _logsMenuItems.Add(new()
            {
                IsDivider = true
            });
        }

        if (_activeView == ConsoleLogsView.Terminal)
        {
            // Terminal-only items: font +/- and a nested Terminal dimensions
            // submenu carrying the same presets the old inline toolbar used.
            // We render these unconditionally so the menu structure is stable
            // even before the first toolbar-state snapshot arrives; enabled
            // state and current font readout come from _terminalToolbarState
            // when present.
            var terminalState = _terminalToolbarState;
            var fontPx = terminalState?.FontPx ?? 0;
            var fontControlsEnabled = terminalState?.FontControlsEnabled ?? false;
            var sizeSelectEnabled = terminalState?.SizeSelectEnabled ?? false;
            var currentSizeKey = terminalState?.SizeKey;

            _logsMenuItems.Add(new()
            {
                OnClick = TerminalFontMinusAsync,
                Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.TerminalToolbarDecreaseFontSize)],
                Icon = new Icons.Regular.Size16.Subtract(),
                IsDisabled = !fontControlsEnabled || fontPx <= TerminalFontMin,
            });

            _logsMenuItems.Add(new()
            {
                OnClick = TerminalFontPlusAsync,
                Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.TerminalToolbarIncreaseFontSize)],
                Icon = new Icons.Regular.Size16.Add(),
                IsDisabled = !fontControlsEnabled || fontPx >= TerminalFontMax,
            });

            if (_terminalSizePresets.Count > 0)
            {
                var nested = new List<MenuButtonItem>();
                var displayPresets = terminalState is not null
                    ? GetTerminalSizePresetsForDisplay(terminalState)
                    : _terminalSizePresets;
                foreach (var preset in displayPresets)
                {
                    // Capture the value locally so the click handler doesn't
                    // see whatever `preset` ends up as after the foreach.
                    var value = preset.Value;
                    nested.Add(new()
                    {
                        OnClick = () => TerminalSizeChangedAsync(value),
                        Text = preset.Label,
                        // Bare tick on the active preset; no icon on the others
                        // so the menu doesn't look like a checkbox list.
                        Icon = string.Equals(currentSizeKey, value, StringComparison.Ordinal)
                            ? new Icons.Regular.Size16.Checkmark()
                            : null,
                        IsDisabled = !sizeSelectEnabled,
                    });
                }

                _logsMenuItems.Add(new()
                {
                    Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.TerminalToolbarGridSize)],
                    Icon = new Icons.Regular.Size16.ArrowExpand(),
                    NestedMenuItems = nested,
                });
            }
        }
        else
        {
            // Console-view items: preserved from the original menu.
            _logsMenuItems.Add(new()
            {
                IsDisabled = PageViewModel.SelectedResource is null && !_isSubscribedToAll,
                OnClick = DownloadLogsAsync,
                Text = Loc[nameof(Dashboard.Resources.ConsoleLogs.DownloadLogs)],
                Icon = new Icons.Regular.Size16.ArrowDownload()
            });

            _logsMenuItems.Add(new()
            {
                IsDivider = true
            });

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
        }

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

    private void StartNoLogsMessageDelay()
    {
        ResetNoLogsMessage();

        _showNoLogsMessageCts = new();
        _showNoLogsMessageDelayTask = ShowNoLogsMessageAfterDelayAsync(_showNoLogsMessageCts.Token);
    }

    private void ResetNoLogsMessage()
    {
        _showNoLogsMessageCts?.Cancel();
        _showNoLogsMessage = false;
    }

    private async Task ShowNoLogsMessageAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(s_noLogsMessageDelay, cancellationToken);

            await InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    var hasNoLogEntries = false;
                    lock (_updateLogsLock)
                    {
                        hasNoLogEntries = _logEntries.EntriesCount == 0;
                    }

                    _showNoLogsMessage = hasNoLogEntries;
                    StateHasChanged();
                }
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to show the no logs message.");
        }
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

            // If the currently-selected terminal resource just transitioned
            // from running → stopped, treat that the same as a clean PTY
            // exit and flip the page back to Console. We do this here in
            // addition to OnTerminalExitedAsync because manual "Stop" on a
            // resource kills the producer process directly (DCP sends a
            // termination signal); the producer never sends an HMP1 Exit
            // frame, so client.onExit on the JS side never fires. The
            // resource snapshot is the only reliable signal for that path.
            if (_selectedResourceHasTerminal &&
                string.Equals(resource.Name, PageViewModel.SelectedResource.Id?.InstanceId, StringComparisons.ResourceName))
            {
                // OnResourceChanged runs on the resource-subscription
                // background task (see the Task.Run wrapping the await
                // foreach in InitializeAsync). The terminal callbacks
                // OnTerminalToolbarStateChangedAsync and OnTerminalExitedAsync
                // read/write these same fields (_selectedTerminalResourceStopped
                // gates the auto-switch, _activeView is the switch target) on
                // the renderer dispatcher. Hop the entire read/modify/write
                // sequence onto the dispatcher so those two writers are
                // serialized and neither observes a torn intermediate state.
                var stoppedSnapshot = resource.IsStopped();
                await InvokeAsync(() =>
                {
                    var wasStopped = _selectedTerminalResourceStopped;
                    _selectedTerminalResourceStopped = stoppedSnapshot;

                    if (!wasStopped && stoppedSnapshot &&
                        !_userPickedView && _activeView == ConsoleLogsView.Terminal)
                    {
                        // Flip back to Console. We deliberately do NOT
                        // synthetically set _lastTerminalStatus to
                        // "connecting" here: the JS side may still have
                        // an in-flight `primary` snapshot, and pretending
                        // we just saw "connecting" would make that next
                        // snapshot look like a fresh attach edge and yank
                        // the user straight back to Terminal. The real WS
                        // will go through close → connecting → connected
                        // naturally on restart, so the genuine edge fires
                        // then. The _selectedTerminalResourceStopped gate
                        // in the auto-switch logic suppresses any spurious
                        // edges in the meantime.
                        _activeView = ConsoleLogsView.Console;
                        StateHasChanged();
                    }
                });
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
        ResetNoLogsMessage();
        _showNoLogsMessageCts?.Cancel();
        await TaskHelpers.WaitIgnoreCancelAsync(_showNoLogsMessageDelayTask);

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

    // --- Terminal toolbar wiring -----------------------------------------
    //
    // The TerminalView component pushes a TerminalToolbarState snapshot up
    // here whenever the underlying xterm/HMP1 state changes (role flips,
    // resize, font change). Those snapshots drive the page-level toolbar
    // that replaces the in-frame chrome the terminal used to render itself.
    // JS remains the source of truth for terminal state; this layer just
    // mirrors the latest snapshot and routes user actions back to JS via
    // the TerminalView public methods.
    private const int TerminalFontStep = 1;
    private const int TerminalFontMin = 4;
    private const int TerminalFontMax = 72;

    private async Task OnTerminalToolbarStateChangedAsync(Controls.TerminalToolbarState state)
    {
        _terminalToolbarState = state;

        // First snapshot after init — fetch the size preset list once so
        // the dropdown stays in sync with whatever JS knows how to handle.
        if (_terminalSizePresets.Count == 0 && _terminalViewRef is not null)
        {
            // The JS side ships labels as English string literals (it has no
            // localization stack of its own). Numeric labels like "80×24" are
            // language-neutral and pass through unchanged, but "Auto" is an
            // English word and must come from the dashboard's .resx so it
            // matches the rest of the terminal toolbar in every supported
            // culture. Apply the localized label here, where we still have
            // access to IStringLocalizer<Resources.ConsoleLogs>, before
            // handing the list to FluentSelect.
            var presets = await _terminalViewRef.GetSizePresetsAsync();
            _terminalSizePresets = presets
                .Select(p => p.Value == "auto"
                    ? p with { Label = Loc[nameof(Dashboard.Resources.ConsoleLogs.TerminalToolbarGridSizeAuto)] }
                    : p)
                .ToList();
        }

        // Auto-switch to the Terminal view on the rising edge of "connected"
        // — i.e. when status moves FROM connecting TO any other value. That
        // edge fires both on initial WebSocket connect and on the WS
        // reconnect that follows a resource stop+start cycle (the terminal
        // host process restarts with the replica, so the consumer WS goes
        // down and comes back up). Triggering on the edge rather than on
        // any non-connecting snapshot avoids three failure modes:
        //   1. A stale post-onExit snapshot still showing primary/no-primary
        //      would otherwise immediately undo the PTY-exit auto-switch
        //      back to Console.
        //   2. Routine reconnect-after-network-blip snapshots would
        //      otherwise re-snap the user to Terminal even though they
        //      have been reading Console logs the whole time.
        //   3. On manual Stop the JS side may still have an in-flight
        //      `primary` snapshot. The _selectedTerminalResourceStopped
        //      gate below suppresses the auto-switch while the resource
        //      is stopped so that stale snapshot can't drag the user back
        //      to a now-defunct Terminal view.
        // We still respect the user's manual pick — once they choose a view
        // the latch in _userPickedView suppresses all auto-switching.
        var previousStatus = _lastTerminalStatus;
        _lastTerminalStatus = state.Status;

        // A genuine WebSocket reconnect always emits "connecting" before the
        // next attach snapshot. Use that transition to clear the post-onExit
        // gate: any late in-flight snapshot arriving without a preceding
        // "connecting" is stale and will be ignored by the guard below.
        var isConnectingStatus = string.Equals(state.Status, "connecting", StringComparison.Ordinal);
        if (isConnectingStatus)
        {
            _terminalExitedAwaitingReattach = false;
        }

        // The initial JS-side snapshot may already report a non-connecting
        // status if the WebSocket handshake completes before the first
        // notifyToolbar fires. Treat the "no prior status" case the same as
        // "connecting" so the initial-attach edge still triggers.
        var wasConnecting = previousStatus is null
            || string.Equals(previousStatus, "connecting", StringComparison.Ordinal);
        var isConnected = !isConnectingStatus;

        if (_selectedResourceHasTerminal &&
            !_selectedTerminalResourceStopped &&
            !_terminalExitedAwaitingReattach &&
            !_userPickedView &&
            wasConnecting &&
            isConnected)
        {
            _activeView = ConsoleLogsView.Terminal;
        }

        UpdateMenuButtons();
        StateHasChanged();
    }

    private Task OnTerminalExitedAsync(Controls.TerminalExitInfo info)
    {
        Logger.LogDebug("Terminal for resource '{ResourceName}' exited with code {ExitCode}.", _terminalResourceName, info.ExitCode);

        // Arm the post-exit gate. The auto-switch-to-Terminal path is now
        // suppressed until we observe a genuine "connecting" toolbar snapshot
        // (which only fires when the JS side opens a fresh WebSocket for the
        // restarted terminal host). This defends against a race where the JS
        // side had already queued a `primary`/`viewer` notifyToolbar callback
        // before onExit propagated: without the gate that stale snapshot would
        // look like a fresh attach edge (previous status = whatever, new
        // status = primary) and yank the user back to Terminal right after we
        // just flipped to Console.
        _terminalExitedAwaitingReattach = true;

        // PTY closed — flip back to Console so the user sees the resource's
        // final log lines (including the hosting "exited" message and any
        // tail-end output). Respect the user's manual choice if they have
        // already picked a view since this session started.
        if (_selectedResourceHasTerminal &&
            !_userPickedView &&
            _activeView == ConsoleLogsView.Terminal)
        {
            _activeView = ConsoleLogsView.Console;
            UpdateMenuButtons();
            StateHasChanged();
        }

        return Task.CompletedTask;
    }

    private Task HandleViewChangedAsync(string? newView)
    {
        if (newView is null)
        {
            return Task.CompletedTask;
        }

        // Parse defensively so a bad enum value can't tear down the page.
        // The menu-item click handlers pass nameof(...) literals today, but
        // this indirection keeps the entry point safe if it grows a new
        // caller.
        if (!Enum.TryParse<ConsoleLogsView>(newView, ignoreCase: true, out var parsed))
        {
            return Task.CompletedTask;
        }

        // Latch the user's choice so neither the PTY-attached nor PTY-exited
        // auto-switch can override it for the rest of this resource session.
        _userPickedView = true;
        _activeView = parsed;
        UpdateMenuButtons();
        StateHasChanged();
        return Task.CompletedTask;
    }

    // Test-only accessors. The view-toggle latch and auto-switch behavior
    // are reachable from bUnit only by inspecting the internal state — the
    // user-visible signal (display:none on a wrapper div) is awkward to
    // assert against in bUnit. These mirror existing internal hooks (e.g.
    // _logEntries) used by ConsoleLogsTests.
    internal ConsoleLogsView ActiveViewForTest => _activeView;
    internal Task HandleViewChangedForTestAsync(string? newView) => HandleViewChangedAsync(newView);

    private Task TerminalFontMinusAsync()
    {
        if (_terminalToolbarState is not { } s || _terminalViewRef is null)
        {
            return Task.CompletedTask;
        }
        return _terminalViewRef.SetFontSizeAsync(Math.Max(TerminalFontMin, s.FontPx - TerminalFontStep));
    }

    private Task TerminalFontPlusAsync()
    {
        if (_terminalToolbarState is not { } s || _terminalViewRef is null)
        {
            return Task.CompletedTask;
        }
        return _terminalViewRef.SetFontSizeAsync(Math.Min(TerminalFontMax, s.FontPx + TerminalFontStep));
    }

    private Task TerminalSizeChangedAsync(string? newKey)
    {
        if (newKey is null || _terminalViewRef is null)
        {
            return Task.CompletedTask;
        }
        return _terminalViewRef.SetSizeModeAsync(newKey);
    }

    // Rebuild the presets list for rendering so the "Fit" (auto) entry shows
    // the cols x rows Fit mode *would* produce right now — using the JS-side
    // FitCols/FitRows preview rather than the actual current grid dims, which
    // may be locked by a fixed preset. Called from the menu builder on each
    // UpdateMenuButtons pass; the list is short (~6 items) and only allocates
    // when there is a Fit preview to fold in.
    private IReadOnlyList<Controls.TerminalSizePreset> GetTerminalSizePresetsForDisplay(Controls.TerminalToolbarState state)
    {
        if (state.FitCols <= 0 || state.FitRows <= 0)
        {
            return _terminalSizePresets;
        }

        var baseAutoLabel = Loc[nameof(Dashboard.Resources.ConsoleLogs.TerminalToolbarGridSizeAuto)];
        var autoLabel = $"{baseAutoLabel} ({state.FitCols}×{state.FitRows})";

        return _terminalSizePresets
            .Select(p => p.Value == "auto" ? p with { Label = autoLabel } : p)
            .ToList();
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Page, TelemetryComponentIds.ConsoleLogs);

    public void UpdateTelemetryProperties()
    {
        TelemetryContext.UpdateTelemetryProperties([
            new ComponentTelemetryProperty(TelemetryPropertyKeys.ConsoleLogsShowTimestamp, new AspireTelemetryProperty(_showTimestamp, AspireTelemetryPropertyType.UserSetting))
        ], Logger);
    }

    /// <summary>
    /// The two MainSection contents the <see cref="ConsoleLogs"/> page can show
    /// for a resource that has <c>WithTerminal()</c> applied. Non-terminal
    /// resources implicitly always show <see cref="Console"/>.
    /// </summary>
    public enum ConsoleLogsView
    {
        /// <summary>The resource's standard log stream (LogViewer).</summary>
        Console,
        /// <summary>The interactive xterm.js terminal (TerminalView).</summary>
        Terminal,
    }
}
