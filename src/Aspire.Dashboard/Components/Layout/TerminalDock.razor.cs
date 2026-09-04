// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Layout;

/// <summary>
/// A collapsible, tabbed dock of terminals owned by the AppHost process, toggled with <c>Ctrl+`</c>.
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

    private readonly List<TerminalDescriptor> _terminals = [];
    private readonly CancellationTokenSource _cts = new();

    private bool _hasBeenOpened;
    private bool _isVisible;
    private string? _activeTerminalId;
    private int _heightPx = DefaultHeightPx;
    private Task? _watchTask;
    private readonly TaskCompletionSource _firstUpdateReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    private TerminalWindowLauncher? _windowLauncher;
    private bool _popupBlocked;

    [Inject]
    public required IDashboardClient DashboardClient { get; init; }

    [Inject]
    public required ShortcutManager ShortcutManager { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required ILogger<TerminalDock> Logger { get; init; }

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
        ShortcutManager.AddGlobalKeydownListener(this);

        // Watched eagerly rather than on first open: an `activated` notification is how AppHost code reveals a
        // terminal it created (IAspireTerminal.Show()), and that has to work in a browser that has never opened the
        // dock. One idle server stream per circuit is the price of that.
        _watchTask = Task.Run(() => WatchTerminalsAsync(_cts.Token), _cts.Token);
    }

    public Task OnPageKeyDownAsync(AspireKeyboardShortcut shortcut)
        => shortcut == AspireKeyboardShortcut.ToggleTerminalDock ? ToggleAsync() : Task.CompletedTask;

    /// <summary>
    /// Shows the dock, or hides it if it is already showing.
    /// </summary>
    /// <remarks>
    /// Public so the header button can drive the dock. The keyboard chord alone is not enough: whether
    /// <c>Ctrl+`</c> reaches the page depends on the browser, the OS window manager, and any extensions the user has
    /// installed, so the dock needs an affordance that cannot be intercepted.
    /// </remarks>
    public async Task ToggleAsync()
    {
        if (_isVisible)
        {
            Hide();
            return;
        }

        try
        {
            await ShowAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // The dock is already on screen; it will populate if and when the watch stream recovers.
            Logger.LogDebug(ex, "Timed out waiting for the initial terminal list.");
        }
    }

    private async Task ShowAsync()
    {
        _hasBeenOpened = true;
        _isVisible = true;
        StateHasChanged();

        // Wait for the first update before deciding whether the dock is empty. Terminals live in the AppHost, so a
        // dock opened for the first time in a second browser (or after a reload) already has tabs, and creating one
        // off a not-yet-populated list would spawn a redundant terminal.
        await _firstUpdateReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(true);

        // First open with nothing running gets the built-in terminal, so the dock is never an empty shell.
        if (_terminals.Count == 0)
        {
            await CreateTerminalAsync().ConfigureAwait(true);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Wiring happens on the render that first materialises the dock element, which is not the component's first
        // render — the markup is suppressed until the dock has been opened at least once.
        if (_hasBeenOpened && _jsModule is null)
        {
            _selfRef = DotNetObjectReference.Create(this);
            _jsModule = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/TerminalDock.razor.js").ConfigureAwait(true);
            await _jsModule.InvokeVoidAsync("registerResizeHandle", _dockElement, _selfRef).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Called from JS while the user drags the dock's top edge.
    /// </summary>
    [JSInvokable]
    public Task SetHeightAsync(int heightPx)
    {
        _heightPx = Math.Clamp(heightPx, 120, 1200);
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void Hide()
    {
        _isVisible = false;
        StateHasChanged();
    }

    private void Activate(string terminalId)
    {
        _activeTerminalId = terminalId;
        StateHasChanged();
    }

    private TerminalWindowLauncher WindowLauncher
        => _windowLauncher ??= new TerminalWindowLauncher(JS, OnDetachedWindowClosedAsync);

    /// <summary>
    /// Pops the active terminal out into its own window.
    /// </summary>
    private async Task DetachActiveAsync()
    {
        if (_activeTerminalId is not { } terminalId)
        {
            return;
        }

        _popupBlocked = false;

        try
        {
            var url = NavigationManager.ToAbsoluteUri($"/terminal-window/apphost/{Uri.EscapeDataString(terminalId)}").ToString();
            var result = await WindowLauncher.OpenAsync(terminalId, url).ConfigureAwait(true);

            if (result is TerminalWindowOpenResult.Blocked)
            {
                // Surfaced in the tab strip rather than swallowed: to the user, detaching just did nothing.
                _popupBlocked = true;
            }
            else
            {
                _detachedTerminalIds.Add(terminalId);
            }

            StateHasChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to detach terminal {TerminalId} into a window.", terminalId);
        }
    }

    private async Task FocusDetachedWindowAsync(string terminalId)
    {
        try
        {
            // A window the browser closed without us noticing yet would otherwise leave the pane stuck on the
            // placeholder, so a failed focus reattaches instead.
            if (!await WindowLauncher.FocusAsync(terminalId).ConfigureAwait(true))
            {
                await OnDetachedWindowClosedAsync(terminalId).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to focus the window for terminal {TerminalId}.", terminalId);
        }
    }

    private async Task ReturnToDockAsync(string terminalId)
    {
        try
        {
            await WindowLauncher.CloseAsync(terminalId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reattach regardless: leaving the pane on the placeholder because the close call failed would strand
            // the terminal with no viewer at all.
            Logger.LogWarning(ex, "Failed to close the window for terminal {TerminalId}.", terminalId);
        }

        _detachedTerminalIds.Remove(terminalId);
        StateHasChanged();
    }

    /// <summary>
    /// Reattaches a terminal whose window the user closed. Remounting <c>TerminalView</c> opens a fresh socket and
    /// the HMP1 state sync replays the screen, so nothing is lost by having had no viewer in between.
    /// </summary>
    private Task OnDetachedWindowClosedAsync(string terminalId)
    {
        if (_detachedTerminalIds.Remove(terminalId))
        {
            return InvokeAsync(StateHasChanged);
        }

        return Task.CompletedTask;
    }

    private async Task CreateTerminalAsync()
    {
        try
        {
            var descriptor = await DashboardClient.CreateDockTerminalAsync(title: null, _cts.Token).ConfigureAwait(true);

            // Select eagerly rather than waiting for the watch stream so the new tab is focused immediately even if
            // the notification is still in flight. Apply/Activate are both idempotent by terminal id.
            Apply(TerminalChangeType.Added, descriptor);
            _activeTerminalId = descriptor.TerminalId;
            StateHasChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to create a dock terminal.");
        }
    }

    private async Task CloseTerminalAsync(string terminalId)
    {
        try
        {
            await DashboardClient.CloseTerminalAsync(terminalId, _cts.Token).ConfigureAwait(true);
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
                if (update.KindCase == WatchTerminalsUpdate.KindOneofCase.Snapshot)
                {
                    _terminals.Clear();
                    _terminals.AddRange(update.Snapshot.Terminals);
                    _activeTerminalId ??= _terminals.FirstOrDefault()?.TerminalId;
                }
                else if (update.KindCase == WatchTerminalsUpdate.KindOneofCase.Change)
                {
                    if (Apply(update.Change.ChangeType, update.Change.Terminal) is { } endedTerminalId)
                    {
                        // The terminal is gone, so its window is showing a dead grid. Close it here rather than
                        // leaving the user to notice and dismiss it.
                        await InvokeAsync(() => CloseDetachedWindowAsync(endedTerminalId)).ConfigureAwait(false);
                    }
                }

                _firstUpdateReceived.TrySetResult();
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
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
        finally
        {
            // Unblocks a concurrent ShowAsync so a broken stream degrades to an empty dock rather than a hang.
            _firstUpdateReceived.TrySetResult();
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
                // Raised by IAspireTerminal.Show() in the AppHost, so AppHost code can reveal its own terminal.
                if (index < 0)
                {
                    _terminals.Add(descriptor);
                }
                _activeTerminalId = descriptor.TerminalId;
                _hasBeenOpened = true;
                _isVisible = true;
                break;
        }

        return null;
    }

    private async Task CloseDetachedWindowAsync(string terminalId)
    {
        try
        {
            await WindowLauncher.CloseAsync(terminalId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Failed to close the window for ended terminal {TerminalId}.", terminalId);
        }
    }

    private static string BuildEndpoint(string terminalId)
        => $"/api/apphost-terminal?terminalId={Uri.EscapeDataString(terminalId)}";

    public async ValueTask DisposeAsync()
    {
        ShortcutManager.RemoveGlobalKeydownListener(this);

        if (_jsModule is { } module)
        {
            try
            {
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone; there is nothing left to clean up on the browser side.
            }
        }

        _selfRef?.Dispose();

        if (_windowLauncher is { } launcher)
        {
            // Leaves any detached windows open: they are viewers of AppHost-owned terminals and have no reason to
            // die because this circuit went away.
            await launcher.DisposeAsync().ConfigureAwait(false);
        }

        await _cts.CancelAsync().ConfigureAwait(false);

        if (_watchTask is { } watchTask)
        {
            try
            {
                await watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}
