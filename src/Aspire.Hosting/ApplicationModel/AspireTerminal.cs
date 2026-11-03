// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// An Aspire-owned terminal handle for dashboard interaction and automation from AppHost code.
/// </summary>
/// <remarks>
/// <para>
/// Obtain a handle from <see cref="TerminalService.CreateTerminal(TerminalLaunchOptions)"/> or
/// <see cref="TerminalService.TryGetTerminal"/>. Handles cannot be constructed or extended by callers;
/// Aspire manages their registration and connection to the underlying terminal implementation.
/// </para>
/// <para>
/// What disposal means depends on <see cref="Owner"/>. For <see cref="TerminalOwner.AppHost"/> the AppHost
/// owns the workload, so disposing stops it and removes the terminal from the dashboard; whoever creates
/// such a terminal owns it and must dispose it, and showing one in an interaction does not transfer that
/// ownership, so the terminal survives the dialog it was displayed in. For
/// <see cref="TerminalOwner.Resource"/> the workload belongs to the resource, so disposing only releases
/// Aspire's handle and leaves the workload running.
/// </para>
/// <para>
/// When an AppHost-owned workload ends, its terminal remains in the dashboard until disposed, but no longer
/// accepts input or automation. Reopening an ended terminal displays its ended state rather than replaying output.
/// </para>
/// </remarks>
[Experimental(TerminalDiagnostics.DiagnosticId, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class AspireTerminal : IAsyncDisposable
{
    internal AspireTerminal(ITerminalBackend backend)
    {
        Backend = backend;
    }

    internal ITerminalBackend Backend { get; }

    /// <summary>
    /// Gets the opaque identifier used to address this terminal over the dashboard connection.
    /// </summary>
    public string Id => Backend.Id;

    /// <summary>
    /// Gets the title shown on the terminal's dock tab.
    /// </summary>
    public string Title => Backend.Title;

    /// <summary>
    /// Gets the process that owns this terminal's workload.
    /// </summary>
    public TerminalOwner Owner => Backend.Owner;

    /// <summary>
    /// Gets where this terminal is displayed in the dashboard.
    /// </summary>
    public TerminalPlacement Placement => Backend.Placement;

    /// <summary>
    /// Starts the terminal's workload if it is not already running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starting is the caller's decision, not the dashboard's and not the interaction service's. Call this to
    /// have the workload running before anyone is looking at it — a terminal that is already running when a
    /// dialog opens shows its scrollback immediately, and automation can drive a terminal that is never
    /// displayed at all.
    /// </para>
    /// <para>
    /// This is idempotent and does not block: it schedules the workload rather than waiting for it to produce
    /// output. Use <see cref="WaitForTextAsync"/> to wait for the workload to reach a known state. It is also
    /// a no-op for <see cref="TerminalOwner.Resource"/> terminals, whose workload is started by the resource.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The terminal has already stopped.</exception>
    public void Start() => Backend.Start();

    /// <summary>
    /// Reveals the terminal dock in every connected dashboard and switches to this terminal's tab.
    /// </summary>
    /// <remarks>
    /// Only meaningful for <see cref="TerminalPlacement.Dock"/> terminals. Terminals in a dialog are revealed
    /// by that dialog, so this is a no-op for them.
    /// </remarks>
    public void Show() => Backend.Show();

    /// <summary>
    /// Sends text to the terminal's workload as though it had been typed.
    /// </summary>
    /// <param name="text">The text to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the input operation.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The AppHost-owned terminal has already stopped.</exception>
    /// <exception cref="OperationCanceledException">The input operation was canceled by <paramref name="cancellationToken"/>.</exception>
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
        => Backend.SendTextAsync(text, cancellationToken);

    /// <summary>
    /// Sends a single key, optionally combined with modifiers, to the terminal's workload.
    /// </summary>
    /// <remarks>
    /// Use the named values on <see cref="AspireTerminalKey"/> and combine them with
    /// <see cref="AspireTerminalKey.Ctrl"/>, <see cref="AspireTerminalKey.Shift"/>, and
    /// <see cref="AspireTerminalKey.Alt"/>. For example, <c>AspireTerminalKey.Ctrl(AspireTerminalKey.R)</c>
    /// sends Control+R. Use <see cref="SendTextAsync"/> for arbitrary text rather than individual keys.
    /// Key encoding follows the terminal's current input mode; a key does not represent a physical
    /// key-down or key-up event.
    /// </remarks>
    /// <param name="key">The key to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the input operation.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is an uninitialized value.</exception>
    /// <exception cref="InvalidOperationException">The AppHost-owned terminal has already stopped.</exception>
    /// <exception cref="OperationCanceledException">The input operation was canceled by <paramref name="cancellationToken"/>.</exception>
    public Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default)
    {
        // Reject invalid keys before the backend can start a workload or connect to a resource terminal.
        key.Validate(nameof(key));
        return Backend.SendKeyAsync(key, cancellationToken);
    }

    /// <summary>
    /// Waits until <paramref name="text"/> appears on the terminal screen.
    /// </summary>
    /// <param name="text">The text to wait for.</param>
    /// <param name="timeout">How long to wait before giving up. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the text appears on the terminal screen.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="TimeoutException">The text did not appear before <paramref name="timeout"/> elapsed.</exception>
    /// <exception cref="InvalidOperationException">The AppHost-owned terminal has already stopped.</exception>
    public Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => Backend.WaitForTextAsync(text, timeout, cancellationToken);

    /// <summary>
    /// Gets the current contents of the terminal screen, with lines separated by newlines.
    /// </summary>
    /// <returns>The current terminal screen text.</returns>
    /// <exception cref="InvalidOperationException">The AppHost-owned terminal has already stopped.</exception>
    public string GetScreenText() => Backend.GetScreenText();

    /// <summary>
    /// Releases the handle, stopping the workload only when it is owned by the AppHost.
    /// </summary>
    /// <returns>A task representing the terminal cleanup operation.</returns>
    public ValueTask DisposeAsync() => Backend.DisposeAsync();
}
