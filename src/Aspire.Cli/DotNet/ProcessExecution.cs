// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Aspire.Cli.Processes;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// The single <see cref="IProcessExecution"/> implementation. Wraps a <see cref="Process"/> for
/// isolated-console, kill-on-parent-exit, detached, and ordinary redirected subprocesses. The child is
/// spawned lazily on <see cref="IProcessExecution.StartAsync"/> so callers that build an execution but never start it (e.g.
/// the extension-host launch path, which reads <see cref="Arguments"/> /
/// <see cref="EnvironmentVariables"/> and returns before starting) don't orphan a process.
/// </summary>
internal sealed class ProcessExecution : IProcessExecution
{
    private static readonly TimeSpan s_drainIdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_drainPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ProcessStartInfo _startInfo;
    private readonly ILogger _logger;
    private readonly ProcessInvocationOptions _options;
    private readonly IEnvironment _hostEnvironment;
    private readonly Lock _lifecycleLock = new();
    private Process? _process;
    private int _processId;
    private DateTimeOffset? _startTime;
    private Task _outputDrained = Task.CompletedTask;
    private bool _disposed;
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();

    internal ProcessExecution(
        ProcessStartInfo startInfo,
        ILogger logger,
        ProcessInvocationOptions options,
        IEnvironment hostEnvironment)
    {
        _startInfo = startInfo;
        _logger = logger;
        _options = options;
        _hostEnvironment = hostEnvironment;
        EnvironmentVariables = new ReadOnlyDictionary<string, string?>(startInfo.Environment);
    }

    /// <inheritdoc />
    public string FileName => _startInfo.FileName;

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments => _startInfo.ArgumentList;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; }

    /// <inheritdoc />
    public int ProcessId
    {
        get
        {
            // Captured at spawn because Process.Id throws once the handle is disposed.
            _ = Process;
            return _processId;
        }
    }

    /// <inheritdoc />
    public bool HasExited => Process.HasExited;

    /// <inheritdoc />
    public int ExitCode => Process.ExitCode;

    /// <inheritdoc />
    public DateTimeOffset? StartTime
    {
        get
        {
            _ = Process;
            return _startTime;
        }
    }

    private Process Process =>
        Volatile.Read(ref _process)
        ?? throw new InvalidOperationException($"{nameof(ProcessExecution)} has not been started. Call {nameof(StartAsync)} first.");

    /// <inheritdoc />
    public Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process process;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is not null)
            {
                throw new InvalidOperationException($"{nameof(ProcessExecution)} has already been started.");
            }

            // Children never consume input from the CLI. A null stdin makes tools such as
            // package-manager lifecycle scripts observe EOF instead of inheriting the TTY and blocking
            // indefinitely (https://github.com/microsoft/aspire/issues/16791). A detached child
            // outlives the CLI, so nothing would be left to drain redirected output either.
            using var nullHandle = File.OpenNullHandle();
            _startInfo.StandardInputHandle = nullHandle;
            if (_options.Detached)
            {
                _startInfo.StandardOutputHandle = nullHandle;
                _startInfo.StandardErrorHandle = nullHandle;
            }

            // Process.Start() only returns null for UseShellExecute, which is never used here.
            process = Process.Start(_startInfo)
                ?? throw new InvalidOperationException($"Failed to start child process: {_startInfo.FileName}");
            _processId = process.Id;
            _startTime = GetStartTime(process);
            Volatile.Write(ref _process, process);

            // Publish the process before reading output so callbacks can read ProcessId.
            if (!_options.Detached)
            {
                _outputDrained = Task.Run(() => ReadOutputAsync(process), CancellationToken.None);
            }
        }

        _logger.LogDebug("{FileName}({ProcessId}) started in {WorkingDirectory}", FileName, _processId, _startInfo.WorkingDirectory);
        return Task.FromResult(true);
    }

    private static DateTimeOffset? GetStartTime(Process process)
    {
        try
        {
            return ProcessStartTimeHelper.TryGetProcessStartTime(process.Id) ?? new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The child already exited and was reaped.
            return null;
        }
    }

    /// <summary>
    /// Forwards each output line to the callbacks until both pipes reach EOF.
    /// </summary>
    /// <remarks>
    /// <see cref="Process.ReadAllLinesAsync"/> reads the pipes directly, unlike
    /// <see cref="Process.BeginOutputReadLine"/>, so <see cref="Process.WaitForExitAsync"/> does not
    /// also wait for EOF, which a grandchild holding the inherited pipe (e.g. a build server) can
    /// delay indefinitely. <see cref="DrainOutputAsync"/> bounds the wait for EOF instead.
    /// </remarks>
    private async Task ReadOutputAsync(Process process)
    {
        Exception? firstCallbackException = null;
        try
        {
            await foreach (var line in process.ReadAllLinesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    if (line.StandardError)
                    {
                        OnErrorLine(line.Content);
                    }
                    else
                    {
                        OnOutputLine(line.Content);
                    }
                }
                catch (Exception ex)
                {
                    // Keep draining so a throwing callback cannot back-pressure the child through a
                    // full pipe. The first failure is surfaced after EOF.
                    firstCallbackException ??= ex;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // DisposeAsync released the pipes while a read was pending (no token is passed, so a
            // cancellation can only come from that). Treat as EOF.
            return;
        }

        if (firstCallbackException is not null)
        {
            ExceptionDispatchInfo.Throw(firstCallbackException);
        }
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = Process;
        _logger.LogDebug("{FileName}({ProcessId}) waiting for exit", FileName, _processId);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{FileName}({ProcessId}) wait was canceled, stopping it", FileName, _processId);

            await ShutdownOnCancelAsync(process).ConfigureAwait(false);

            // The child has now been signalled/killed by the coordinator. Drain trailing stdout/stderr
            // before propagating the cancellation so callers that observe output — or that swallow the
            // OCE and read ExitCode (e.g. the guest launcher distinguishing user-cancel from internal
            // teardown) — still get the full tail. Use a detached token + reset idle window so the drain
            // gets its whole budget even though the caller's token is already cancelled.
            RecordActivity();
            await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", FileName, _processId, process.ExitCode);

        // Reset the idle window at exit so the drain budget is measured from "process gone", not
        // from the last line read. A consumer can block in a callback right up to exit and still
        // get the full tail — see
        // ProcessExecutionTests.WaitForExitAsync_AllowsBufferedTailOutputAfterLongIdlePeriod.
        RecordActivity();
        await DrainOutputAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>
    /// The single decision point this execution routes through when its child must be torn down on
    /// cancellation. Both branches run the same <see cref="ShutdownLadderAsync"/>: with a signaler for
    /// the graceful ladder (the <c>aspire run</c> path) or without one for the best-effort force-kill
    /// fallback (non-Run callers).
    /// </summary>
    /// <remarks>
    /// The graceful-vs-force decision is command-level and all-or-nothing: it keys off
    /// <see cref="IGracefulShutdownWindow.IsEnabled"/> (true when the running command configured a
    /// positive budget). There is no per-child or per-call flag. When the ladder is selected this also
    /// starts the central clock via <see cref="IGracefulShutdownWindow.BeginGracefulWindow"/>, so the
    /// ladder's wait is always bounded regardless of whether teardown was initiated by a user signal or
    /// by disposal of the child owner.
    /// </remarks>
    private Task ShutdownOnCancelAsync(Process process)
    {
        var signaler = _options.GracefulShutdownSignaler;
        var gracefulShutdownWindow = _options.ShutdownService;

        if (signaler is not null && gracefulShutdownWindow is { IsEnabled: true })
        {
            // Start the central clock so the ladder's wait is bounded even when teardown was triggered
            // by disposal (e.g. normal aspire run completion) rather than a user signal. Idempotent —
            // if a user Ctrl+C already armed the window this is a no-op.
            gracefulShutdownWindow.BeginGracefulWindow();

            return ShutdownLadderAsync(process, signaler, gracefulShutdownWindow.GracefulShutdownToken);
        }

        return ShutdownLadderAsync(process, signaler: null, gracefulToken: CancellationToken.None);
    }

    /// <summary>
    /// Shuts down the child, choosing the graceful ladder or the force-kill fallback based on whether
    /// <paramref name="signaler"/> is supplied. Graceful mode (signaler present — <c>aspire run</c>)
    /// runs the four-phase "graceful signal → bounded wait → force tree-kill → bounded drain"
    /// escalation; force mode (no signaler — build/restore/etc.) does a best-effort courtesy SIGTERM on
    /// Unix (a no-op on Windows) then an immediate kill. Both modes tree-kill the same way and differ
    /// only in whether a graceful budget is honored before the kill.
    /// </summary>
    /// <remarks>
    /// Whoever triggers shutdown (<see cref="ConsoleCancellationManager.Cancel"/>) owns the central
    /// clock; this consumes <paramref name="gracefulToken"/> but never owns timing.
    /// </remarks>
    private async Task ShutdownLadderAsync(Process process, IProcessTreeGracefulShutdownSignaler? signaler, CancellationToken gracefulToken)
    {
        if (signaler is null)
        {
            // Force mode: no graceful budget. Best-effort courtesy SIGTERM (Unix) then hard-kill.
            ForceKillChild(process);
            return;
        }

        // Phase 1: fire-and-forget the graceful signal so its own wait does not consume the
        // graceful budget. The signal request blocks until the target process exits, so awaiting
        // it sequentially would burn the entire graceful window and leave nothing for Phase 2's
        // exit-wait — forcing a tree-kill even when the apphost was about to exit cleanly. Running
        // it in parallel lets the apphost receive the signal immediately while the full budget goes
        // to the exit-wait. The signal is dispatched unconditionally (not gated on the graceful
        // token) so callers that intentionally Expire() the budget (e.g. `aspire stop`) still get a
        // best-effort signal.
        var signalTask = InvokeSignalerAsync(signaler, GetSafePid(process), gracefulToken);

        try
        {
            // Phase 2: wait for exit with the FULL graceful budget. When the apphost exits,
            // the signaler task observes the same exit and completes shortly after. Whoever
            // triggered shutdown (CCM.Cancel) owns the timing of `gracefulToken`.
            try
            {
                await process.WaitForExitAsync(gracefulToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Graceful budget expired; fall through to kill.
            }

            if (process.HasExited)
            {
                return;
            }

            // Phase 3: ALWAYS tree-kill on escalation, regardless of OS. Even when the graceful
            // signal returned cleanly, descendants may still be alive — e.g. on Windows tsx wraps
            // node and swallows Ctrl+C/Ctrl+Break, leaving the child node and any further
            // descendants running after the tsx shell exits. Skipping tree-kill would orphan them.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Process exited between HasExited check and Kill — nothing to do.
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill {FileName} (pid {Pid}).", FileName, GetSafePid(process));
                return;
            }

            // Phase 4: brief separately-bounded drain after kill — independent of the central token
            // because by now the central budget has already expired. 1 s is enough for the OS to
            // reap the process so the subsequent ExitCode read succeeds.
            try
            {
                using var killDrain = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(killDrain.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort; nothing more we can do.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error draining killed {FileName} (pid {Pid}).", FileName, GetSafePid(process));
            }
        }
        finally
        {
            // Always observe the signaler before returning, on EVERY path (clean exit, tree-kill
            // escalation, or an early return from a catch arm above). The signaler begins with
            // `await Task.Yield()` (see InvokeSignalerAsync), so its body — which records the target
            // pid and dispatches the signal — runs on a thread-pool continuation; returning without
            // awaiting it could abandon the ladder before that continuation runs, so the signal would
            // never be dispatched. Awaiting here also drains it so a slow signal can't outlive us as
            // an orphan. By now the process has exited or been tree-killed, so the signal returns
            // promptly. Skip the timer allocation when it already finished; SuppressThrowing swallows
            // both the bounded drain timeout and any signaler fault without a try/catch.
            if (signalTask.IsCompleted)
            {
                await signalTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            else
            {
                using var drainCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await signalTask.WaitAsync(drainCts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    private void ForceKillChild(Process process)
    {
        // Mirrors the force path: resolve "already gone?", issue a best-effort courtesy SIGTERM on Unix
        // (so a SIGTERM-aware child can flush), then hard-kill. On Windows there is no graceful signal
        // to send here — Ctrl+C delivery only happens on the signaler-backed graceful ladder — so we
        // skip straight to the kill.
        var entireProcessTree = _options.KillEntireProcessTreeOnCancel;
        try
        {
            if (process.HasExited)
            {
                _logger.LogDebug("{FileName} process {ProcessId} already exited.", FileName, process.Id);
                return;
            }

            if (!_hostEnvironment.IsWindows())
            {
                ProcessSignaler.RequestGracefulShutdown(process.Id, expectedStartTime: null, _logger);

                if (process.HasExited)
                {
                    return;
                }
            }

            _logger.LogDebug(
                "Sending kill to {FileName} process {ProcessId} (entireProcessTree={EntireProcessTree}).",
                FileName,
                process.Id,
                entireProcessTree);
            process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(
                ex,
                "{FileName} process exited before termination could complete (entireProcessTree={EntireProcessTree}).",
                FileName,
                entireProcessTree);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to terminate {FileName} process (entireProcessTree={EntireProcessTree}).",
                FileName,
                entireProcessTree);
        }
    }

    private async Task InvokeSignalerAsync(IProcessTreeGracefulShutdownSignaler signaler, int pid, CancellationToken gracefulToken)
    {
        try
        {
            // startTime is null because includeStartTimeForDcp is false here: neither the Unix nor
            // the Windows signal path consults StartTime at this call site, and querying
            // Process.StartTime could throw on a process whose handle has already been closed.
            //
            // Yield onto the thread pool first: the signal request blocks until the target process
            // exits, which is exactly the wait we don't want to serialize in front of Phase 2's
            // exit-wait (see ShutdownLadderAsync Phase 1).
            await Task.Yield();

            await signaler.RequestProcessTreeGracefulShutdownAsync(
                pid,
                startTime: null,
                includeStartTimeForDcp: false,
                gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
        {
            // Graceful budget expired before the signal could be issued; the kill path
            // is responsible for terminating the process.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to issue graceful shutdown to {FileName} (pid {Pid}); escalating to kill.", FileName, pid);
        }
    }

    private static int GetSafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    /// <inheritdoc />
    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
        }

        if (process is null)
        {
            return;
        }

        // DotNetCliRunner does not dispose the execution (StartBackchannelAsync runs fire-and-forget
        // and reads HasExited/ExitCode after the await — see DotNetCliRunner.cs), so this path is
        // reached only by explicit `await using` consumers (the session, guest launcher) and tests.
        //
        // Terminate the child if it is still running. On the normal teardown paths the caller drives
        // WaitForExitAsync(token) first, so the shutdown ladder has already exited or killed the
        // process by the time we get here and this is a no-op. It matters for the path where an
        // execution was started but never driven (e.g. a fault between Start and the caller wiring up
        // its wait loop): Process.Dispose releases handles but does NOT terminate the process — so
        // without this kill the child would be orphaned. Owning "kill if still alive on dispose" here
        // keeps that responsibility off every consumer.
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort: the process may have exited between the check and the kill, or be
            // unkillable. The drain/handle release below still runs.
        }

        await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);
        process.Dispose();
    }

    private void OnOutputLine(string line)
    {
        // RecordActivity brackets the callback so a slow consumer
        // keeps the drain budget alive both while we hand it the line and while it processes it.
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stdout: {Line}", FileName, _processId, line);
        }
        _options.StandardOutputCallback?.Invoke(line);
        RecordActivity();
    }

    private void OnErrorLine(string line)
    {
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stderr: {Line}", FileName, _processId, line);
        }
        _options.StandardErrorCallback?.Invoke(line);
        RecordActivity();
    }

    private async Task DrainOutputAsync(CancellationToken cancellationToken)
    {
        var drained = _outputDrained;

        while (true)
        {
            if (drained.IsCompleted)
            {
                try
                {
                    await drained.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A throwing callback faults the reader task and surfaces here. The reader still
                    // drained to EOF so output isn't lost; log and move on — the exit code is valid.
                    _logger.LogWarning(ex, "{FileName}({ProcessId}) stdout/stderr callback faulted while draining after exit", FileName, _processId);
                }

                _logger.LogDebug("{FileName}({ProcessId}) output drained", FileName, _processId);
                return;
            }

            // Idle-based budget: a slow-but-progressing consumer keeps resetting the timer via
            // RecordActivity, so only a genuinely stalled reader (no output for the whole window)
            // gives up. The reader keeps running in the background until DisposeAsync releases the
            // pipes — this method never closes streams while callbacks may still be processing data.
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp)) >= s_drainIdleTimeout)
            {
                _logger.LogWarning("{FileName}({ProcessId}) stdout/stderr did not drain within idle timeout after exit", FileName, _processId);
                return;
            }

            try
            {
                await Task.Delay(s_drainPollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private void RecordActivity() => Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
}
