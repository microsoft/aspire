// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    Blocked
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
    private const int DefaultWindowWidthPx = 960;
    private const int DefaultWindowHeightPx = 600;

    private readonly IJSRuntime _js;
    private readonly Func<string, Task> _onWindowClosed;
    private readonly HashSet<string> _tracked = [];

    private DotNetObjectReference<TerminalWindowLauncher>? _selfRef;
    private IJSObjectReference? _module;

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalWindowLauncher"/> class.
    /// </summary>
    /// <param name="js">The JS runtime for the owning component's circuit.</param>
    /// <param name="onWindowClosed">
    /// Invoked with the terminal key when the user closes a detached window. Not raised for windows closed through
    /// <see cref="CloseAsync"/>, because the caller already knows about those.
    /// </param>
    public TerminalWindowLauncher(IJSRuntime js, Func<string, Task> onWindowClosed)
    {
        ArgumentNullException.ThrowIfNull(js);
        ArgumentNullException.ThrowIfNull(onWindowClosed);

        _js = js;
        _onWindowClosed = onWindowClosed;
    }

    /// <summary>
    /// Opens <paramref name="url"/> in a window dedicated to the terminal identified by <paramref name="key"/>, or
    /// focuses the existing window if one is already open for it.
    /// </summary>
    /// <param name="key">
    /// An opaque, page-stable identifier for the terminal — a dock terminal id, or a resource name and replica index.
    /// </param>
    /// <param name="url">The dashboard URL that renders the detached terminal.</param>
    /// <param name="widthPx">Requested window width, in pixels.</param>
    /// <param name="heightPx">Requested window height, in pixels.</param>
    public async Task<TerminalWindowOpenResult> OpenAsync(
        string key,
        string url,
        int widthPx = DefaultWindowWidthPx,
        int heightPx = DefaultWindowHeightPx)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);

        var result = await module.InvokeAsync<string>(
            "openTerminalWindow", key, url, widthPx, heightPx, _selfRef).ConfigureAwait(false);

        if (result is not "blocked")
        {
            _tracked.Add(key);
        }

        return result switch
        {
            "opened" => TerminalWindowOpenResult.Opened,
            "focused" => TerminalWindowOpenResult.Focused,
            _ => TerminalWindowOpenResult.Blocked
        };
    }

    /// <summary>
    /// Brings the window for <paramref name="key"/> to the front. Returns <see langword="false"/> if no window is open
    /// for it, which the caller can treat as a cue to reattach.
    /// </summary>
    public async Task<bool> FocusAsync(string key)
    {
        var module = await GetModuleAsync().ConfigureAwait(false);
        return await module.InvokeAsync<bool>("focusTerminalWindow", key).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the window for <paramref name="key"/>. The close callback is deliberately not raised.
    /// </summary>
    public async Task CloseAsync(string key)
    {
        _tracked.Remove(key);

        var module = await GetModuleAsync().ConfigureAwait(false);
        await module.InvokeVoidAsync("closeTerminalWindow", key).ConfigureAwait(false);
    }

    /// <summary>
    /// Called from JS when a detached window is observed to have closed.
    /// </summary>
    [JSInvokable]
    public Task OnTerminalWindowClosedAsync(string key)
    {
        _tracked.Remove(key);
        return _onWindowClosed(key);
    }

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        // Imported lazily: most sessions never detach a terminal, and the import is only legal once the circuit can
        // reach the browser, which rules out doing it in a constructor.
        _selfRef ??= DotNetObjectReference.Create(this);
        return _module ??= await _js.InvokeAsync<IJSObjectReference>(
            "import", "/js/app-terminalwindow.js").ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is { } module)
        {
            try
            {
                // Stop watching, but leave the windows open. They are independent viewers of an AppHost-owned
                // terminal, so closing them because the opener navigated away would throw away live work.
                foreach (var key in _tracked)
                {
                    await module.InvokeVoidAsync("untrackTerminalWindow", key).ConfigureAwait(false);
                }

                await module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The circuit is already gone, so there is nothing left to untrack.
            }
        }

        _tracked.Clear();
        _selfRef?.Dispose();
    }
}
