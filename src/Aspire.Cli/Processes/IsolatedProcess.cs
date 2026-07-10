// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace Aspire.Cli.Processes;

/// <summary>
/// Describes process launch options that are not fully covered by <see cref="ProcessStartInfo"/>
/// on the target frameworks the CLI currently supports.
/// </summary>
internal sealed class IsolatedProcessStartInfo
{
    private Dictionary<string, string?>? _environment;

    /// <summary>Required. Executable path or PATH-resolved name.</summary>
    public required string FileName { get; init; }

    /// <summary>Required. Working directory for the child.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// The argument list. Mirrors <see cref="ProcessStartInfo.ArgumentList"/> — each entry
    /// is one logical argument, and the launcher handles per-OS quoting.
    /// </summary>
    public Collection<string> ArgumentList { get; } = new();

    /// <summary>
    /// Environment variables for the child. Lazily pre-populated with the current process's
    /// environment on first access — mirrors <see cref="ProcessStartInfo.Environment"/>.
    /// Add an entry to overlay, set an entry's value to <see langword="null"/> to remove it
    /// from the inherited block. Untouched entries are inherited verbatim.
    /// </summary>
    public IDictionary<string, string?> Environment => _environment ??= LoadParentEnvironment();

    /// <summary>
    /// When <see langword="true"/>, the child should be terminated when the parent exits.
    /// This mirrors the .NET 11 ProcessStartInfo.KillOnParentExit shape so the custom Windows
    /// job-object implementation can be replaced by the platform implementation later.
    /// </summary>
    public bool KillOnParentExit { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default) the child is spawned in its own hidden console
    /// group on Windows (CREATE_NEW_CONSOLE | SW_HIDE) so a graceful CTRL+C can target it without
    /// also signalling the CLI. When <see langword="false"/> the child
    /// is spawned via an ordinary redirected <see cref="Process.Start(ProcessStartInfo)"/> unless
    /// <see cref="KillOnParentExit"/> or <see cref="Detached"/> requires the Windows interop launcher.
    /// On Unix this flag does not affect process creation.
    /// </summary>
    public bool IsolateConsole { get; init; } = true;

    /// <summary>
    /// When <see langword="true"/>, launch the child outside the current process group/session on Unix.
    /// </summary>
    public bool Detached { get; init; }

    /// <summary>
    /// DCP executable used to create detached Unix process groups until <see cref="ProcessStartInfo"/>
    /// exposes this directly.
    /// </summary>
    public string? DetachedUnixLauncherPath { get; set; }

    /// <summary>
    /// Returns true when the caller has read or modified <see cref="Environment"/>. The
    /// spawn paths use this to decide whether to inherit the parent env verbatim
    /// (no-touch) or build a fresh custom env block from the dictionary.
    /// </summary>
    internal bool HasCustomEnvironment => _environment is not null;

    /// <summary>
    /// Internal accessor returning the environment dictionary in the read-only shape the
    /// Windows spawn primitive consumes. Returns <see langword="null"/> when the caller never
    /// touched <see cref="Environment"/>, signalling the spawn path to inherit the parent env
    /// verbatim (no allocation of an env block).
    /// </summary>
    internal IReadOnlyDictionary<string, string?>? GetEnvironmentForSpawn()
        => _environment;

    /// <summary>
    /// Initializes <see cref="Environment"/> as an empty block and returns it, skipping the
    /// lazy parent-environment snapshot that <see cref="Environment"/> would otherwise trigger.
    /// Use this for the "replace, don't overlay" path where the caller is about to write the
    /// child's entire environment from an authoritative source: seeding ~50-100 parent entries
    /// only to clear them immediately is pure waste. Marks <see cref="HasCustomEnvironment"/>
    /// so the spawn uses this explicit block rather than re-inheriting the parent.
    /// </summary>
    internal IDictionary<string, string?> UseEmptyEnvironment()
        => _environment = new Dictionary<string, string?>(ProcessEnvironment.Comparer);

    private static Dictionary<string, string?> LoadParentEnvironment()
    {
        // Snapshot the parent env on first access. ProcessStartInfo.Environment has the
        // same semantics — touching the property materializes the inherited block so the
        // caller can mutate it freely without affecting the parent process.
        return ProcessEnvironment.LoadParentEnvironment();
    }
}

/// <summary>
/// Mirrors the subset of <see cref="System.Diagnostics.Process"/> the CLI needs while compensating
/// for process launch features missing from the target framework.
/// </summary>
internal sealed partial class IsolatedProcess : IAsyncDisposable
{
    private readonly Func<TimeSpan, ValueTask> _disposeAsync;
    private readonly Func<int>? _exitCodeProvider;
    private readonly Func<bool>? _hasExitedProvider;
    private readonly Func<CancellationToken, Task>? _waitForExitProvider;
    private readonly DateTimeOffset? _startTime;
    private int _disposed;

    private IsolatedProcess(
        Process process,
        string fileName,
        IReadOnlyList<string> arguments,
        Task standardOutputClosed,
        Task standardErrorClosed,
        Func<TimeSpan, ValueTask> disposeAsync,
        Func<int>? exitCodeProvider,
        Func<bool>? hasExitedProvider,
        Func<CancellationToken, Task>? waitForExitProvider,
        DateTimeOffset? startTime)
    {
        Process = process;
        Id = process.Id;
        FileName = fileName;
        Arguments = arguments;
        StandardOutputClosed = standardOutputClosed;
        StandardErrorClosed = standardErrorClosed;
        _disposeAsync = disposeAsync;
        _exitCodeProvider = exitCodeProvider;
        _hasExitedProvider = hasExitedProvider;
        _waitForExitProvider = waitForExitProvider;
        _startTime = startTime;
    }

    /// <summary>The underlying <see cref="System.Diagnostics.Process"/> for escape-hatch scenarios.</summary>
    public Process Process { get; }

    /// <summary>
    /// The child's process id. Captured eagerly at spawn time because <see cref="Process.Id"/>
    /// throws once the underlying handle is closed, which can race a fast-exiting child.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The original executable path. <see cref="ProcessStartInfo.FileName"/> on a
    /// <see cref="System.Diagnostics.Process"/> obtained from <see cref="Process.GetProcessById(int)"/>
    /// is empty, so callers (telemetry, error messages) read it from here.
    /// </summary>
    public string FileName { get; }

    /// <summary>The original argument list. Same rationale as <see cref="FileName"/>.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Mirrors <see cref="Process.HasExited"/>. The Windows spawn path overrides this with a
    /// <c>WaitForSingleObject(handle, 0)</c> check against the kept <c>SafeProcessHandle</c>
    /// because <see cref="Process.HasExited"/> on a <see cref="Process.GetProcessById(int)"/>
    /// instance is unreliable for processes the managed Process didn't itself start.
    /// </summary>
    public bool HasExited => _hasExitedProvider?.Invoke() ?? Process.HasExited;

    /// <summary>
    /// Mirrors <see cref="Process.ExitCode"/>. The Windows spawn path overrides this with a
    /// <c>GetExitCodeProcess</c> call against the kept <c>SafeProcessHandle</c>; without the
    /// override <see cref="Process.ExitCode"/> throws <see cref="InvalidOperationException"/>
    /// for processes obtained via <see cref="Process.GetProcessById(int)"/> on Windows.
    /// See https://github.com/dotnet/runtime/issues/45003.
    /// </summary>
    public int ExitCode => _exitCodeProvider?.Invoke() ?? Process.ExitCode;

    public DateTimeOffset? StartTime => _startTime;

    /// <summary>
    /// Completes when the stdout pump finishes (pipe EOF — i.e. child closed the stream
    /// or exited). Faults if any stdout handler threw at any point during draining; the
    /// pump still drains to EOF before surfacing the fault so a hostile handler cannot
    /// back-pressure the child via a full pipe.
    /// </summary>
    public Task StandardOutputClosed { get; }

    /// <summary>Stderr counterpart of <see cref="StandardOutputClosed"/>.</summary>
    public Task StandardErrorClosed { get; }

    /// <summary>Mirrors <see cref="Process.WaitForExitAsync(CancellationToken)"/>.</summary>
    /// <remarks>
    /// The Windows spawn path overrides this to wait on the kept <c>CreateProcess</c> handle rather
    /// than the <see cref="Process.GetProcessById(int)"/> instance, whose
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/> can complete before the kernel marks
    /// the process exited — which would make an immediately-following <see cref="ExitCode"/> read
    /// throw. Routing the wait through the same kept handle as <see cref="ExitCode"/> /
    /// <see cref="HasExited"/> keeps them consistent. See https://github.com/dotnet/runtime/issues/45003.
    /// </remarks>
    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _waitForExitProvider?.Invoke(cancellationToken) ?? Process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Mirrors <see cref="Process.Kill(bool)"/>. Every consumer of this type needs tree-kill
    /// semantics (graceful-shutdown failures escalate to tree kill), so callers pass
    /// <see langword="true"/> explicitly.
    /// </summary>
    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // The caller is expected to have terminated the process by now, but we guard
        // against double-dispose anyway because this object can land in `using` blocks
        // and explicit cleanup paths at the same time during error recovery.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _disposeAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Mirrors <see cref="Process.Start(ProcessStartInfo)"/> plus the launch knobs that require
    /// platform shims on the current target framework.
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="standardOutputHandler">Per-line callback for stdout; receives the wrapper as sender.</param>
    /// <param name="standardErrorHandler">Per-line callback for stderr; receives the wrapper as sender.</param>
    /// <param name="cancellationToken">Cancellation token for asynchronous launch work.</param>
    /// <param name="launcherDiagnosticCallback">Receives diagnostics emitted by helper launchers used to compensate for missing process APIs.</param>
    public static Task<IsolatedProcess> StartAsync(
        IsolatedProcessStartInfo startInfo,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler,
        CancellationToken cancellationToken,
        Action<string>? launcherDiagnosticCallback = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardOutputHandler);
        ArgumentNullException.ThrowIfNull(standardErrorHandler);

        if (startInfo.Detached && !OperatingSystem.IsWindows())
        {
            return StartDetachedUnixAsync(startInfo, cancellationToken, launcherDiagnosticCallback);
        }

        // Windows parent-exit protection requires the suspended-create / assign / resume ceremony in
        // StartWindows. Route protected helpers through that path even when they do not need a
        // graceful CTRL+C console group; otherwise use the ordinary redirected Process.Start shape.
        if (OperatingSystem.IsWindows() && (startInfo.IsolateConsole || startInfo.KillOnParentExit || startInfo.Detached))
        {
            return Task.FromResult(StartWindows(startInfo, standardOutputHandler, standardErrorHandler));
        }

        return Task.FromResult(StartRedirected(startInfo, standardOutputHandler, standardErrorHandler, redirectStandardInput: !startInfo.IsolateConsole));
    }

    /// <summary>
    /// Cross-platform redirected spawn: a thin <see cref="Process.Start(ProcessStartInfo)"/>
    /// wrapper. Used for every non-isolated child (all platforms) and for isolated children on
    /// Unix, where SIGTERM / process groups handle cooperative shutdown so the new-console
    /// gymnastics the Windows partial uses are unnecessary. <see cref="IsolatedProcessStartInfo.KillOnParentExit"/>
    /// is ignored here until a cross-platform primitive is available.
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="redirectStandardInput">
    /// <see langword="true"/> wires stdin to an empty redirected pipe (the non-isolated shape every
    /// other CLI subprocess uses). <see langword="false"/> lets the child inherit the CLI's stdin
    /// (the isolated-Unix shape).
    /// </param>
    /// <param name="standardOutputHandler">Per-line callback for stdout; receives the wrapper as sender.</param>
    /// <param name="standardErrorHandler">Per-line callback for stderr; receives the wrapper as sender.</param>
    private static IsolatedProcess StartRedirected(
        IsolatedProcessStartInfo startInfo,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler,
        bool redirectStandardInput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = startInfo.FileName,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            // Pin encodings so process output decoding is stable regardless of the ambient
            // Console.OutputEncoding (e.g. on container hosts that leave it set to ASCII).
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
        };

        foreach (var arg in startInfo.ArgumentList)
        {
            psi.ArgumentList.Add(arg);
        }

        // Only mutate the ProcessStartInfo env block when the caller actually touched
        // IsolatedProcessStartInfo.Environment. Otherwise leave ProcessStartInfo to inherit
        // the parent's env verbatim — saves a snapshot-and-copy round trip for the common
        // case where nothing was customized.
        ProcessEnvironment.ApplyTo(psi, startInfo.GetEnvironmentForSpawn());

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start child process: {startInfo.FileName}");

        return WrapStartedProcess(
            startInfo,
            process,
            process.StandardOutput,
            process.StandardError,
            standardOutputHandler,
            standardErrorHandler,
            extraDispose: null);
    }

    /// <summary>
    /// Shared post-spawn wiring: build the <see cref="IsolatedProcess"/> wrapper, start
    /// the pumps with the wrapper bound as the handler "sender", and bridge pump
    /// completion to the wrapper's <see cref="StandardOutputClosed"/> /
    /// <see cref="StandardErrorClosed"/> tasks.
    /// </summary>
    /// <remarks>
    /// The wrapper must exist before the pumps start so the handlers can receive it as
    /// their "sender" parameter. The pumps must produce real Task completions before
    /// the wrapper exposes them on its <see cref="StandardOutputClosed"/>
    /// surface. We resolve the chicken-and-egg with a pair of <see cref="TaskCompletionSource"/>
    /// instances: the wrapper holds <c>tcs.Task</c>; pump completion drives the TCS via
    /// <see cref="ForwardPumpAsync"/>. No assignment race exists because the TCS task
    /// is fully constructed before the pumps start reading.
    /// </remarks>
    /// <param name="startInfo">The original start info — used for snapshotting FileName/Arguments onto the wrapper.</param>
    /// <param name="process">The already-started underlying <see cref="Process"/>.</param>
    /// <param name="standardOutput">Reader fed from the child's stdout pipe.</param>
    /// <param name="standardError">Reader fed from the child's stderr pipe.</param>
    /// <param name="standardOutputHandler">Per-line callback for stdout; receives the wrapper as sender.</param>
    /// <param name="standardErrorHandler">Per-line callback for stderr; receives the wrapper as sender.</param>
    /// <param name="extraDispose">
    /// Optional extra cleanup to run as part of <see cref="DisposeAsync"/> after the
    /// pump-drain window expires but before the wrapped <see cref="Process"/> is disposed.
    /// The Windows path uses this slot to dispose the anonymous pipes and the NUL stdin
    /// handle that are owned by the spawn path, not by the Process.
    /// </param>
    /// <param name="exitCodeProvider">
    /// Optional override for <see cref="ExitCode"/>. The Windows spawn path provides one
    /// because the managed <see cref="Process"/> returned by <see cref="Process.GetProcessById(int)"/>
    /// cannot reliably surface ExitCode for processes it did not itself start; the override
    /// reads the exit code via <c>GetExitCodeProcess</c> against the kept CreateProcess handle.
    /// Unix passes <see langword="null"/> — its <see cref="Process.Start(ProcessStartInfo)"/>
    /// path produces a Process with a working ExitCode getter.
    /// </param>
    /// <param name="hasExitedProvider">
    /// Optional override for <see cref="HasExited"/>. Same rationale as
    /// <paramref name="exitCodeProvider"/> — Windows reads via <c>WaitForSingleObject</c>
    /// against the kept handle; Unix uses the default <see cref="Process.HasExited"/>.
    /// </param>
    /// <param name="waitForExitProvider">
    /// Optional override for <see cref="WaitForExitAsync"/>. Same rationale as
    /// <paramref name="exitCodeProvider"/> — Windows waits on the kept handle so the wait stays
    /// consistent with the ExitCode/HasExited reads; Unix uses the default
    /// <see cref="Process.WaitForExitAsync(CancellationToken)"/>.
    /// </param>
    /// <param name="startTime">Optional cached child process start time.</param>
    private static IsolatedProcess WrapStartedProcess(
        IsolatedProcessStartInfo startInfo,
        Process process,
        TextReader standardOutput,
        TextReader standardError,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler,
        Func<ValueTask>? extraDispose,
        Func<int>? exitCodeProvider = null,
        Func<bool>? hasExitedProvider = null,
        Func<CancellationToken, Task>? waitForExitProvider = null,
        DateTimeOffset? startTime = null)
    {
        // Snapshot identity off startInfo now — the caller may mutate the startInfo after
        // we return and we don't want the wrapper to observe those changes.
        var argumentsSnapshot = startInfo.ArgumentList.ToArray();

        var outputTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ProcessPump? outputPump = null;
        ProcessPump? errorPump = null;

        async ValueTask DisposeAsync(TimeSpan drainTimeout)
        {
            // Give the pumps a bounded window to finish. They normally complete when the
            // child exits and the pipes hit EOF. If a caller disposes before the child
            // exits, the readers stay blocked until the OS tears the pipes down (Process
            // disposal on Unix, pipe disposal on Windows) — the timeout keeps us from
            // hanging on bugs.
            using var timeoutCts = new CancellationTokenSource(drainTimeout);
            try
            {
                if (outputPump is not null && errorPump is not null)
                {
                    await Task.WhenAll(outputPump.Completion, errorPump.Completion).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Drain timed out — fall through to extraDispose so it unblocks the pumps
                // by tearing the pipes down (Windows path). The TCSs are completed below
                // via the bridge once the pumps actually return.
            }
            catch
            {
                // Pump faults surface via StandardOutputClosed / StandardErrorClosed; swallow
                // here so dispose still tears the rest down cleanly.
            }

            if (extraDispose is not null)
            {
                try { await extraDispose().ConfigureAwait(false); } catch { }
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // Best effort.
            }
        }

        var isolated = new IsolatedProcess(
            process,
            startInfo.FileName,
            argumentsSnapshot,
            standardOutputClosed: outputTcs.Task,
            standardErrorClosed: errorTcs.Task,
            DisposeAsync,
            exitCodeProvider,
            hasExitedProvider,
            waitForExitProvider,
            startTime ?? GetStartTime(process));

        // The pumps capture 'isolated' as the handler's "sender". The assignment is fully
        // visible to the pump's Task.Run worker by happens-before semantics.
        outputPump = ProcessPump.Start(standardOutput, line => standardOutputHandler(isolated, line));
        errorPump = ProcessPump.Start(standardError, line => standardErrorHandler(isolated, line));

        _ = ForwardPumpAsync(outputPump.Completion, outputTcs);
        _ = ForwardPumpAsync(errorPump.Completion, errorTcs);

        return isolated;
    }

    private static DateTimeOffset? GetStartTime(Process process)
    {
        try
        {
            return ProcessStartTimeHelper.TryGetProcessStartTime(process.Id) ?? new DateTimeOffset(process.StartTime);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Bridges a <see cref="ProcessPump.Completion"/> task into a
    /// <see cref="TaskCompletionSource"/> the wrapper exposes. Preserves fault/cancel
    /// semantics so consumers of <see cref="StandardOutputClosed"/> see the same outcome
    /// the pump produced.
    /// </summary>
    private static async Task ForwardPumpAsync(Task pumpCompletion, TaskCompletionSource tcs)
    {
        try
        {
            await pumpCompletion.ConfigureAwait(false);
            tcs.TrySetResult();
        }
        catch (OperationCanceledException oce)
        {
            tcs.TrySetCanceled(oce.CancellationToken);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
    }

    /// <summary>
    /// Gives <see cref="IsolatedProcess"/> a Process-like line-output surface when platform
    /// launch shims own the stdio pipes.
    /// </summary>
    /// <remarks>
    /// Callback exceptions do NOT terminate the drain — the pump continues reading until
    /// EOF so that a verbose child cannot back-pressure into a full pipe and block on
    /// every subsequent write. The first exception is recorded and surfaced via the
    /// returned <see cref="Completion"/> task after the pump finishes draining.
    /// </remarks>
    private sealed class ProcessPump
    {
        private ProcessPump(Task completion)
        {
            Completion = completion;
        }

        /// <summary>Completes (or faults) when the underlying reader hits EOF.</summary>
        public Task Completion { get; }

        /// <summary>
        /// Starts a pump that reads lines from <paramref name="reader"/> and invokes
        /// <paramref name="onLine"/> for each non-null line. The pump runs on a background
        /// task and stops when the reader returns null (EOF) or throws.
        /// </summary>
        public static ProcessPump Start(TextReader reader, Action<string> onLine)
        {
            var completion = Task.Run(() => RunAsync(reader, onLine));
            return new ProcessPump(completion);
        }

        private static async Task RunAsync(TextReader reader, Action<string> onLine)
        {
            Exception? firstCallbackException = null;

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // The reader's underlying stream was torn down (typically because the
                    // child process was disposed while a read was in flight). Treat as EOF
                    // and let the pump exit cleanly — surfacing a previously-recorded
                    // callback exception at this point would just confuse error attribution.
                    return;
                }
                catch (IOException)
                {
                    // The pipe was broken — Windows surfaces "pipe is broken" / Unix surfaces
                    // EBADF when the underlying handle is reaped during a read. Treat as EOF.
                    return;
                }

                if (line is null)
                {
                    break;
                }

                try
                {
                    onLine(line);
                }
                catch (Exception ex) when (firstCallbackException is null)
                {
                    // Record but keep draining — the pipe MUST be drained so the child can
                    // continue to write without blocking. The recorded exception is
                    // re-thrown after EOF so callers can observe it via Completion.
                    firstCallbackException = ex;
                }
                catch
                {
                    // Subsequent callback failures are dropped; the first one is enough
                    // signal for callers.
                }
            }

            if (firstCallbackException is not null)
            {
                // Preserve the original stack trace when faulting the pump task.
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstCallbackException).Throw();
            }
        }
    }

}
