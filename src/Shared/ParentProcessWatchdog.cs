// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;

namespace Aspire.Shared;

/// <summary>
/// Watches a configured parent process and tears the current helper down if that parent disappears.
/// Used by long-running <c>aspire-managed</c> operations and terminal-host processes that must not
/// survive their launcher.
/// </summary>
internal static class ParentProcessWatchdog
{
    // If the operation ignores the cancellation token (e.g. a NuGet network call already issued with
    // CancellationToken.None, or a dashboard host that is slow to shut down), force the process to exit
    // after a short grace period so it cannot outlive its parent. 124 mirrors the conventional
    // "terminated by timeout" exit code.
    private const int TerminatedExitCode = 124;
    internal static TimeSpan ForceExitGracePeriod { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts monitoring the parent identified by <c>ASPIRE_CLI_PID</c>/<c>ASPIRE_CLI_STARTED</c>.
    /// When the parent is no longer alive, <paramref name="operationCts"/> is cancelled (and the
    /// process force-exits as a backstop). Returns a handle that stops the watchdog when disposed, or
    /// <see langword="null"/> when no parent identity is present — either because the helper was invoked
    /// directly, or because the launching CLI deliberately omitted the identity on Windows, where the
    /// kernel kill-on-close job already terminates ordinary helpers.
    /// </summary>
    public static IAsyncDisposable? Start(CancellationTokenSource operationCts)
        => Start(
            operationCts,
            KnownConfigNames.CliProcessId,
            KnownConfigNames.CliProcessStartedStable,
            KnownConfigNames.CliProcessStarted);

    /// <summary>
    /// Starts monitoring a parent identity supplied through the specified environment variables.
    /// </summary>
    public static IAsyncDisposable? Start(
        CancellationTokenSource operationCts,
        string processIdVariable,
        string stableStartVariable,
        string? legacyStartVariable)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(processIdVariable), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
        {
            return null;
        }

        var expectedStartTimeUnix = GetExpectedParentStartTimeUnix(
            Environment.GetEnvironmentVariable,
            stableStartVariable,
            legacyStartVariable,
            out var useRuntimeStartTime);

        return ParentProcessLivenessMonitor.Start(
            parentPid,
            expectedStartTimeUnix,
            stopToken => CancelAndForceExitAsync(operationCts, stopToken, ForceExitGracePeriod, Environment.Exit),
            useRuntimeStartTime: useRuntimeStartTime);
    }

    // Returns the stable identity value (ASPIRE_CLI_STARTED_STABLE, Unix milliseconds) when present,
    // otherwise the legacy value (ASPIRE_CLI_STARTED, whole Unix seconds). The out flag tells the caller
    // which clock domain the returned value is in.
    internal static long? GetExpectedParentStartTimeUnix(Func<string, string?> getEnvironmentVariable, out bool useRuntimeStartTime)
        => GetExpectedParentStartTimeUnix(
            getEnvironmentVariable,
            KnownConfigNames.CliProcessStartedStable,
            KnownConfigNames.CliProcessStarted,
            out useRuntimeStartTime);

    private static long? GetExpectedParentStartTimeUnix(
        Func<string, string?> getEnvironmentVariable,
        string stableStartVariable,
        string? legacyStartVariable,
        out bool useRuntimeStartTime)
    {
        if (ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(getEnvironmentVariable(stableStartVariable)) is { } stableStartTimeUnix)
        {
            useRuntimeStartTime = false;
            return stableStartTimeUnix;
        }

        useRuntimeStartTime = true;
        return legacyStartVariable is null
            ? null
            : ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(getEnvironmentVariable(legacyStartVariable));
    }

    internal static async Task CancelAndForceExitAsync(
        CancellationTokenSource operationCts,
        CancellationToken stopToken,
        TimeSpan forceExitGracePeriod,
        Action<int> exit)
    {
        // Cancellation callbacks run synchronously and can themselves stall. Arm the timer
        // first so the force-exit backstop remains independent of cooperative shutdown.
        var forceExitTask = ForceExitAfterDelayAsync(stopToken, forceExitGracePeriod, exit);

        try
        {
            if (!operationCts.IsCancellationRequested)
            {
                operationCts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // The operation already completed and disposed its CTS; nothing left to cancel.
        }
        catch (AggregateException)
        {
            // Cancellation callbacks run synchronously from Cancel(). A faulty callback must not skip
            // the force-exit backstop because that is what prevents aspire-managed from leaking.
        }

        await forceExitTask.ConfigureAwait(false);
    }

    private static async Task ForceExitAfterDelayAsync(
        CancellationToken stopToken,
        TimeSpan forceExitGracePeriod,
        Action<int> exit)
    {
        await Task.Delay(forceExitGracePeriod, stopToken).ConfigureAwait(false);
        exit(TerminatedExitCode);
    }
}
