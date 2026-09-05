// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// A terminal owned by the AppHost process, surfaced in the dashboard and driveable from AppHost code.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a thin, Aspire-shaped abstraction over the underlying terminal implementation
/// (currently Hex1b). Keeping the implementation type out of this interface is what lets terminals be
/// used from Aspire APIs without dragging Hex1b's very large surface area into Aspire's own.
/// </para>
/// <para>
/// The automation members are an intentionally small subset. Hex1b exposes a rich cell-pattern matching
/// DSL (<c>CellPatternSearcher</c> and around sixty supporting types); none of it is projected here.
/// "Send some input, wait for some text, read the screen" covers the scenarios a spike needs, and the
/// surface can grow later if real usage demands it.
/// </para>
/// <para>
/// Disposing the terminal cancels its workload and removes it from the dashboard. Whoever creates a
/// terminal owns it and must dispose it; showing one in an interaction does not transfer that ownership,
/// so a terminal survives the dialog it was displayed in.
/// </para>
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public interface IAspireTerminal : IAsyncDisposable
{
    /// <summary>
    /// Gets the opaque identifier used to address this terminal over the dashboard connection.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the title shown on the terminal's dock tab.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Gets the surface this terminal is displayed on.
    /// </summary>
    TerminalSurface Surface { get; }

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
    /// output. Use <see cref="WaitForTextAsync"/> to wait for the workload to reach a known state.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The terminal has already stopped.</exception>
    void Start();

    /// <summary>
    /// Reveals the terminal dock in every connected dashboard and switches to this terminal's tab.
    /// </summary>
    /// <remarks>
    /// Only meaningful for <see cref="TerminalSurface.Dock"/> terminals. Interaction terminals are
    /// revealed by their dialog, so this is a no-op for them.
    /// </remarks>
    void Show();

    /// <summary>
    /// Sends text to the terminal's workload as though it had been typed.
    /// </summary>
    Task SendTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a single non-printable key to the terminal's workload.
    /// </summary>
    Task SendKeyAsync(AspireTerminalKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until <paramref name="text"/> appears on the terminal screen.
    /// </summary>
    /// <param name="text">The text to wait for.</param>
    /// <param name="timeout">How long to wait before giving up. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="TimeoutException">The text did not appear before <paramref name="timeout"/> elapsed.</exception>
    Task WaitForTextAsync(string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current contents of the terminal screen, with lines separated by newlines.
    /// </summary>
    string GetScreenText();
}
