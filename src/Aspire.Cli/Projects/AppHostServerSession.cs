// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Processes;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Owns the lifetime of an AppHost server child process. Construction stashes configuration
/// (including the stop token) without launching anything; <see cref="StartAsync"/> launches the
/// process, wires lifecycle observation, and returns a task that completes with the process exit
/// code.
/// </summary>
/// <remarks>
/// The session is the only class that calls <see cref="Process.Kill(bool)"/> on its child.
/// Termination is requested either by cancelling the <c>stopRequested</c> token passed to the
/// constructor, or by calling <see cref="DisposeAsync"/>. Both routes flow through the same
/// internal linked CTS, so there is exactly one kill site (the registered callback). Callers
/// should not reach into <see cref="ServerProcess"/> to drive lifecycle.
/// </remarks>
internal sealed class AppHostServerSession : IAsyncDisposable
{
    private const string ProcessDescription = "AppHost server";

    private readonly IAppHostServerProject _project;
    private readonly Dictionary<string, string>? _callerEnvironmentVariables;
    private readonly bool _debug;
    private readonly ILogger _logger;
    private readonly ProfilingTelemetry? _profilingTelemetry;
    private readonly string _authenticationToken;
    private readonly CancellationTokenSource _stopCts;
    private readonly CancellationToken _externalStopToken;
    private readonly IProcessTreeGracefulShutdownSignaler? _gracefulShutdownSignaler;
    private readonly GracefulShutdownService? _shutdownService;
    private readonly bool _isolateConsole;
    private readonly WindowsConsoleProcessJob? _consoleProcessJob;

    private readonly object _startGate = new();
    private bool _startInvoked;
    private bool _disposed;
    private int _stopRequested;

    private Process? _serverProcess;
    private IAsyncDisposable? _processLifetime;
    private string? _socketPath;
    private OutputCollector? _output;
    private TaskCompletionSource<int>? _completion;
    private ProfilingTelemetry.ActivityScope _activity;
    private CancellationTokenRegistration _stopRegistration;
    private IAppHostRpcClient? _rpcClient;
    private Task? _shutdownTask;

    public AppHostServerSession(
        IAppHostServerProject project,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        ILogger logger,
        CancellationToken stopRequested,
        ProfilingTelemetry? profilingTelemetry = null,
        IProcessTreeGracefulShutdownSignaler? gracefulShutdownSignaler = null,
        GracefulShutdownService? shutdownService = null,
        bool isolateConsole = false,
        WindowsConsoleProcessJob? consoleProcessJob = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _callerEnvironmentVariables = environmentVariables;
        _debug = debug;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profilingTelemetry = profilingTelemetry;
        _authenticationToken = TokenGenerator.GenerateToken();
        _externalStopToken = stopRequested;
        _gracefulShutdownSignaler = gracefulShutdownSignaler;
        _shutdownService = shutdownService;
        _isolateConsole = isolateConsole;
        _consoleProcessJob = consoleProcessJob;

        // Fail fast on misconfigured isolation: on Windows the kill-on-close job is the safety
        // net that ensures the AppHost server doesn't outlive a CLI crash as an orphan in its
        // new console group. Without the job the new-console isolation is a downgrade, not a
        // safety net. Mirrored in IsolatedConsoleSpawner as defense-in-depth.
        if (isolateConsole && OperatingSystem.IsWindows() && consoleProcessJob is null)
        {
            throw new ArgumentNullException(
                nameof(consoleProcessJob),
                "consoleProcessJob is required when isolateConsole is true on Windows.");
        }

        // Linked CTS so caller-initiated cancellation AND DisposeAsync both flow through the
        // same stop trigger. The registered callback on _stopCts.Token (wired in StartAsync) is
        // the single kill site for the process. OnStopRequested reads _externalStopToken to
        // distinguish "caller-initiated stop" (run the graceful ladder) from "dispose-only stop"
        // (force-kill immediately — graceful ladder would hang because nothing started the
        // central GracefulShutdownService timer).
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(stopRequested);
    }

    /// <summary>
    /// Gets the authentication token injected into the server environment. Available before
    /// <see cref="StartAsync"/> so callers can plumb it into the guest AppHost environment.
    /// </summary>
    public string AuthenticationToken => _authenticationToken;

    /// <summary>
    /// Gets the RPC socket path, or <see langword="null"/> if <see cref="StartAsync"/> has not
    /// been called (or threw before the process was published).
    /// </summary>
    public string? SocketPath => _socketPath;

    /// <summary>
    /// Gets the output collector for the server's stdout/stderr, or <see langword="null"/> if
    /// <see cref="StartAsync"/> has not been called (or threw before the process was published).
    /// </summary>
    public OutputCollector? Output => _output;

    /// <summary>
    /// Gets the underlying server process for read-only observation by the backchannel polling
    /// loop, or <see langword="null"/> if <see cref="StartAsync"/> has not been called (or threw
    /// before the process was published).
    /// </summary>
    /// <remarks>
    /// This is intentionally narrow: the only legitimate consumer is
    /// <c>StartBackchannelConnectionAsync</c>'s <c>catch (SocketException) when (process.HasExited)</c>
    /// filter, which distinguishes "server died" from "server still starting up". Callers must not
    /// invoke <see cref="Process.Kill(bool)"/>, <see cref="Process.WaitForExitAsync(CancellationToken)"/>,
    /// or other lifecycle APIs on the returned instance — those belong to the session.
    /// </remarks>
    public Process? ServerProcess => _serverProcess;

    /// <summary>
    /// Launches the AppHost server process. The returned task completes with the process exit
    /// code when the process exits (either on its own, or because the stop token supplied to the
    /// constructor was cancelled and the session killed it).
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="StartAsync"/> has already been called.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the session has been disposed.</exception>
    public Task<int> StartAsync()
    {
        // Hold _startGate across the entire startup body — env build, _project.Run, field
        // publication, Exited wiring, and stop registration. DisposeAsync's top-of-method lock
        // then either runs before us (and StartAsync sees _disposed and throws) or after us
        // (and Dispose sees a fully-published process + registration). Without this widening
        // there is a window between _project.Run returning and the stop registration completing
        // where a concurrent Dispose would orphan the just-launched process. Every operation
        // below is synchronous, so a Monitor lock is safe (no await inside).
        lock (_startGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_startInvoked)
            {
                throw new InvalidOperationException("AppHostServerSession has already been started.");
            }

            _startInvoked = true;

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;

            var serverEnvironmentVariables = _callerEnvironmentVariables is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(_callerEnvironmentVariables);
            serverEnvironmentVariables[KnownConfigNames.RemoteAppHostToken] = _authenticationToken;

            _activity = _profilingTelemetry is null
                ? default
                : _profilingTelemetry.StartAppHostServerLifetime(_project.GetType().Name);
            if (_activity.IsRunning)
            {
                _activity.AddContextToEnvironment(serverEnvironmentVariables);
            }
            else
            {
                // Profiling may be disabled even when an upstream CLI span is active. Still pass that
                // ambient context through so the AppHostServer can join the existing startup trace.
                ProfilingTelemetry.AddCurrentContextToEnvironment(serverEnvironmentVariables);
            }

            AppHostServerRunResult result;
            try
            {
                result = _project.Run(
                    Environment.ProcessId,
                    serverEnvironmentVariables,
                    debug: _debug,
                    isolateConsole: _isolateConsole,
                    consoleProcessJob: _consoleProcessJob);
            }
            catch (Exception ex)
            {
                _activity.SetError(ex.Message);
                _activity.Dispose();
                (_project as IDisposable)?.Dispose();
                completion.TrySetException(ex);
                throw;
            }

            // Publish the lifetime BEFORE any further work so a fault in the wiring below still
            // routes the spawned process through normal DisposeAsync cleanup (which awaits the
            // shutdown ladder and then disposes the lifetime). Anything between here and the
            // stop registration that throws will fall into the catch below and tear the just-
            // launched process down explicitly.
            _processLifetime = result.ProcessLifetime;
            _serverProcess = result.Process;
            _socketPath = result.SocketPath;
            _output = result.OutputCollector;

            try
            {
                var process = result.Process;

                _activity.SetProcessId(process.Id);

                // Read identity from the result, not process.StartInfo: on the isolated Windows
                // path the Process is obtained via Process.GetProcessById and its StartInfo is
                // empty, so reading from there would lose the telemetry signal.
                _activity.SetProcessInvocation(result.FileName, result.Arguments);

                // Hook Exited before we check HasExited to close the race window where the process
                // could exit between Start returning and our subscription being wired up. Capture the
                // process + completion locals in the closure so the handler doesn't need to read
                // mutable fields when it fires from the thread pool.
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => TrySetExitCode(completion, process);

                // If the process exited before we hooked Exited (or before EnableRaisingEvents was set),
                // the event will not fire. Trip the completion ourselves in that case.
                if (process.HasExited)
                {
                    TrySetExitCode(completion, process);
                }

                // Register on the internal linked CTS. If the caller's token already fired (or DisposeAsync
                // raced us to Cancel) the registration's callback fires synchronously here, killing the
                // just-launched process. CT.Register handles already-cancelled tokens via inline invocation.
                _stopRegistration = _stopCts.Token.Register(static state => ((AppHostServerSession)state!).OnStopRequested(), this);
            }
            catch (Exception ex)
            {
                // Post-spawn wiring failed (Exited hook, HasExited probe, stop registration).
                // The lifetime is published, so DisposeAsync would normally clean it up — but the
                // caller is about to receive an exception from StartAsync and may not call
                // DisposeAsync. Tear the process down here so we don't leak the just-launched
                // child + (on the isolated Windows path) its anonymous pipes and NUL stdin handle.
                _activity.SetError(ex.Message);
                _activity.Dispose();
                try
                {
                    if (!result.Process.HasExited)
                    {
                        result.Process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Best effort — the lifetime disposal below handles the handles either way.
                }
                try
                {
                    // Synchronously wait for the disposal: StartAsync's contract is synchronous
                    // until the returned Task is observed, and the cleanup window for the freshly
                    // spawned child should be milliseconds (the kill above unblocks the pipes).
                    result.ProcessLifetime.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Best effort.
                }
                _processLifetime = null;
                _serverProcess = null;
                (_project as IDisposable)?.Dispose();
                completion.TrySetException(ex);
                throw;
            }

            return completion.Task;
        }
    }

    /// <summary>
    /// Returns an RPC client connected to the server. Must be called after <see cref="StartAsync"/>.
    /// </summary>
    public async Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var socketPath = _socketPath ?? throw NotStarted();
        return _rpcClient ??= await AppHostRpcClient.ConnectAsync(socketPath, _authenticationToken, _profilingTelemetry, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_startGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        // Trigger the registered stop callback (idempotent — if the caller's token already fired
        // earlier this is a no-op). The callback runs ProcessTerminator.ShutdownAsync, which is
        // synchronous when requestGracefulShutdown:false, so by the time Cancel returns the kill
        // signal has been sent.
        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS was already disposed by a prior partial teardown; the stop callback (if any)
            // would have fired then.
        }

        // Detach the registration before disposing the CTS so no late callback fires while the
        // rest of the teardown progresses.
        await _stopRegistration.DisposeAsync().ConfigureAwait(false);

        // Await the shutdown ladder before observing completion: when external stop fired, the
        // ladder is bounded by the central token (so it cannot hang past the central budget);
        // when dispose-only fired, the ladder force-killed near-instantly. Either way, awaiting
        // here keeps the kill sequence ordered ahead of completion + RPC teardown so the process
        // is dead (or definitely going to die) before we touch its handles.
        if (_shutdownTask is { } shutdownTask)
        {
            try
            {
                await shutdownTask.ConfigureAwait(false);
            }
            catch
            {
                // The ladder swallows expected errors internally; defensively swallow anything
                // else so disposal stays best-effort.
            }
        }

        if (_rpcClient is not null)
        {
            await _rpcClient.DisposeAsync().ConfigureAwait(false);
            _rpcClient = null;
        }

        // Observe the completion task unconditionally to prevent UnobservedTaskException if
        // StartAsync's _project.Run threw synchronously and faulted the completion before the
        // process handle was assigned. When the process did start, this also waits for the
        // Exited handler (or post-Run HasExited check) to flow the exit code through.
        if (_completion is { } completion)
        {
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            catch
            {
                // Exceptions surface to the StartAsync caller; swallow during disposal.
            }
        }

        if (_serverProcess is { } process)
        {
            if (process.HasExited)
            {
                _activity.SetProcessExitCode(process.ExitCode);
            }
        }

        // Dispose via the lifetime, not the process. On the isolated Windows path the lifetime is
        // the IsolatedProcess wrapper which drains stdout/stderr pumps (bounded by an internal 5s
        // timeout) and releases the anonymous pipes + NUL stdin handle that the Process doesn't
        // own. On the non-isolated path the lifetime is a thin adapter that just disposes the
        // Process. Either way, this is the single disposal site for the spawned child.
        if (_processLifetime is { } lifetime)
        {
            try
            {
                await lifetime.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the shutdown ladder has already run, so the process is exited or
                // being killed. Throwing from a disposal path is never useful.
            }
            _processLifetime = null;
        }

        _stopCts.Dispose();
        (_project as IDisposable)?.Dispose();
        _activity.Dispose();
    }

    private static void TrySetExitCode(TaskCompletionSource<int> completion, Process process)
    {
        try
        {
            completion.TrySetResult(process.ExitCode);
        }
        catch (InvalidOperationException)
        {
            // Process handle was closed concurrently (e.g., during DisposeAsync teardown). The
            // completion task is the contract callers observe; without an exit code there is
            // nothing meaningful to surface, so just leave the completion open — DisposeAsync
            // swallows the awaited exception.
        }
    }

    private void OnStopRequested()
    {
        // CT registration callbacks must be synchronous. We stash the started ShutdownAsync task
        // in _shutdownTask so DisposeAsync can await it instead of leaving it dangling. The kill
        // path inside ShutdownAsync is bounded (either an immediate force-kill on the no-graceful
        // path, or by the central GracefulShutdownService.Token on the graceful path), so the
        // task cannot hang past the central budget.
        //
        // Intentionally no `_disposed` check: DisposeAsync sets `_disposed = true` before calling
        // `_stopCts.Cancel()`, so an early return here would suppress the kill that disposal is
        // trying to trigger.
        //
        // Defense in depth against the helper being called more than once — a CT registration only
        // fires once today, but the kill path should never depend on that contract holding through
        // future refactors (e.g. if disposal grows additional cancel paths).
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        var process = _serverProcess;
        if (process is null)
        {
            return;
        }

        // _externalStopToken.IsCancellationRequested distinguishes "caller cancelled the external
        // token" (we're on the propagation path and the token reads as cancelled) from
        // "DisposeAsync cancelled the internal linked CTS" (the external token never fired).
        // Only the former should run the graceful ladder — the latter must force-kill because
        // nothing started the central GracefulShutdownService timer.
        _shutdownTask = ShutdownAsync(process, externalStopFired: _externalStopToken.IsCancellationRequested);
    }

    private async Task ShutdownAsync(Process process, bool externalStopFired)
    {
        // Force-kill path: take this when graceful infra isn't wired (non-Run callers — SDK gen,
        // scaffolding, publish, dump) OR when the dispose-only path was taken (external token
        // never fired). The dispose-only case is the critical reason for the externalStopFired
        // check: if we observed _shutdownService.Token in that case, the ladder would hang
        // indefinitely because nothing started the central timer.
        if (_gracefulShutdownSignaler is null || _shutdownService is null || !externalStopFired)
        {
            try
            {
                await ProcessTerminator.ShutdownAsync(
                    process,
                    requestGracefulShutdown: false,
                    entireProcessTree: !OperatingSystem.IsWindows(),
                    _logger,
                    ProcessDescription,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Process.Id can throw if the handle was disposed mid-shutdown — surface as -1
                // rather than masking the original failure with a secondary log exception.
                var pid = TryGetPid(process);
                _logger.LogWarning(ex, "Failed to shut down {ProcessDescription} (pid {Pid}).", ProcessDescription, pid);
            }
            return;
        }

        await ProcessGracefulShutdownLadder.ExecuteAsync(
            process,
            _gracefulShutdownSignaler,
            _shutdownService.Token,
            _logger,
            ProcessDescription).ConfigureAwait(false);
    }

    private static InvalidOperationException NotStarted() =>
        new($"{nameof(AppHostServerSession)} has not been started. Call {nameof(StartAsync)} first.");

    private static int TryGetPid(Process process)
    {
        try { return process.Id; }
        catch { return -1; }
    }
}
