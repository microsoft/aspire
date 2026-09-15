// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Model;

/// <summary>
/// The outcome of asking the browser to open a terminal in its own window.
/// </summary>
public enum TerminalWindowOpenResult
{
    /// <summary>
    /// A new window was opened.
    /// </summary>
    Opened,

    /// <summary>
    /// A window was already open for this terminal, so it was brought to the front instead.
    /// </summary>
    Focused,

    /// <summary>
    /// The browser blocked the popup. The caller is expected to tell the user, because from their point of view
    /// nothing happened.
    /// </summary>
    Blocked,

    /// <summary>
    /// The browser could not open or focus the window.
    /// </summary>
    Failed
}

/// <summary>
/// Opens terminals in their own browser windows on behalf of a component, and reports when the user closes one.
/// </summary>
/// <remarks>
/// <para>
/// Detaching a terminal does not move it. Terminals are multi-headed — several viewers can attach to one PTY — and
/// the popup reaches the dashboard on its own, so it outlives the page that opened it. This type only owns the
/// window handle, so a component can focus the window, close it, and learn when it went away.
/// </para>
/// <para>
/// Whether the in-page view keeps rendering while a window is open is the caller's policy, not this type's. The
/// terminal dock replaces the pane with a placeholder because a dock tab and its window are the same viewport in
/// two places; a resource terminal keeps rendering inline, because seeing it in both is the point.
/// </para>
/// </remarks>
public sealed class TerminalWindowLauncher : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly NavigationManager _navigationManager;
    private readonly Func<string, TerminalWindowOpenResult, Task> _onWindowOpened;
    private readonly Func<string, Task> _onWindowClosed;
    private readonly string _id = Guid.NewGuid().ToString("N");

    private DotNetObjectReference<TerminalWindowLauncher>? _selfRef;
    private IJSObjectReference? _module;
    private Task? _registrationTask;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalWindowLauncher"/> class.
    /// </summary>
    /// <param name="js">The JS runtime for the owning component's circuit.</param>
    /// <param name="navigationManager">The navigation manager providing the dashboard's base URI.</param>
    /// <param name="onWindowOpened">Invoked after a native click opens or focuses a window, with its captured key and outcome.</param>
    /// <param name="onWindowClosed">
    /// Invoked with the terminal key when the user closes a detached window. Not raised for windows closed through
    /// <see cref="CloseAsync"/>, because the caller already knows about those.
    /// </param>
    public TerminalWindowLauncher(
        IJSRuntime js,
        NavigationManager navigationManager,
        Func<string, TerminalWindowOpenResult, Task> onWindowOpened,
        Func<string, Task> onWindowClosed)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(navigationManager);
        ArgumentNullException.ThrowIfNull(onWindowOpened);
        ArgumentNullException.ThrowIfNull(onWindowClosed);

        _js = js;
        _navigationManager = navigationManager;
        _onWindowOpened = onWindowOpened;
        _onWindowClosed = onWindowClosed;
    }

    /// <summary>
    /// Registers a native click listener before the owning component enables its button.
    /// </summary>
    /// <param name="buttonId">The ID of the Fluent button carrying the current terminal key and complete URL.</param>
    /// <returns>A task that completes when the listener is registered.</returns>
    public Task RegisterAsync(string buttonId)
        => _registrationTask ??= RegisterCoreAsync(buttonId);

    private async Task RegisterCoreAsync(string buttonId)
    {
        _selfRef = DotNetObjectReference.Create(this);
        var moduleUri = new Uri(new Uri(_navigationManager.BaseUri), "js/app-terminalwindow.js");
        _module = await _js.InvokeAsync<IJSObjectReference>("import", moduleUri.PathAndQuery).ConfigureAwait(false);
        if (!_disposed)
        {
            await _module.InvokeVoidAsync("registerTerminalWindowButton", buttonId, _id, _selfRef).ConfigureAwait(false);
        }
    }

    /// <summary>Receives the captured terminal key and browser result after the synchronous native launch.</summary>
    /// <param name="key">The terminal key at the time of the click, not the current selection.</param>
    /// <param name="result">The browser's launch outcome.</param>
    /// <returns>A task that completes when the owning component has reconciled the outcome.</returns>
    [JSInvokable]
    public Task OnTerminalWindowOpenedAsync(string key, string result)
        => _disposed ? Task.CompletedTask : _onWindowOpened(key, result switch
        {
            "opened" => TerminalWindowOpenResult.Opened,
            "focused" => TerminalWindowOpenResult.Focused,
            "blocked" => TerminalWindowOpenResult.Blocked,
            _ => TerminalWindowOpenResult.Failed
        });

    /// <summary>
    /// Brings the window for <paramref name="key"/> to the front. Returns <see langword="false"/> if no window is open
    /// for it, which the caller can treat as a cue to reattach.
    /// </summary>
    public async Task<bool> FocusAsync(string key)
    {
        return !_disposed && _module is { } module &&
            await module.InvokeAsync<bool>("focusTerminalWindow", key).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the window for <paramref name="key"/>. The close callback is deliberately not raised.
    /// </summary>
    public async Task CloseAsync(string key)
    {
        if (!_disposed && _module is { } module)
        {
            await module.InvokeVoidAsync("closeTerminalWindow", key).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Called from JS when a detached window is observed to have closed.
    /// </summary>
    [JSInvokable]
    public Task OnTerminalWindowClosedAsync(string key)
        => _disposed ? Task.CompletedTask : _onWindowClosed(key);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_registrationTask is { } registration)
        {
            try
            {
                await registration.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The component reports registration failures. Still release a partially initialized module.
            }
        }

        if (_module is { } module)
        {
            try
            {
                // Stop watching, but leave the windows open. They are independent viewers of an AppHost-owned
                // terminal, so closing them because the opener navigated away would throw away live work.
                await module.InvokeVoidAsync("unregisterTerminalWindowButton", _id).ConfigureAwait(false);
                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, so there is nothing left to untrack.
            }
        }

        _selfRef?.Dispose();
    }
}
