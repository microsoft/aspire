// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Renders a GPU terminal connected to the resource's per-replica session
/// through the dashboard's HWT1 presentation endpoint.
/// </summary>
public sealed partial class TerminalView : ComponentBase, IAsyncDisposable
{
    private ElementReference _terminalElement;
    private IJSObjectReference? _jsModule;
    private DotNetObjectReference<TerminalView>? _selfRef;
    private int _terminalId;
    private string? _connectedResourceName;
    private int _connectedReplicaIndex = -1;
    // Highest reconnect generation we've observed from JS via a toolbar
    // snapshot. The JS side bumps `state.generation` on every
    // initTerminal / reconnectTerminal / auto-reconnect. `reconnectTerminal`
    // keeps the same terminal id, so terminal id alone can't tell us whether
    // a late-arriving `onExit` or `OnTerminalStateChanged` callback belongs
    // to the currently bound connection or to a superseded one. Any callback
    // whose generation is below _connectedGeneration is stale and dropped.
    private int _connectedGeneration = -1;
    // Guards against concurrent or re-entrant initialization. OnAfterRenderAsync
    // can fire again while the first InitializeTerminalAsync await is still in
    // flight (Blazor does not serialize OnAfterRenderAsync calls when re-renders
    // happen during awaits). Without this latch, the non-firstRender branch
    // below would see _connectedResourceName == null, mistake that for "rebind
    // needed", call ReconnectAsync, and — because _terminalId is also still 0
    // — fall through to InitializeTerminalAsync a second time. Each
    // initTerminal call appends a brand-new terminal host element to the same
    // Blazor container, leaving multiple stacked terminals in the DOM that
    // mirror the same input/output stream. This pattern is easy to trigger
    // on a resource stop+restart where the dashboard fires a burst of
    // resource-snapshot-driven re-renders right after the page mounts.
    private bool _initStarted;
    private bool _initializationFailed;
    private bool _disposed;
    private string? _terminalError;
    private int _terminalColumns;
    private int _terminalRows;
    private Task? _initializationTask;

    /// <summary>
    /// Gets or sets the user-facing display name of the resource that owns the
    /// terminal session (e.g. <c>myapp</c>, not the per-replica DCP suffix).
    /// </summary>
    [Parameter]
    public string? ResourceName { get; set; }

    /// <summary>
    /// Gets or sets the stable 0-based replica index for the terminal session.
    /// Defaults to <c>0</c> for single-replica resources.
    /// </summary>
    [Parameter]
    public int ReplicaIndex { get; set; }

    /// <summary>
    /// Raised when the JS side pushes a fresh toolbar state snapshot (role,
    /// dims, font size, etc.). The host page subscribes to render status,
    /// "Take control", font controls and size options in its toolbar.
    /// </summary>
    [Parameter]
    public EventCallback<TerminalToolbarState> OnToolbarStateChanged { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.ConsoleLogs> Loc { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_disposed || _initializationFailed || string.IsNullOrEmpty(ResourceName))
        {
            return;
        }

        if (firstRender)
        {
            _initStarted = true;
            // Snapshot the resource/replica values BEFORE the JS init await:
            // parameter push from the parent can change ResourceName/
            // ReplicaIndex while initTerminal is in flight. Recording the
            // *post-await* field values would falsely mark the terminal as
            // connected to the new resource, so the rebind branch below
            // would never fire and the JS terminal would keep streaming
            // the previous resource.
            var initResource = ResourceName;
            var initReplica = ReplicaIndex;
            await InitializeTerminalAsync(initResource!, initReplica);
            if (_disposed || _terminalId == 0)
            {
                return;
            }
            _connectedResourceName = initResource;
            _connectedReplicaIndex = initReplica;

            if (!string.Equals(ResourceName, _connectedResourceName, StringComparison.Ordinal) ||
                ReplicaIndex != _connectedReplicaIndex)
            {
                var newResource = ResourceName;
                var newReplica = ReplicaIndex;
                try
                {
                    await ReconnectAsync(newResource, newReplica);
                }
                catch (JSDisconnectedException)
                {
                    return;
                }
                catch (Exception)
                {
                    _terminalError = "mount-failed";
                    _initializationFailed = true;
                    StateHasChanged();
                    return;
                }

                _connectedResourceName = newResource;
                _connectedReplicaIndex = newReplica;
            }
            return;
        }

        // If a re-render fires while the very first initTerminal call is still
        // in flight, do nothing here. Once that call completes the firstRender
        // path will set _connectedResourceName / _connectedReplicaIndex and
        // any future rebind needed will be caught on the next render after
        // that. Without this guard the rebind branch below would re-enter
        // initialization and stack a second terminal onto the same container —
        // see the comment on _initStarted.
        if (_initStarted && _terminalId == 0)
        {
            return;
        }

        // The same TerminalView instance is reused across resource/replica
        // switches in the parent (e.g. ConsoleLogs page selects a different
        // terminal-enabled resource). Detect that here and rebind the
        // underlying WebSocket and mounted client. The client owns a single
        // connection; reconnect must create a fresh view and worker.
        //
        // ALL exceptions are swallowed at this layer because OnAfterRenderAsync
        // is a Blazor lifecycle method: an unhandled exception here can fail
        // the SignalR circuit and tear down the entire dashboard tab. Failing
        // to switch terminals is a localized, recoverable issue (the JS side
        // will keep retrying or the user can reload); a circuit failure is not.
        if (!string.Equals(ResourceName, _connectedResourceName, StringComparison.Ordinal) ||
            ReplicaIndex != _connectedReplicaIndex)
        {
            var newResource = ResourceName;
            var newReplica = ReplicaIndex;
            try
            {
                await ReconnectAsync(newResource, newReplica);
            }
            catch (JSDisconnectedException)
            {
                // Component is being disposed; don't bother updating tracked state.
                return;
            }
            catch (Exception)
            {
                // Keep the failure local to this view instead of tearing down
                // the SignalR circuit, but do not hide it from the user.
                _terminalError = "mount-failed";
                _initializationFailed = true;
                StateHasChanged();
                return;
            }
            _connectedResourceName = newResource;
            _connectedReplicaIndex = newReplica;
        }
    }

    private Task InitializeTerminalAsync(string resourceName, int replicaIndex)
    {
        if (_initializationTask is { IsCompleted: false })
        {
            return _initializationTask;
        }

        _initStarted = true;
        return _initializationTask = InitializeTerminalCoreAsync(resourceName, replicaIndex);
    }

    private async Task InitializeTerminalCoreAsync(string resourceName, int replicaIndex)
    {
        try
        {
            var moduleUri = new Uri(new Uri(NavigationManager.BaseUri), "Components/Controls/TerminalView.razor.js");
            _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", moduleUri.PathAndQuery);

            if (_disposed)
            {
                await JSInteropHelpers.SafeDisposeAsync(_jsModule);
                _jsModule = null;
                return;
            }

            _selfRef ??= DotNetObjectReference.Create(this);

            _connectedGeneration = -1;
            _terminalId = await _jsModule.InvokeAsync<int>(
                "initTerminal", _terminalElement, BuildWebSocketUrl(resourceName, replicaIndex), _selfRef,
                Loc[nameof(Resources.ConsoleLogs.TerminalInputLabel)].Value);
        }
        catch (JSDisconnectedException)
        {
            // Component disposed during initialization. Clear _initStarted so
            // that if a later render *does* fire (e.g. reconnection scenarios
            // that re-mount the JS module), OnAfterRenderAsync's
            // `_initStarted && _terminalId == 0` short-circuit doesn't
            // permanently wedge us with no terminal.
            _initStarted = false;
        }
        catch (Exception)
        {
            // Defensive: any other JS-side error (e.g. JSException while
            // importing the module or during initTerminal) must not bubble
            // out of a Blazor lifecycle method — that can tear down the
            // SignalR circuit and take the whole dashboard tab with it.
            // Offer an explicit retry rather than re-entering initialization
            // on every render while the module remains unavailable.
            _initStarted = false;
            _initializationFailed = true;
            _terminalError = "mount-failed";
            if (!_disposed)
            {
                StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Reconnects the terminal to a resource/replica. Matching arguments still
    /// create a fresh connection so a failed connection can be retried.
    /// </summary>
    public async Task ReconnectAsync(string? newResourceName, int newReplicaIndex)
    {
        if (_jsModule is null || _terminalId == 0)
        {
            ResourceName = newResourceName;
            ReplicaIndex = newReplicaIndex;
            if (!string.IsNullOrEmpty(newResourceName))
            {
                await InitializeTerminalAsync(newResourceName, newReplicaIndex);
                if (_terminalId != 0)
                {
                    _connectedResourceName = newResourceName;
                    _connectedReplicaIndex = newReplicaIndex;
                }
            }
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(newResourceName))
            {
                await _jsModule.InvokeVoidAsync("disposeTerminal", _terminalId);
                _terminalId = 0;
                _connectedGeneration = -1;
                return;
            }

            ResourceName = newResourceName;
            ReplicaIndex = newReplicaIndex;
            var generation = await _jsModule.InvokeAsync<int>(
                "reconnectTerminal",
                _terminalId,
                BuildWebSocketUrl(newResourceName, newReplicaIndex));
            if (generation > 0)
            {
                _connectedGeneration = generation;
                _connectedResourceName = newResourceName;
                _connectedReplicaIndex = newReplicaIndex;
            }
        }
        catch (JSDisconnectedException)
        {
            // Component disposed mid-call; nothing to do.
        }
    }

    /// <summary>
    /// Invoked by the JS terminal whenever its role/size/font state changes.
    /// Forwards the snapshot to the host page via <see cref="OnToolbarStateChanged"/>.
    /// JS remains the source of truth for terminal state — the toolbar
    /// renders whatever the most recent snapshot says.
    /// </summary>
    [JSInvokable]
    public async Task OnTerminalStateChanged(TerminalToolbarState state)
    {
        if (IsStaleTerminalCallback(state.TerminalId, state.Generation))
        {
            return;
        }

        if (_terminalError != state.Error || _terminalColumns != state.Cols || _terminalRows != state.Rows)
        {
            _terminalError = state.Error;
            _terminalColumns = state.Cols;
            _terminalRows = state.Rows;
            StateHasChanged();
        }
        await OnToolbarStateChanged.InvokeAsync(state);
    }

    private bool IsStaleTerminalCallback(int terminalId, int generation)
    {
        // Drop stale callbacks that arrive after this view was rebound. The
        // terminal id changes when initTerminal allocates a new terminal host;
        // explicit reconnect keeps the id but bumps the JS-side generation.
        if (_disposed || (_terminalId != 0 && terminalId != _terminalId))
        {
            return true;
        }

        if (generation < _connectedGeneration)
        {
            return true;
        }

        if (generation > _connectedGeneration)
        {
            _connectedGeneration = generation;
        }

        return false;
    }

    /// <summary>
    /// Sets the terminal font size and switches sizing back to "Auto" (font-
    /// driven) mode. Out-of-range values are clamped by the JS side.
    /// </summary>
    public async Task SetFontSizeAsync(int fontPx)
    {
        if (_jsModule is null || _terminalId == 0)
        {
            return;
        }
        try
        {
            await _jsModule.InvokeVoidAsync("setFontSizeFromHost", _terminalId, fontPx);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>
    /// Sets the sizing mode by preset key (<c>auto</c> or one of the
    /// <see cref="TerminalToolbarState.SizeKey"/> values). Unknown keys are
    /// ignored by the JS side.
    /// </summary>
    public async Task SetSizeModeAsync(string sizeKey)
    {
        if (_jsModule is null || _terminalId == 0)
        {
            return;
        }
        try
        {
            await _jsModule.InvokeVoidAsync("setSizeModeFromHost", _terminalId, sizeKey);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>
    /// Fetches the set of size presets exposed by the JS terminal so the
    /// host page's size dropdown stays in sync with the values JS knows
    /// how to handle.
    /// </summary>
    public async Task<IReadOnlyList<TerminalSizePreset>> GetSizePresetsAsync()
    {
        if (_jsModule is null)
        {
            return Array.Empty<TerminalSizePreset>();
        }
        try
        {
            return await _jsModule.InvokeAsync<TerminalSizePreset[]>("getSizePresets");
        }
        catch (JSDisconnectedException)
        {
            return Array.Empty<TerminalSizePreset>();
        }
    }

    /// <summary>
    /// Asks the JS terminal to re-push its current toolbar snapshot,
    /// bypassing the change-detection cache. Called by the host page when
    /// it has lost its cached snapshot (e.g. across a layout transition)
    /// but the JS terminal is still live and the cached "last pushed JSON"
    /// would otherwise suppress a fresh push.
    /// </summary>
    public async Task RefreshToolbarStateAsync()
    {
        if (_jsModule is null || _terminalId == 0)
        {
            return;
        }
        try
        {
            await _jsModule.InvokeVoidAsync("refreshToolbarState", _terminalId);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <summary>
    /// Notifies the JS terminal when it becomes visible, starting a deferred
    /// mount or refreshing selection overlays without remounting the client.
    /// The Hex1b client observes container geometry and owns terminal fitting.
    /// </summary>
    public async Task RefreshLayoutAsync()
    {
        if (_jsModule is null || _terminalId == 0)
        {
            return;
        }
        try
        {
            await _jsModule.InvokeVoidAsync("refreshLayout", _terminalId);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private string BuildWebSocketUrl(string resource, int replica)
    {
        var baseUri = new Uri(NavigationManager.BaseUri);
        var endpoint = new Uri(baseUri, "api/terminal");
        var wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
        return $"{wsScheme}://{endpoint.Authority}{endpoint.AbsolutePath}?resource={Uri.EscapeDataString(resource)}&replica={replica}";
    }

    private string GetErrorMessage() => Loc[_terminalError switch
    {
        "disconnected" => nameof(Resources.ConsoleLogs.TerminalDisconnected),
        "input-failed" => nameof(Resources.ConsoleLogs.TerminalInputFailed),
        "sizing-failed" => nameof(Resources.ConsoleLogs.TerminalSizingFailed),
        _ => nameof(Resources.ConsoleLogs.TerminalMountFailed)
    }];

    private async Task RetryAsync()
    {
        _initializationFailed = false;
        _terminalError = null;
        try
        {
            await ReconnectAsync(ResourceName, ReplicaIndex);
        }
        catch (JSException)
        {
            _terminalError = "mount-failed";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        // JS init returns its id without waiting for the first frame, but the
        // interop round trip can still overlap component disposal. Wait for
        // that id before disposing the module so its worker cannot be orphaned.
        if (_initializationTask is not null)
        {
            await _initializationTask;
        }
        if (_jsModule is not null && _terminalId != 0)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("disposeTerminal", _terminalId);
            }
            catch (JSDisconnectedException)
            {
                // Expected during shutdown
            }
        }
        if (_jsModule is not null)
        {
            await JSInteropHelpers.SafeDisposeAsync(_jsModule);
            _jsModule = null;
        }
        _terminalId = 0;
        _selfRef?.Dispose();
        _selfRef = null;
    }
}

/// <summary>
/// Snapshot of the JS terminal's current role, sizing, and dims, pushed up
/// to the host page so the toolbar can render the right state.
/// </summary>
public sealed record TerminalToolbarState
{
    /// <summary>Unique JS-side terminal id (allocated by <c>initTerminal</c>).</summary>
    public int TerminalId { get; init; }

    /// <summary>Reconnect generation; bumped on every (re)connect.</summary>
    public int Generation { get; init; }

    /// <summary>
    /// One of <c>connecting</c>, <c>primary</c>, <c>viewer</c>, <c>no-primary</c>.
    /// </summary>
    public string Status { get; init; } = "connecting";

    /// <summary>True after the mounted client has presented its first connected frame.</summary>
    public bool Connected { get; init; }

    /// <summary>True when this client owns resize authority; all peers can send input.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>True when "Take control" is meaningful to surface.</summary>
    public bool CanTakeControl { get; init; }

    /// <summary>Current sizing mode (<c>font</c> or <c>fixed</c>).</summary>
    public string SizeMode { get; init; } = "font";

    /// <summary>
    /// Dropdown key — <c>auto</c> for font-driven sizing or <c>{cols}x{rows}</c>
    /// for a preset.
    /// </summary>
    public string SizeKey { get; init; } = "auto";

    /// <summary>Current terminal font size in CSS pixels.</summary>
    public int FontPx { get; init; }

    /// <summary>Whether font ± buttons should be enabled (Auto mode + primary).</summary>
    public bool FontControlsEnabled { get; init; }

    /// <summary>Whether the size dropdown should be enabled (primary).</summary>
    public bool SizeSelectEnabled { get; init; }

    /// <summary>Current server-authoritative grid width.</summary>
    public int Cols { get; init; }

    /// <summary>Current server-authoritative grid height.</summary>
    public int Rows { get; init; }

    /// <summary>Localized error category, or <see langword="null"/> when healthy.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// A named size preset surfaced by the JS terminal (used to populate the
/// host page's size dropdown).
/// </summary>
public sealed record TerminalSizePreset(string Value, string Label, int Cols, int Rows);
