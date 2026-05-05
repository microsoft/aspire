// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Renders an interactive terminal using xterm.js, connected to the resource's
/// per-replica terminal session via a WebSocket bridge to the AppHost-owned
/// terminal host (HMP v1 over Unix domain socket).
/// </summary>
public sealed partial class TerminalView : ComponentBase, IAsyncDisposable
{
    private ElementReference _terminalElement;
    private IJSObjectReference? _jsModule;
    private int _terminalId;
    private string? _connectedResourceName;
    private int _connectedReplicaIndex = -1;

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

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (string.IsNullOrEmpty(ResourceName))
        {
            return;
        }

        if (firstRender)
        {
            await InitializeTerminalAsync();
            _connectedResourceName = ResourceName;
            _connectedReplicaIndex = ReplicaIndex;
            return;
        }

        // The same TerminalView instance is reused across resource/replica
        // switches in the parent (e.g. ConsoleLogs page selects a different
        // terminal-enabled resource). Detect that here and rebind the
        // underlying WebSocket; xterm.js is preserved and just gets cleared
        // and refilled by the new connection's StateSync replay.
        if (!string.Equals(ResourceName, _connectedResourceName, StringComparison.Ordinal) ||
            ReplicaIndex != _connectedReplicaIndex)
        {
            var newResource = ResourceName;
            var newReplica = ReplicaIndex;
            await ReconnectAsync(newResource, newReplica);
            _connectedResourceName = newResource;
            _connectedReplicaIndex = newReplica;
        }
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                "import", "/Components/Controls/TerminalView.razor.js");

            _terminalId = await _jsModule.InvokeAsync<int>(
                "initTerminal", _terminalElement, BuildWebSocketUrl(ResourceName!, ReplicaIndex));
        }
        catch (JSDisconnectedException)
        {
            // Component disposed during initialization
        }
    }

    /// <summary>
    /// Reconnects the terminal to a different resource/replica. When both
    /// arguments match the current values this is a no-op.
    /// </summary>
    public async Task ReconnectAsync(string? newResourceName, int newReplicaIndex)
    {
        if (_jsModule is null || _terminalId == 0)
        {
            ResourceName = newResourceName;
            ReplicaIndex = newReplicaIndex;
            if (!string.IsNullOrEmpty(newResourceName))
            {
                await InitializeTerminalAsync();
            }
            return;
        }

        if (string.IsNullOrEmpty(newResourceName))
        {
            await _jsModule.InvokeVoidAsync("disposeTerminal", _terminalId);
            _terminalId = 0;
            return;
        }

        ResourceName = newResourceName;
        ReplicaIndex = newReplicaIndex;
        await _jsModule.InvokeVoidAsync("reconnectTerminal", _terminalId, BuildWebSocketUrl(newResourceName, newReplicaIndex));
    }

    private string BuildWebSocketUrl(string resource, int replica)
    {
        var baseUri = new Uri(NavigationManager.BaseUri);
        var wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
        return $"{wsScheme}://{baseUri.Authority}/api/terminal?resource={Uri.EscapeDataString(resource)}&replica={replica}";
    }

    public async ValueTask DisposeAsync()
    {
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
        }
    }
}
