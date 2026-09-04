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
                    Apply(update.Change.ChangeType, update.Change.Terminal);
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

    private void Apply(TerminalChangeType changeType, TerminalDescriptor descriptor)
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
                break;

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
