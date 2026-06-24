// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Processes;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

/// <summary>
/// The single <see cref="IProcessExecution"/> implementation. Wraps an <see cref="IsolatedProcess"/>
/// for both the isolated-console run path (Windows AppHost graceful shutdown) and ordinary
/// non-isolated subprocesses — the only difference is the
/// <see cref="IsolatedProcessStartInfo.IsolateConsole"/> flag the factory sets. The child is
/// spawned lazily on <see cref="Start"/> so callers that build an execution but never start it
/// (e.g. the extension-host launch path, which reads <see cref="Arguments"/> /
/// <see cref="EnvironmentVariables"/> and returns before starting) don't orphan a process.
/// </summary>
internal sealed class ProcessExecution : IProcessExecution
{
    private static readonly TimeSpan s_drainIdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_drainPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IsolatedProcessStartInfo _startInfo;
    private readonly string _fileName;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly ILogger _logger;
    private readonly ProcessInvocationOptions _options;
    private IsolatedProcess? _process;
    private long _lastActivityTimestamp = Stopwatch.GetTimestamp();
    private int _disposed;

    internal ProcessExecution(
        IsolatedProcessStartInfo startInfo,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        ILogger logger,
        ProcessInvocationOptions options)
    {
        _startInfo = startInfo;
        _fileName = fileName;
        _arguments = arguments;
        _environment = environment;
        _logger = logger;
        _options = options;
    }

    /// <inheritdoc />
    public string FileName => _fileName;

    /// <inheritdoc />
    public IReadOnlyList<string> Arguments => _arguments;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> EnvironmentVariables => _environment;

    /// <inheritdoc />
    public int ProcessId => Process.Id;

    /// <inheritdoc />
    public bool HasExited => Process.HasExited;

    /// <inheritdoc />
    public int ExitCode => Process.ExitCode;

    private IsolatedProcess Process =>
        _process ?? throw new InvalidOperationException($"{nameof(ProcessExecution)} has not been started. Call {nameof(Start)} first.");

    /// <inheritdoc />
    public bool Start()
    {
        // IsolatedProcess.Start spawns the child and starts the stdout/stderr pumps. It throws on
        // spawn failure (matching the old ProcessExecution, whose Process.Start could also throw),
        // so a successful return always means the child is running — there is no false-on-failure
        // case to model. The old Process.Start() == false path was dead for UseShellExecute=false.
        _process = IsolatedProcess.Start(_startInfo, OnOutputLine, OnErrorLine);
        _logger.LogDebug("{FileName}({ProcessId}) started in {WorkingDirectory}", _fileName, _process.Id, _startInfo.WorkingDirectory);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var process = Process;
        _logger.LogDebug("{FileName}({ProcessId}) waiting for exit", _fileName, process.Id);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{FileName}({ProcessId}) wait was canceled, stopping it", _fileName, process.Id);

            await ShutdownOnCancelAsync(process.Process).ConfigureAwait(false);

            // The child has now been signalled/killed by the coordinator. Drain trailing stdout/stderr
            // before propagating the cancellation so callers that observe output — or that swallow the
            // OCE and read ExitCode (e.g. the guest launcher distinguishing user-cancel from internal
            // teardown) — still get the full tail. Use a detached token + reset idle window so the drain
            // gets its whole budget even though the caller's token is already cancelled.
            RecordActivity();
            await DrainOutputAsync(process, CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", _fileName, process.Id, process.ExitCode);

        // Reset the idle window at exit so the drain budget is measured from "process gone", not
        // from the last line read. A consumer can block in a callback right up to exit and still
        // get the full tail — see
        // ProcessExecutionTests.WaitForExitAsync_AllowsBufferedTailOutputAfterLongIdlePeriod.
        RecordActivity();
        await DrainOutputAsync(process, cancellationToken).ConfigureAwait(false);

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
                _logger.LogWarning(ex, "Failed to kill {FileName} (pid {Pid}).", _fileName, GetSafePid(process));
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
                _logger.LogWarning(ex, "Error draining killed {FileName} (pid {Pid}).", _fileName, GetSafePid(process));
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
                _logger.LogDebug("{FileName} process {ProcessId} already exited.", _fileName, process.Id);
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                ProcessSignaler.RequestGracefulShutdown(process.Id, expectedStartTime: null, _logger);

                if (process.HasExited)
                {
                    return;
                }
            }

            _logger.LogDebug(
                "Sending kill to {FileName} process {ProcessId} (entireProcessTree={EntireProcessTree}).",
                _fileName,
                process.Id,
                entireProcessTree);
            process.Kill(entireProcessTree);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(
                ex,
                "{FileName} process exited before termination could complete (entireProcessTree={EntireProcessTree}).",
                _fileName,
                entireProcessTree);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to terminate {FileName} process (entireProcessTree={EntireProcessTree}).",
                _fileName,
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
            _logger.LogWarning(ex, "Failed to issue graceful shutdown to {FileName} (pid {Pid}); escalating to kill.", _fileName, pid);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // IsolatedProcess exposes only DisposeAsync — it drains the pumps then tears the
        // pipes/handles down. DotNetCliRunner does not dispose the execution (StartBackchannelAsync
        // runs fire-and-forget and reads HasExited/ExitCode after the await — see DotNetCliRunner.cs),
        // so this path is reached only by explicit `await using` consumers (the session, guest
        // launcher) and tests.
        var process = _process;
        if (process is null)
        {
            return;
        }

        // Terminate the child if it is still running. On the normal teardown paths the caller drives
        // WaitForExitAsync(token) first, so the shutdown ladder has already exited or killed the
        // process by the time we get here and this is a no-op. It matters for the path where an
        // execution was started but never driven (e.g. a fault between Start and the caller wiring up
        // its wait loop): IsolatedProcess.DisposeAsync only drains pumps and releases handles — it
        // does NOT terminate the process — so without this kill the child would be orphaned. Owning
        // "kill if still alive on dispose" here keeps that responsibility off every consumer.
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

        try
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{FileName} IsolatedProcess dispose threw", _fileName);
        }
    }

    private void OnOutputLine(IsolatedProcess sender, string line)
    {
        // RecordActivity brackets the callback (matching the old forwarder) so a slow consumer
        // keeps the drain budget alive both while we hand it the line and while it processes it.
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stdout: {Line}", _fileName, sender.Id, line);
        }
        _options.StandardOutputCallback?.Invoke(line);
        RecordActivity();
    }

    private void OnErrorLine(IsolatedProcess sender, string line)
    {
        RecordActivity();
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("{FileName}({ProcessId}) stderr: {Line}", _fileName, sender.Id, line);
        }
        _options.StandardErrorCallback?.Invoke(line);
        RecordActivity();
    }

    private async Task DrainOutputAsync(IsolatedProcess process, CancellationToken cancellationToken)
    {
        var drained = Task.WhenAll(process.StandardOutputClosed, process.StandardErrorClosed);

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
                    // A throwing callback faults the pump task and surfaces here. The pumps still
                    // drained to EOF so output isn't lost; log and move on — the exit code is valid.
                    _logger.LogWarning(ex, "{FileName}({ProcessId}) stdout/stderr pump faulted while draining after exit", _fileName, process.Id);
                }

                _logger.LogDebug("{FileName}({ProcessId}) output drained", _fileName, process.Id);
                return;
            }

            // Idle-based budget: a slow-but-progressing consumer keeps resetting the timer via
            // RecordActivity, so only a genuinely stalled pump (no output for the whole window)
            // gives up. The pumps keep running in the background and are reaped by DisposeAsync —
            // we never force the streams closed (that's the isolated path's already-accepted shape).
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp)) >= s_drainIdleTimeout)
            {
                _logger.LogWarning("{FileName}({ProcessId}) stdout/stderr pumps did not drain within idle timeout after exit", _fileName, process.Id);
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
