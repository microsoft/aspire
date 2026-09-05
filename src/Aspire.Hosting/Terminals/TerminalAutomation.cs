// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Hex1b;
using Hex1b.Automation;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The shared implementation of <see cref="IAspireTerminal"/>'s automation members.
/// </summary>
/// <remarks>
/// Every terminal Aspire exposes is ultimately a <see cref="Hex1bTerminal"/>, whether its workload runs in the
/// AppHost or in a resource's terminal host that this process is merely connected to as a peer. Only the way
/// that terminal is obtained differs, so the automation semantics — cancellation layering, exception
/// translation, snapshot disposal — live here once rather than in each implementation.
/// </remarks>
internal static class TerminalAutomation
{
    /// <summary>
    /// How long the wait helpers poll for before giving up when the caller does not specify a timeout.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static Task SendTextAsync(Hex1bTerminalAutomator automator, string text, CancellationToken cancellationToken)
        => automator.TypeAsync(text, cancellationToken);

    public static Task SendKeyAsync(Hex1bTerminal terminal, AspireTerminalKey key, CancellationToken cancellationToken)
    {
        var sequence = AspireTerminalKeySequences.Get(key);
        return terminal.SendInputAsync(Encoding.UTF8.GetBytes(sequence), cancellationToken);
    }

    public static async Task WaitForTextAsync(
        Hex1bTerminalAutomator automator,
        string terminalId,
        string text,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        // Hex1b's wait takes a timeout but no token, so the caller's cancellation is layered on here. The
        // underlying wait keeps running until its timeout elapses; that is acceptable because it is a passive
        // screen poll with no side effects.
        var wait = automator.WaitUntilTextAsync(text, timeout ?? DefaultTimeout);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);

        var completed = await Task.WhenAny(wait, cancelled.Task).ConfigureAwait(false);
        if (completed != wait)
        {
            // The wait is abandoned rather than awaited, so nothing would observe the WaitUntilTimeoutException it
            // raises when its own timeout later elapses. An unobserved faulted task surfaces on
            // TaskScheduler.UnobservedTaskException, which is a process-wide event an AppHost may treat as fatal.
            _ = wait.ContinueWith(
                static t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            await wait.ConfigureAwait(false);
        }
        catch (WaitUntilTimeoutException ex)
        {
            // Translate so callers never have to reference Hex1b to handle a timeout.
            throw new TimeoutException($"Terminal '{terminalId}' did not display the expected text within the timeout.", ex);
        }
    }

    /// <summary>
    /// Reads the current screen, treating a terminal that has no automator yet as an empty screen.
    /// </summary>
    /// <remarks>
    /// A terminal that has never been attached to or driven has no screen yet. Reporting empty is friendlier
    /// than starting the workload, or dialling a socket, as a side effect of a read.
    /// </remarks>
    public static string GetScreenText(Hex1bTerminalAutomator? automator)
    {
        if (automator is null)
        {
            return string.Empty;
        }

        // The snapshot holds pooled buffers, so it must be released rather than left to finalization.
        using var snapshot = automator.CreateSnapshot();
        return snapshot.GetScreenText();
    }
}
