// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Renders an interactive terminal using xterm.js, connected to the resource's
/// terminal session via WebSocket → Aspire Terminal Protocol over UDS.
/// </summary>
public sealed partial class TerminalView : ComponentBase, IAsyncDisposable
{
    private ElementReference _terminalElement;
    private IJSObjectReference? _jsModule;
    private IJSObjectReference? _terminalInstance;

    [Parameter]
    public string? SocketPath { get; set; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender && SocketPath is not null)
        {
            await InitializeTerminalAsync();
        }
    }

    private async Task InitializeTerminalAsync()
    {
        try
        {
            _jsModule = await JS.InvokeAsync<IJSObjectReference>(
                "import", "/Components/Controls/TerminalView.razor.js");

            // Build the WebSocket URL for the terminal proxy.
            // The Dashboard exposes a WebSocket endpoint at /api/terminal?socketPath=...
            var baseUri = new Uri(NavigationManager.BaseUri);
            var wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
            var wsUrl = $"{wsScheme}://{baseUri.Host}:{baseUri.Port}/api/terminal?socketPath={Uri.EscapeDataString(SocketPath!)}";

            _terminalInstance = await _jsModule.InvokeAsync<IJSObjectReference>(
                "initTerminal", _terminalElement, wsUrl);
        }
        catch (JSDisconnectedException)
        {
            // Component disposed during initialization
        }
    }

    /// <summary>
    /// Called when the socket path changes (e.g., resource selection changes).
    /// </summary>
    public async Task ReconnectAsync(string? newSocketPath)
    {
        if (_jsModule is null || _terminalInstance is null)
        {
            SocketPath = newSocketPath;
            if (newSocketPath is not null)
            {
                await InitializeTerminalAsync();
            }
            return;
        }

        if (newSocketPath is null)
        {
            await _jsModule.InvokeVoidAsync("disposeTerminal", _terminalInstance);
            _terminalInstance = null;
            return;
        }

        SocketPath = newSocketPath;
        var baseUri = new Uri(NavigationManager.BaseUri);
        var wsScheme = baseUri.Scheme == "https" ? "wss" : "ws";
        var wsUrl = $"{wsScheme}://{baseUri.Host}:{baseUri.Port}/api/terminal?socketPath={Uri.EscapeDataString(newSocketPath)}";

        await _jsModule.InvokeVoidAsync("reconnectTerminal", _terminalInstance, wsUrl);
    }

    public async ValueTask DisposeAsync()
    {
        if (_terminalInstance is not null)
        {
            await JSInteropHelpers.SafeDisposeAsync(_terminalInstance);
        }
        if (_jsModule is not null)
        {
            await JSInteropHelpers.SafeDisposeAsync(_jsModule);
        }
    }
}
