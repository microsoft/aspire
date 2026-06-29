// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Cli.Processes;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

/// <summary>
/// Owns the lifetime of an AppHost server child process. Construction stashes configuration
/// (including the stop token) without launching anything; <see cref="StartAsync"/> launches the
/// process and wires lifecycle observation, and <see cref="WaitForExitAsync"/>
/// returns the task that completes with the process exit code.
/// </summary>
/// <remarks>
/// The session drives the child entirely through the <see cref="IProcessExecution"/> returned by
/// <see cref="IAppHostServerProject.RunAsync"/>. Termination is requested either by cancelling the
/// <c>stopRequested</c> token passed to the constructor, or by calling <see cref="DisposeAsync"/>.
/// Both routes cancel the same internal linked CTS, which the drive loop passes to
/// <see cref="IProcessExecution.WaitForExitAsync(CancellationToken)"/>; the execution runs the
/// shared shutdown ladder (graceful signal → bounded wait → tree-kill, or force-kill fallback)
/// from inside that call. The session itself never spawns or kills — there is exactly one shutdown
/// driver, and it lives in <see cref="ProcessExecution"/>.
/// </remarks>
internal sealed class AppHostServerSession : IAppHostServerSession
{
    private readonly IAppHostServerProject _project;
    private readonly Dictionary<string, string>? _callerEnvironmentVariables;
    private readonly bool _debug;
    private readonly IEnvironment _environment;
    private readonly ILogger _logger;
    private readonly ProfilingTelemetry? _profilingTelemetry;
    private readonly string _authenticationToken;
    private readonly CancellationTokenSource _stopCts;
    private readonly IProcessTreeGracefulShutdownSignaler? _gracefulShutdownSignaler;
    private readonly IGracefulShutdownWindow? _shutdownService;
    private readonly bool _isolateConsole;

    // Serializes StartAsync against DisposeAsync so a concurrent dispose cannot orphan a
    // just-spawned process (see StartAsync for the ordering guarantee). Never disposed: the
    // idempotency guard in DisposeAsync reads _disposed only AFTER acquiring this gate, so
    // disposing it would make a second DisposeAsync throw ObjectDisposedException from WaitAsync
    // before it could observe that the first dispose already ran. We never touch
    // AvailableWaitHandle, so SemaphoreSlim needs no disposal here.
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _startInvoked;
    private bool _disposed;

    private IProcessExecution? _execution;
    private Task? _runTask;
    private string? _socketPath;
    private OutputCollector? _output;
    private TaskCompletionSource<int>? _completion;
    private ProfilingTelemetry.ActivityScope _activity;
    private IAppHostRpcClient? _rpcClient;

    public AppHostServerSession(
        IAppHostServerProject project,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        IEnvironment environment,
        ILogger logger,
        ProfilingTelemetry? profilingTelemetry,
        IProcessTreeGracefulShutdownSignaler? gracefulShutdownSignaler,
        IGracefulShutdownWindow? shutdownService,
        bool isolateConsole,
        CancellationToken stopRequested)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _callerEnvironmentVariables = environmentVariables;
        _debug = debug;
        _environment = environment;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _profilingTelemetry = profilingTelemetry;
        _authenticationToken = TokenGenerator.GenerateToken();
        _gracefulShutdownSignaler = gracefulShutdownSignaler;
        _shutdownService = shutdownService;
        _isolateConsole = isolateConsole;

        // Linked CTS so caller-initiated cancellation AND DisposeAsync both flow through the same
        // stop trigger. The drive loop (DriveAsync) passes _stopCts.Token to WaitForExitAsync, and
        // the execution runs the shared shutdown ladder when that token fires. The graceful-vs-force
        // decision is made centrally inside the ladder off IGracefulShutdownWindow.IsEnabled — there
        // is no per-stop distinction here, and the ladder bounds the graceful wait by starting the
        // central clock itself, so even a dispose-only teardown cannot hang.
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
    /// Gets whether the underlying AppHost server process has exited, or <see langword="null"/>
    /// if <see cref="StartAsync"/> has not been called (or threw before the process was
    /// published). Routes through the <see cref="IProcessExecution"/>, which encapsulates the
    /// isolated Windows spawn quirk (the underlying Process is obtained via
    /// <see cref="System.Diagnostics.Process.GetProcessById(int)"/>); see
    /// https://github.com/dotnet/runtime/issues/45003.
    /// </summary>
    public bool? HasServerExited => _execution?.HasExited;

    /// <summary>
    /// Reads the AppHost server process's exit code if it has exited, or <see langword="null"/>
    /// if the server is still running, has not been started, or the exit code cannot be read.
    /// </summary>
    public int? TryGetServerExitCode()
    {
        if (_execution is not { HasExited: true } execution)
        {
            return null;
        }

        try
        {
            return execution.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the underlying server process id for read-only observation, or <see langword="null"/>
    /// if <see cref="StartAsync"/> has not been called (or threw before the process was published).
    /// Prefer <see cref="HasServerExited"/> for has-exited checks and
    /// <see cref="TryGetServerExitCode"/> for exit-code reads.
    /// </summary>
    public int? ServerProcessId => _execution?.ProcessId;

    /// <summary>
    /// Launches the AppHost server process and wires lifecycle observation. The returned task
    /// completes once the process has been spawned and its socket path and PID are published (the
    /// process then runs in the background). Use <see cref="WaitForExitAsync"/> to observe the exit
    /// code.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="StartAsync"/> has already been called.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the session has been disposed.</exception>
    public async Task StartAsync()
    {
        // Hold _startGate across the entire startup body — env build, _project.RunAsync, field
        // publication, and drive-loop start. DisposeAsync also acquires _startGate before flipping
        // _disposed, so it either runs before us (and StartAsync sees _disposed and throws) or after
        // us (and Dispose sees a fully-published execution + run task). Without this widening there
        // is a window between _project.RunAsync returning and the run task starting where a
        // concurrent Dispose would orphan the just-launched process.
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
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
                result = await _project.RunAsync(
                    Environment.ProcessId,
                    serverEnvironmentVariables,
                    additionalArgs: null,
                    debug: _debug,
                    runControl: new AppHostServerRunControl(_isolateConsole, _gracefulShutdownSignaler, _shutdownService)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _activity.SetError(ex.Message);
                _activity.Dispose();
                (_project as IDisposable)?.Dispose();
                // Trip the completion so DisposeAsync (which awaits it unconditionally) doesn't hang.
                completion.TrySetException(ex);
                throw;
            }

            // Publish the execution + socket, then immediately start the drive loop so the completion
            // is wired to the process before anything else. From here on a fault routes through normal
            // DisposeAsync cleanup: the session is held in `await using`, and DisposeAsync cancels the
            // stop CTS (driving the drive loop's WaitForExitAsync into the shutdown ladder, which trips
            // the completion) then disposes the execution — which now kills the still-running child if
            // the ladder never ran. The telemetry calls below cannot throw, so there is no post-spawn
            // fault path to hand-roll a kill for.
            _execution = result.Execution;
            _socketPath = result.SocketPath;
            _output = result.OutputCollector;

            // Start the drive loop. It owns the single WaitForExitAsync call, trips the completion
            // when the process exits (or is torn down), and never throws — so a fire-and-forget caller
            // cannot orphan a faulted task, and DisposeAsync can always observe completion.
            _runTask = DriveAsync(result.Execution, completion, _stopCts.Token);

            _activity.SetProcessId(result.Execution.ProcessId);

            // Read identity from the execution, not process.StartInfo: on the isolated Windows path the
            // Process is obtained via Process.GetProcessById and its StartInfo is empty, so the
            // execution captured these at spawn time instead.
            _activity.SetProcessInvocation(result.Execution.FileName, result.Execution.Arguments);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Returns the task that completes with the process exit code when the server exits — either on
    /// its own, or because the stop token supplied to the constructor was cancelled (or the session
    /// was disposed) and the shutdown ladder ran. Returns the same task on every call.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if <see cref="StartAsync"/> has not been called.</exception>
    public Task<int> WaitForExitAsync()
    {
        // The completion is the lifetime signal published by Start; DriveAsync trips it (never the
        // raw run task, which is hardened to never throw). Hand back the same task each call so a
        // caller can capture it, poll IsCompleted, and await it without spawning new tasks.
        return (_completion ?? throw new SessionNotStartedException()).Task;
    }

    // Owns the single WaitForExitAsync call for the child. On normal exit it trips the completion
    // with the exit code. On cancellation (external stop OR DisposeAsync), the execution runs the
    // shared shutdown ladder from inside WaitForExitAsync and ALWAYS rethrows OCE even when the
    // ladder cleanly exited the process — so we read the (now-exited or killed) exit code and trip
    // the completion with it. This preserves the "WaitForExitAsync always completes with an int, never
    // surfaces OCE" contract that GuestAppHostProject and the codegen path depend on.
    private static async Task DriveAsync(IProcessExecution execution, TaskCompletionSource<int> completion, CancellationToken stopToken)
    {
        try
        {
            completion.TrySetResult(await execution.WaitForExitAsync(stopToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            completion.TrySetResult(execution.HasExited ? execution.ExitCode : -1);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    /// <summary>
    /// Returns an RPC client connected to the server. Must be called after <see cref="StartAsync"/>.
    /// </summary>
    public async Task<IAppHostRpcClient> GetRpcClientAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_rpcClient is not null)
        {
            return _rpcClient;
        }

        var socketPath = _socketPath ?? throw new SessionNotStartedException();
        // _completion is published alongside _socketPath in Start, so a non-null socket
        // path guarantees the server-exit signal is available here.
        var serverExitTask = (_completion ?? throw new SessionNotStartedException()).Task;

        // ConnectAsync already retries until the RPC socket is available. Race it against the
        // server-exit signal instead of sleeping first, so fast startups connect immediately and
        // failed server launches surface as soon as the process exits. We race _completion rather
        // than Process.WaitForExitAsync because on the isolated Windows path the Process is
        // GetProcessById-derived and its lifetime getters are unreliable — the completion is
        // tripped from the IsolatedProcess wrapper that holds the original CreateProcess handle.
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connectTask = AppHostRpcClient.ConnectAsync(socketPath, _authenticationToken, _environment, _profilingTelemetry, connectCts.Token);
        var completedTask = await Task.WhenAny(connectTask, serverExitTask).ConfigureAwait(false);

        if (completedTask == connectTask)
        {
            // Stop the process-exit watcher once the RPC connection wins the race.
            connectCts.Cancel();
            ObserveFaultedTask(serverExitTask);
            _rpcClient = await connectTask.ConfigureAwait(false);
            return _rpcClient;
        }

        await serverExitTask.ConfigureAwait(false);
        // Stop the retrying connection attempt once the server has exited, then observe any
        // cancellation/failure it reports so the losing task cannot raise an unobserved exception.
        connectCts.Cancel();
        ObserveFaultedTask(connectTask);
        var exitCode = TryGetServerExitCode();
        throw new InvalidOperationException(
            exitCode is { } code
                ? $"AppHost server process exited before the RPC connection could be established. Exit code: {code}."
                : "AppHost server process exited before the RPC connection could be established.");
    }

    public async ValueTask DisposeAsync()
    {
        // Acquire _startGate the same way StartAsync does so dispose is serialized against an
        // in-flight start: we either flip _disposed before StartAsync acquires the gate (it then
        // throws ObjectDisposedException and spawns nothing) or after it has fully published the
        // execution + run task (we then tear that down below). The gate is released immediately;
        // the teardown runs outside it so a late StartAsync observes _disposed and bails fast.
        bool alreadyDisposed;
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            alreadyDisposed = _disposed;
            _disposed = true;
        }
        finally
        {
            _startGate.Release();
        }

        if (alreadyDisposed)
        {
            return;
        }

        // Cancel the stop trigger. This drives the run loop's WaitForExitAsync(_stopCts.Token)
        // into the shared shutdown ladder (graceful or force-kill, decided centrally), which then
        // rethrows OCE so DriveAsync trips the completion. Awaiting _runTask below therefore cannot
        // hang past the central budget.
        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // CTS was already disposed by a prior partial teardown.
        }

        // Await the drive loop before observing completion: it owns the single WaitForExitAsync,
        // so once it returns the process is exited (graceful) or killed (escalation) and the
        // completion has been tripped. Keeping this ordered ahead of RPC teardown ensures the
        // process is dead (or definitely dying) before we touch its handles.
        if (_runTask is { } runTask)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch
            {
                // DriveAsync never throws (it funnels faults into the completion), but swallow
                // defensively so disposal stays best-effort.
            }
        }

        if (_rpcClient is not null)
        {
            await _rpcClient.DisposeAsync().ConfigureAwait(false);
            _rpcClient = null;
        }

        // Observe the completion task unconditionally to prevent UnobservedTaskException if
        // StartAsync's _project.RunAsync faulted the completion before the execution was published.
        // When the process did start, DriveAsync has already tripped this.
        if (_completion is { } completion)
        {
            try
            {
                await completion.Task.ConfigureAwait(false);
            }
            catch
            {
                // Exceptions surface to the Start caller; swallow during disposal.
            }
        }

        if (_execution is { HasExited: true } execution)
        {
            try
            {
                _activity.SetProcessExitCode(execution.ExitCode);
            }
            catch
            {
                // Exit code unreadable (handle closed concurrently) — telemetry is best-effort.
            }
        }

        // Single disposal site for the spawned child. On the isolated Windows path the execution
        // drains stdout/stderr pumps (bounded internally) and releases the anonymous pipes + NUL
        // stdin handle the Process doesn't own. On the non-isolated path it disposes the Process.
        if (_execution is { } executionToDispose)
        {
            try
            {
                await executionToDispose.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the shutdown ladder has already run, so the process is exited or
                // being killed. Throwing from a disposal path is never useful.
            }
            _execution = null;
        }

        _stopCts.Dispose();
        (_project as IDisposable)?.Dispose();
        _activity.Dispose();
    }

    private static void ObserveFaultedTask(Task task)
    {
        _ = task.ContinueWith(
            completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class SessionNotStartedException() : InvalidOperationException(
        $"{nameof(AppHostServerSession)} has not been started. Call {nameof(StartAsync)} first.");
}

/// <summary>
/// Default <see cref="IAppHostServerSessionFactory"/> that constructs real
/// <see cref="AppHostServerSession"/> instances. The factory injects the ambient
/// <see cref="ILogger{AppHostServerSession}"/> and <see cref="ProfilingTelemetry"/>; callers supply
/// the per-session configuration, including the optional graceful-shutdown wiring used by the
/// <c>aspire run</c> path.
/// </summary>
internal sealed class AppHostServerSessionFactory : IAppHostServerSessionFactory
{
    private readonly IEnvironment _environment;
    private readonly ILogger<AppHostServerSession> _logger;
    private readonly ProfilingTelemetry _profilingTelemetry;

    public AppHostServerSessionFactory(IEnvironment environment, ILogger<AppHostServerSession> logger, ProfilingTelemetry profilingTelemetry)
    {
        _environment = environment;
        _logger = logger;
        _profilingTelemetry = profilingTelemetry;
    }

    public IAppHostServerSession Create(
        IAppHostServerProject appHostServerProject,
        Dictionary<string, string>? environmentVariables,
        bool debug,
        IProcessTreeGracefulShutdownSignaler? gracefulShutdownSignaler,
        IGracefulShutdownWindow? shutdownService,
        bool isolateConsole,
        CancellationToken stopRequested) =>
        new AppHostServerSession(
            appHostServerProject,
            environmentVariables,
            debug,
            _environment,
            _logger,
            _profilingTelemetry,
            gracefulShutdownSignaler,
            shutdownService,
            isolateConsole,
            stopRequested);
}
