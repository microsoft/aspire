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
    /// Starts monitoring the host application that launched a long-running CLI command
    /// (<c>ps --follow</c>, <c>describe --follow</c>) when it supplied its identity. The command exits
    /// when that host disappears so a force-killed launcher (for example the VS Code extension host)
    /// cannot leave the command running forever.
    /// </summary>
    /// <remarks>
    /// Deliberately not public: CLI commands must go through <see cref="StartCliParentScope"/> so the
    /// watchdog is armed before any awaited setup and the command cannot obtain a token without it.
    /// </remarks>
    /// <returns>A handle that stops the watchdog when disposed, or <see langword="null"/> when this
    /// process was not launched by a host that opted into the watchdog.</returns>
    internal static IAsyncDisposable? StartForCliParent(CancellationTokenSource operationCts)
        => Start(
            operationCts,
            KnownConfigNames.CliParentProcessId,
            stableStartVariable: null,
            KnownConfigNames.CliParentProcessStarted);

    /// <summary>
    /// Starts watching the host that launched this CLI process and returns the cancellation scope the
    /// command must run under. This is the only supported way to observe the launcher: the monitor is
    /// always armed, and when no launcher identity is present it simply never fires, so a command cannot
    /// end up running with no watchdog at all.
    /// </summary>
    /// <remarks>
    /// A command that resolves a connection, performs an RPC handshake, or loads initial state before
    /// arming the watchdog can stall in that setup forever after the launcher is force-killed, because
    /// nothing is watching yet. <c>BaseCommand</c> calls this before <c>ExecuteAsync</c> for commands that
    /// opt in via <c>RunsUnderLauncherWatchdog</c>.
    /// </remarks>
    public static ParentProcessWatchdogScope StartCliParentScope(CancellationToken cancellationToken)
    {
        var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return new ParentProcessWatchdogScope(operationCts, StartForCliParent(operationCts));
        }
        catch
        {
            operationCts.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts monitoring a parent identity supplied through the specified environment variables.
    /// </summary>
    public static IAsyncDisposable? Start(
        CancellationTokenSource operationCts,
        string processIdVariable,
        string? stableStartVariable,
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

    internal static long? GetExpectedParentStartTimeUnix(
        Func<string, string?> getEnvironmentVariable,
        string? stableStartVariable,
        string? legacyStartVariable,
        out bool useRuntimeStartTime)
    {
        if (stableStartVariable is not null &&
            ProcessStartTimeHelper.TryParseStartTimeUnixSeconds(getEnvironmentVariable(stableStartVariable)) is { } stableStartTimeUnix)
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

/// <summary>
/// The cancellation scope a long-running CLI command runs under while a launcher watchdog is armed.
/// Created by <see cref="ParentProcessWatchdog.StartCliParentScope"/>, so obtaining the token and arming
/// the watchdog cannot be separated: there is no way to hold this scope without the monitor running.
/// </summary>
internal sealed class ParentProcessWatchdogScope : IAsyncDisposable
{
    private readonly CancellationTokenSource _operationCts;
    private readonly CancellationToken _token;
    private readonly IAsyncDisposable? _watchdog;

    internal ParentProcessWatchdogScope(CancellationTokenSource operationCts, IAsyncDisposable? watchdog)
    {
        _operationCts = operationCts;
        // Capture the token before disposal so callers can still inspect it while unwinding, which is
        // exactly when cancellation handlers and catch filters run. Reading CancellationTokenSource.Token
        // after Dispose throws ObjectDisposedException.
        _token = operationCts.Token;
        _watchdog = watchdog;
    }

    /// <summary>
    /// Gets the token that is cancelled when the caller cancels or the watched launcher disappears.
    /// Every await in the command, including setup before the main loop, must observe this token.
    /// Remains readable after <see cref="DisposeAsync"/>.
    /// </summary>
    public CancellationToken Token => _token;

    /// <summary>
    /// Gets a value indicating whether the launcher watchdog is actually armed.
    /// <see langword="false"/> when no launcher identity was supplied, which means only the caller's
    /// own cancellation can stop the command.
    /// </summary>
    public bool IsWatchingLauncher => _watchdog is not null;

    /// <summary>
    /// Requests cancellation of <see cref="Token"/> without disarming the watchdog, so the force-exit
    /// backstop stays armed while the command unwinds. A no-op once the scope is disposed.
    /// </summary>
    public async Task CancelAsync()
    {
        try
        {
            await _operationCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The command already completed and disposed its scope; there is nothing left to cancel.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watchdog is not null)
        {
            await _watchdog.DisposeAsync().ConfigureAwait(false);
        }

        _operationCts.Dispose();
    }
}
