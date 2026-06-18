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

    // Pre-cancelled token handed to the force-kill fallback's graceful wait. An already-cancelled
    // token makes ProcessTerminator dispatch the best-effort SIGTERM (Unix) and then immediately
    // escalate to Kill, rather than waiting for a graceful exit. CancellationToken.None must NOT
    // be used here: with requestGracefulShutdown the terminator would WaitForExitAsync(None) and
    // block forever if the child ignores SIGTERM.
    private static readonly CancellationToken s_immediateEscalation = new(canceled: true);

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
    /// cancellation. Picks between the shared <see cref="ProcessGracefulShutdownLadder"/> (graceful
    /// signal → bounded wait → tree-kill, used on the <c>aspire run</c> path) and the best-effort
    /// <see cref="ProcessTerminator"/> force-kill fallback (non-Run callers).
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

            return ProcessGracefulShutdownLadder.ExecuteAsync(
                process,
                signaler,
                gracefulShutdownWindow.GracefulShutdownToken,
                _logger,
                _fileName);
        }

        return ProcessTerminator.ShutdownAsync(
            process,
            requestGracefulShutdown: !OperatingSystem.IsWindows(),
            entireProcessTree: _options.KillEntireProcessTreeOnCancel,
            _logger,
            _fileName,
            gracefulShutdownCancellationToken: s_immediateEscalation);
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
