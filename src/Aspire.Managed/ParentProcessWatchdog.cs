// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting;

namespace Aspire.Managed;

/// <summary>
/// Watches the launching CLI process and tears this <c>aspire-managed</c> helper down if the parent
/// disappears. Long-running operations — a NuGet search/restore, or the standalone dashboard started
/// by <c>aspire dashboard run</c> — would otherwise linger as orphaned processes when the CLI is killed
/// (for example a test runner timeout sending SIGKILL), which is one of the ways <c>aspire-managed</c>
/// processes accumulate over time.
/// </summary>
internal static class ParentProcessWatchdog
{
    // If the operation ignores the cancellation token (e.g. a NuGet network call already issued with
    // CancellationToken.None, or a dashboard host that is slow to shut down), force the process to exit
    // after a short grace period so it cannot outlive its parent. 124 mirrors the conventional
    // "terminated by timeout" exit code.
    private const int TerminatedExitCode = 124;
    private static readonly TimeSpan s_forceExitGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts monitoring the parent identified by <c>ASPIRE_CLI_PID</c>/<c>ASPIRE_CLI_STARTED</c>.
    /// When the parent is no longer alive, <paramref name="operationCts"/> is cancelled (and the
    /// process force-exits as a backstop). Returns a handle that stops the watchdog when disposed, or
    /// <see langword="null"/> when no parent is configured (the helper was invoked directly).
    /// </summary>
    public static IAsyncDisposable? Start(CancellationTokenSource operationCts)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable(KnownConfigNames.CliProcessId), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentPid))
        {
            return null;
        }

        var expectedStartTimeUnix = GetExpectedParentStartTimeUnix(Environment.GetEnvironmentVariable, out var useRuntimeStartTime);

        return ParentProcessLivenessMonitor.Start(
            parentPid,
            expectedStartTimeUnix,
            stopToken => OnParentExitedAsync(operationCts, stopToken, s_forceExitGracePeriod, Environment.Exit),
            useRuntimeStartTime: useRuntimeStartTime);
    }

    // Returns the stable identity value (ASPIRE_CLI_STARTED_STABLE, Unix milliseconds) when present,
    // otherwise the legacy value (ASPIRE_CLI_STARTED, whole Unix seconds). The out flag tells the caller
    // which clock domain the returned value is in.
    internal static long? GetExpectedParentStartTimeUnix(Func<string, string?> getEnvironmentVariable, out bool useRuntimeStartTime)
    {
        if (ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(getEnvironmentVariable(KnownConfigNames.CliProcessStartedStable)) is { } stableStartTimeUnix)
        {
            useRuntimeStartTime = false;
            return stableStartTimeUnix;
        }

        useRuntimeStartTime = true;
        return ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(getEnvironmentVariable(KnownConfigNames.CliProcessStarted));
    }

    internal static async Task OnParentExitedAsync(
        CancellationTokenSource operationCts,
        CancellationToken stopToken,
        TimeSpan forceExitGracePeriod,
        Action<int> exit)
    {
        // Parent is gone: ask the in-flight operation to stop, then hard-exit if it doesn't.
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

        await Task.Delay(forceExitGracePeriod, stopToken).ConfigureAwait(false);
        exit(TerminatedExitCode);
    }
}
