// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using FluentMessageIntent = Microsoft.FluentUI.AspNetCore.Components.MessageBarIntent;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// A collapsible, tabbed dock of terminals owned by the AppHost process, toggled with <c>`</c>.
/// </summary>
/// <remarks>
/// <para>
/// The dock's chrome (visible/collapsed, which tab is selected) is per-browser-circuit, but the terminals
/// themselves live in the AppHost. Two browsers therefore see the same tabs and the same output, and closing
/// the dock in one browser does not disturb the other or stop any workload.
/// </para>
/// <para>
/// Distinct from resource terminals, which are DCP-owned and reached through the terminal host.
/// </para>
/// </remarks>
public sealed partial class TerminalDock : ComponentBase, IGlobalKeydownListener, IAsyncDisposable
{
    private const int DefaultHeightPx = 320;
    private const int MinimumHeightPx = 120;
    private const int MaximumHeightPx = 1200;

    private readonly List<TerminalDescriptor> _terminals = [];
    private readonly Dictionary<string, ResourceViewModel> _resourceByName = new(StringComparers.ResourceName);
    private ResourceTerminalLink[] _resourceTerminalLinks = [];
    private readonly Dictionary<string, TerminalView> _terminalViews = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _elementIdPrefix = $"terminal-dock-{Guid.NewGuid():N}";

    private bool _hasBeenOpened;
    private bool _isVisible;
    private bool _disposed;
    private string? _activeTerminalId;

    private int _heightPx = DefaultHeightPx;
    private int _maximumHeightPx = MaximumHeightPx;
    private Task? _watchTask;
    private Task? _resourceWatchTask;
    private bool _jsInitializationStarted;
    private Task? _jsInitializationTask;
    private bool _resizeHandleRegistrationStarted;
    private bool _tabNavigationRegistrationStarted;
    private Task? _disposeTask;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<TerminalDock>? _selfRef;
    private ElementReference _dockElement;

    /// <summary>
    /// Terminals the user has popped out into their own window. The dock keeps the tab — the terminal is still
    /// running and still AppHost-owned — but stops rendering a viewer for it, so the window is the only place it is
    /// on screen. That is deliberate: a dock pane and a detached window are the same small viewport twice over, and
    /// two attached viewers would fight over the HMP1 primary role and therefore over the PTY's grid size.
    /// </summary>
    private readonly HashSet<string> _detachedTerminalIds = [];
    private readonly HashSet<string> _windowTrackingReadyIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _recoveringWindowIds = new(StringComparer.Ordinal);

    private TerminalWindowButton? _windowButton;
    private bool _popupBlocked;

    internal ComponentTelemetryContext? TelemetryContext { get; private set; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required IResourceRepository ResourceRepository { get; init; }

    [Inject]
    public required ShortcutManager ShortcutManager { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.TerminalStrings> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalDock> Logger { get; init; }

    [Inject]
    public required Aspire.Dashboard.Model.INotificationService NotificationService { get; init; }

    [Inject]
    public required Microsoft.FluentUI.AspNetCore.Components.INotificationService ToastService { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    public IReadOnlySet<AspireKeyboardShortcut> SubscribedShortcuts { get; } = new HashSet<AspireKeyboardShortcut>
    {
        AspireKeyboardShortcut.ToggleTerminalDock
    };

    protected override void OnInitialized()
    {
        if (DashboardClient.IsEnabled && !DashboardClient.IsReadOnly)
        {
            ShortcutManager.AddGlobalKeydownListener(this);

            // Keep only metadata watching eager: AspireTerminal.Show() must reveal the dock even before
            // its first manual opening. Resource links, browser controls, and viewers can wait.
            _watchTask = Task.Run(() => WatchTerminalsAsync(_cts.Token), _cts.Token);
        }
    }

    public Task OnPageKeyDownAsync(AspireKeyboardShortcut shortcut)
        => shortcut == AspireKeyboardShortcut.ToggleTerminalDock ? ToggleAsync() : Task.CompletedTask;

    /// <summary>
    /// Shows the dock, or hides it if it is already showing.
    /// </summary>
    /// <remarks>
    /// Public so the header button can drive the dock. The keyboard shortcut alone is not enough: <c>`</c> is
    /// suppressed whenever focus is in a terminal or any other text input, because it types <c>`</c> there, so the
    /// dock needs an affordance that works regardless of where focus happens to be.
    /// </remarks>
    public Task ToggleAsync() => InvokeAsync(() =>
    {
        if (_disposed || !DashboardClient.IsEnabled || DashboardClient.IsReadOnly)
        {
            return;
        }

        if (_isVisible)
        {
            Hide();
        }
        else
        {
            Show(TerminalDockTrigger.User);
            StateHasChanged();
        }

    });

    private void Show(TerminalDockTrigger trigger)
    {
        if (_isVisible)
        {
            return;
        }

        // Construction eagerly starts the subscription, not a user-visible dock session.
        // Each hidden-to-visible transition needs a fresh correlation and its own closing event.
        TelemetryContext = new ComponentTelemetryContext(ComponentType.Control, TelemetryComponentIds.TerminalDock);
        TelemetryContextProvider.Initialize(TelemetryContext);
        TelemetryContext.UpdateTelemetryProperties(
            [new(TelemetryPropertyKeys.TerminalDockTrigger, new AspireTelemetryProperty(trigger.ToString()))], Logger);
        _hasBeenOpened = true;
        _isVisible = true;
        if (_resourceWatchTask is null && DashboardClient.IsEnabled && !DashboardClient.IsReadOnly)
        {
            // The initial snapshot supplies resources accumulated before opening. Keep watching after collapse
            // so reopening preserves the existing dock state without restarting its subscriptions.
            _resourceWatchTask = Task.Run(() => WatchResourceTerminalsAsync(_cts.Token), _cts.Token);
        }
    }

    protected override Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || !_hasBeenOpened || _jsInitializationStarted)
        {
            return Task.CompletedTask;
        }

        // Import and registration can yield while another render runs. Mark the entire operation started
        // synchronously, not just when the module arrives, and retain its task for disposal to join.
        _jsInitializationStarted = true;
        return _jsInitializationTask = InitializeJsAsync();
    }

    private async Task InitializeJsAsync()
    {
        try
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/TerminalDock.razor.js").ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }
            _selfRef = DotNetObjectReference.Create(this);
            // Mark attempts before invoking JS so disposal also cleans up partially registered listeners.
            _resizeHandleRegistrationStarted = true;
            await _jsModule.InvokeVoidAsync("registerResizeHandle", _dockElement, _selfRef, MinimumHeightPx, MaximumHeightPx).ConfigureAwait(true);
            if (!_disposed)
            {
                _tabNavigationRegistrationStarted = true;
                await _jsModule.InvokeVoidAsync("registerTabNavigation", _dockElement).ConfigureAwait(true);
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; disposal will release any managed references.
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to initialize terminal dock controls.");
            throw;
        }
    }

    /// <summary>
    /// Updates the dock height after pointer or keyboard resizing, or a viewport size change.
    /// </summary>
    /// <param name="heightPx">The requested dock height in CSS pixels.</param>
    /// <param name="viewportHeightPx">The browser viewport height in CSS pixels.</param>
    /// <returns>A task that completes after the dock state is updated.</returns>
    [JSInvokable]
    public Task SetHeightAsync(int heightPx, int viewportHeightPx) => InvokeAsync(() =>
    {
        if (_disposed)
        {
            return;
        }

        _maximumHeightPx = Math.Clamp(viewportHeightPx, 1, MaximumHeightPx);
        _heightPx = Math.Clamp(heightPx, EffectiveMinimumHeightPx, _maximumHeightPx);
        StateHasChanged();
    });

    private int EffectiveMinimumHeightPx => Math.Min(MinimumHeightPx, _maximumHeightPx);

    private void Hide()
    {
        TelemetryContext?.Dispose();
        TelemetryContext = null;
        _isVisible = false;
    }

    private void Activate(string terminalId)
    {
        if (!_terminals.Any(t => t.TerminalId == terminalId))
        {
            // A queued click can arrive after the watch stream removes its tab.
            Logger.LogDebug("Ignored selection of removed dock terminal {TerminalId}.", terminalId);
            return;
        }

        _activeTerminalId = terminalId;
        StateHasChanged();
    }

    private bool IsPanelVisible => _terminals.Count == 0;

    private bool IsPaneActive(string terminalId) => terminalId == _activeTerminalId;

    private string GetTabId(string terminalId) => $"{_elementIdPrefix}-tab-{terminalId}";

    private string GetPaneId(string terminalId) => $"{_elementIdPrefix}-pane-{terminalId}";

    private string? ActiveTerminalWindowUrl => _activeTerminalId is { } id
        ? $"terminal-window/apphost/{Uri.EscapeDataString(id)}"
        : null;

    private int? ActiveTerminalFontSize => _activeTerminalId is { } id && _terminalViews.TryGetValue(id, out var view)
        ? view.FontSize
        : null;

    private static void OnTerminalToolbarStateChanged(TerminalToolbarState _)
    {
    }

    private void OnWindowsAdopted(string[] keys)
    {
        if (!_disposed)
        {
            _windowTrackingReadyIds.UnionWith(keys);
        }
    }

    private void OnWindowTrackingFailed(string[] keys)
    {
        if (!_disposed)
        {
            foreach (var key in keys.Where(key => _terminals.Any(terminal => terminal.TerminalId == key)))
            {
                _detachedTerminalIds.Add(key);
                _recoveringWindowIds.Add(key);
            }
        }
    }

    private async Task OnDetachedWindowOpenedAsync((string Key, TerminalWindowOpenResult Result) launch)
    {
        if (_disposed)
        {
            return;
        }

        var (terminalId, result) = launch;
        if (!_terminals.Any(t => t.TerminalId == terminalId))
        {
            // The watch stream can remove a terminal while its native launch notification is in flight.
            // Reconcile the captured key; never detach the newly active tab in its place.
            await CloseDetachedWindowAsync(terminalId);
            return;
        }

        _popupBlocked = result == TerminalWindowOpenResult.Blocked;
        if (result is TerminalWindowOpenResult.Opened or TerminalWindowOpenResult.Focused or TerminalWindowOpenResult.Adopted or TerminalWindowOpenResult.Recovering)
        {
            _detachedTerminalIds.Add(terminalId);
            _terminalViews.Remove(terminalId);
            if (result is TerminalWindowOpenResult.Recovering)
            {
                _recoveringWindowIds.Add(terminalId);
            }
            else
            {
                _recoveringWindowIds.Remove(terminalId);
            }
        }
    }

    private async Task ReturnToDockAsync(string terminalId)
    {
        try
        {
            if (_windowButton is { } button)
            {
                await button.CloseAsync(terminalId).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reattach regardless: leaving the pane on the placeholder because the close call failed would strand
            // the terminal with no viewer at all.
            Logger.LogWarning(ex, "Failed to close the window for terminal {TerminalId}.", terminalId);
        }

        _detachedTerminalIds.Remove(terminalId);
        _recoveringWindowIds.Remove(terminalId);
        // Explicit return is also the escape hatch when unavailable/corrupt storage prevented passive recovery.
        _windowTrackingReadyIds.Add(terminalId);
        StateHasChanged();
    }

    /// <summary>
    /// Reattaches a terminal whose window the user closed. Remounting <c>TerminalView</c> opens a fresh socket and
    /// the HMP1 state sync replays the screen, so nothing is lost by having had no viewer in between.
    /// </summary>
    private Task OnDetachedWindowClosedAsync(string terminalId) => InvokeAsync(() =>
    {
        if (!_disposed && _detachedTerminalIds.Remove(terminalId))
        {
            _recoveringWindowIds.Remove(terminalId);
            StateHasChanged();
        }
    });

    private async Task CloseTerminalAsync(string terminalId, string terminalTitle)
    {
        try
        {
            await DashboardClient.CloseTerminalAsync(terminalId, _cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (_cts.IsCancellationRequested && (ex is OperationCanceledException || ex is RpcException { StatusCode: StatusCode.Cancelled }))
        {
            Logger.LogDebug(ex, "Stopped waiting for dock terminal {TerminalId} to close because the dashboard disconnected.", terminalId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded && !_disposed)
        {
            Logger.LogWarning(ex, "Timed out waiting for dock terminal {TerminalId} to close.", terminalId);

            // Removal can arrive on the watch stream before disposal times out. Keep the clicked title rather
            // than looking it up in the remaining tabs, and retain the warning in the notification center.
            var title = Loc[nameof(Resources.TerminalStrings.TerminalDockCloseTimedOutTitle)].Value;
            var message = Loc[nameof(Resources.TerminalStrings.TerminalDockCloseTimedOutMessage), terminalTitle].Value;
            NotificationService.AddNotification(new NotificationEntry
            {
                Title = title,
                Body = message,
                Intent = FluentMessageIntent.Warning
            });
            await ToastService.ShowWarningToastAsync(message).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to close dock terminal {TerminalId}.", terminalId);
        }
    }

    private async Task WatchTerminalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in DashboardClient.SubscribeTerminalsAsync(cancellationToken).ConfigureAwait(false))
            {
                // The stream runs on a worker. Dispatch the entire update, not just the render: Razor and click
                // handlers enumerate these collections and must never race a snapshot or removal.
                await InvokeAsync(async () =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    List<string> endedTerminalIds = [];
                    if (update.KindCase == WatchTerminalsUpdate.KindOneofCase.Snapshot)
                    {
                        var previousActiveIndex = _terminals.FindIndex(t => t.TerminalId == _activeTerminalId);
                        _terminals.Clear();
                        _terminals.AddRange(update.Snapshot.Terminals);
                        if (!_terminals.Any(t => t.TerminalId == _activeTerminalId))
                        {
                            // Snapshots can also remove the active tab; use the same adjacent fallback as removal.
                            _activeTerminalId = _terminals.Count > 0
                                ? _terminals[Math.Clamp(previousActiveIndex, 0, _terminals.Count - 1)].TerminalId
                                : null;
                        }

                        if (!string.IsNullOrEmpty(update.Snapshot.ActivatedTerminalId))
                        {
                            // An overflow snapshot retains the latest Show() request even if its terminal has
                            // since been removed. Reveal the dock, but never resurrect a removed terminal's tab.
                            Show(TerminalDockTrigger.AppHost);
                            if (_terminals.Any(t => t.TerminalId == update.Snapshot.ActivatedTerminalId))
                            {
                                _activeTerminalId = update.Snapshot.ActivatedTerminalId;
                            }
                        }

                        // Recovery snapshots replace all prior state, including terminals removed while offline.
                        endedTerminalIds.AddRange(_detachedTerminalIds.Where(id => !_terminals.Any(t => t.TerminalId == id)));
                        _detachedTerminalIds.ExceptWith(endedTerminalIds);
                        _recoveringWindowIds.IntersectWith(_detachedTerminalIds);
                        foreach (var id in _terminalViews.Keys.Where(id => !_terminals.Any(t => t.TerminalId == id)).ToArray())
                        {
                            _terminalViews.Remove(id);
                        }
                    }
                    else if (update.KindCase == WatchTerminalsUpdate.KindOneofCase.Change &&
                             Apply(update.Change.ChangeType, update.Change.Terminal) is { } endedTerminalId)
                    {
                        endedTerminalIds.Add(endedTerminalId);
                    }

                    if (_hasBeenOpened)
                    {
                        StateHasChanged();
                    }
                    foreach (var terminalId in endedTerminalIds)
                    {
                        if (_disposed)
                        {
                            return;
                        }
                        await CloseDetachedWindowAsync(terminalId).ConfigureAwait(true);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The component is going away or the circuit disconnected.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Terminal dock watch stream ended unexpectedly.");
        }
    }

    private async Task WatchResourceTerminalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (snapshot, subscription) = await ResourceRepository.SubscribeResourcesAsync(cancellationToken).ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var resource in snapshot)
                {
                    _resourceByName[resource.Name] = resource;
                }
                UpdateResourceTerminalLinks();
            }).ConfigureAwait(false);

            await foreach (var changes in subscription.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                // Resource notifications arrive off the renderer thread, just like terminal notifications.
                await InvokeAsync(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    foreach (var (changeType, resource) in changes)
                    {
                        if (changeType == ResourceViewModelChangeType.Upsert)
                        {
                            _resourceByName[resource.Name] = resource;
                        }
                        else if (changeType == ResourceViewModelChangeType.Delete)
                        {
                            _resourceByName.Remove(resource.Name);
                        }
                    }
                    UpdateResourceTerminalLinks();
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The component is going away or the circuit disconnected.
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Terminal dock resource watch stream ended unexpectedly.");
        }
    }

    private void UpdateResourceTerminalLinks()
    {
        var links = _resourceByName.Values
            .Where(resource => !resource.IsResourceHidden(showHiddenResources: false) &&
                resource.HasTerminal() && resource.TryGetTerminalReplicaInfo(out _, out _))
            .OrderBy(resource => resource, ResourceViewModelNameComparer.Instance)
            .Select(resource =>
            {
                // Use the same resource identity as the console/terminal page so replicas remain distinct.
                var name = ResourceViewModel.GetResourceName(resource, _resourceByName);
                var url = NavigationManager.ToAbsoluteUri(DashboardUrls.ConsoleLogsUrl(name).TrimStart('/')).AbsoluteUri;
                return new ResourceTerminalLink(name, url);
            })
            .ToArray();

        // Health and other property updates must not rerender terminal viewers when the links are unchanged.
        if (!_resourceTerminalLinks.SequenceEqual(links))
        {
            _resourceTerminalLinks = links;
            if (_hasBeenOpened && IsPanelVisible)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Applies a change from the watch stream. Returns the id of a terminal whose detached window should be closed
    /// because the terminal itself has ended, or <see langword="null"/> when there is nothing to close.
    /// </summary>
    private string? Apply(TerminalChangeType changeType, TerminalDescriptor descriptor)
    {
        var index = _terminals.FindIndex(t => t.TerminalId == descriptor.TerminalId);

        switch (changeType)
        {
            case TerminalChangeType.Added or TerminalChangeType.Retitled:
                if (index >= 0)
                {
                    _terminals[index] = descriptor;
                }
                else
                {
                    _terminals.Add(descriptor);
                }
                _activeTerminalId ??= descriptor.TerminalId;
                break;

            case TerminalChangeType.Removed:
                _terminalViews.Remove(descriptor.TerminalId);
                _recoveringWindowIds.Remove(descriptor.TerminalId);
                if (index >= 0)
                {
                    _terminals.RemoveAt(index);
                }
                if (_activeTerminalId == descriptor.TerminalId)
                {
                    // Fall back to the neighbour that took the closed tab's place, matching editor tab behaviour.
                    var fallback = Math.Min(index, _terminals.Count - 1);
                    _activeTerminalId = fallback >= 0 ? _terminals[fallback].TerminalId : null;
                }
                return _detachedTerminalIds.Remove(descriptor.TerminalId) ? descriptor.TerminalId : null;

            case TerminalChangeType.Activated:
                // Raised by AspireTerminal.Show() in the AppHost, so AppHost code can reveal its own terminal.
                if (index < 0)
                {
                    _terminals.Add(descriptor);
                }
                _activeTerminalId = descriptor.TerminalId;
                Show(TerminalDockTrigger.AppHost);
                break;
        }

        return null;
    }

    private async Task CloseDetachedWindowAsync(string terminalId)
    {
        try
        {
            if (_windowButton is { } button)
            {
                await button.CloseAsync(terminalId).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to close the window for ended terminal {TerminalId}.", terminalId);
        }
    }

    private static string BuildEndpoint(string terminalId)
        => $"api/apphost-terminal?terminalId={Uri.EscapeDataString(terminalId)}";

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        TelemetryContext?.Dispose();
        TelemetryContext = null;
        ShortcutManager.RemoveGlobalKeydownListener(this);

        // Stop updates before releasing browser-side state. A queued dispatcher callback observes _disposed and
        // does nothing, and cancellation interrupts either the active RPC or its recovery wait.
        await _cts.CancelAsync().ConfigureAwait(true);
        try
        {
            await Task.WhenAll(_watchTask ?? Task.CompletedTask, _resourceWatchTask ?? Task.CompletedTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping the watches.
        }

        if (_jsInitializationTask is { } initialization)
        {
            try
            {
                await initialization.ConfigureAwait(true);
            }
            catch (Exception)
            {
                // InitializeJsAsync already logged and propagated the failure. Release partial registration too.
            }
        }

        try
        {
            await DisposeJsAsync().ConfigureAwait(true);
        }
        finally
        {
            try
            {
                if (_windowButton is { } button)
                {
                    // Leaves independent windows and their AppHost-owned producers running.
                    await button.DisposeAsync().ConfigureAwait(true);
                }
            }
            finally
            {
                _cts.Dispose();
            }
        }
    }

    private async Task DisposeJsAsync()
    {
        try
        {
            if (_jsModule is { } module)
            {
                try
                {
                    try
                    {
                        if (_resizeHandleRegistrationStarted)
                        {
                            await module.InvokeVoidAsync("unregisterResizeHandle", _dockElement).ConfigureAwait(true);
                        }
                    }
                    finally
                    {
                        if (_tabNavigationRegistrationStarted)
                        {
                            await module.InvokeVoidAsync("unregisterTabNavigation", _dockElement).ConfigureAwait(true);
                        }
                    }
                }
                catch (JSDisconnectedException)
                {
                    // There is no browser-side state left to unregister after the circuit disconnects.
                }
                finally
                {
                    await JSInteropHelpers.SafeDisposeAsync(module).ConfigureAwait(true);
                    _jsModule = null;
                }
            }
        }
        finally
        {
            _selfRef?.Dispose();
            _selfRef = null;
        }
    }

    private enum TerminalDockTrigger
    {
        User,
        AppHost
    }

    private sealed record ResourceTerminalLink(string Name, string Url);
}
