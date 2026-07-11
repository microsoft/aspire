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
    private readonly IsolatedProcessStartInfo _startInfo;
    private Func<ValueTask> _disposeAsync = () => ValueTask.CompletedTask;
    private Func<int>? _exitCodeProvider;
    private Func<bool>? _hasExitedProvider;
    private Func<CancellationToken, Task>? _waitForExitProvider;
    private Process? _process;
    private int? _id;
    private DateTimeOffset? _startTime;
    private int _started;
    private int _disposed;

    public IsolatedProcess(IsolatedProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        _startInfo = startInfo;
        FileName = startInfo.FileName;
        Arguments = startInfo.ArgumentList.ToArray();
        StandardOutputClosed = Task.CompletedTask;
        StandardErrorClosed = Task.CompletedTask;
    }

    /// <summary>Raised for each line read from stdout.</summary>
    public event Action<IsolatedProcess, string>? OutputDataReceived;

    /// <summary>Raised for each line read from stderr.</summary>
    public event Action<IsolatedProcess, string>? ErrorDataReceived;

    /// <summary>The underlying <see cref="System.Diagnostics.Process"/> for escape-hatch scenarios.</summary>
    public Process Process => _process ?? throw new InvalidOperationException($"{nameof(IsolatedProcess)} has not been started.");

    /// <summary>
    /// The child's process id. Captured eagerly at spawn time because <see cref="Process.Id"/>
    /// throws once the underlying handle is closed, which can race a fast-exiting child.
    /// </summary>
    public int Id => _id ?? throw new InvalidOperationException($"{nameof(IsolatedProcess)} has not been started.");

    /// <summary>
    /// The original executable path. <see cref="ProcessStartInfo.FileName"/> on a
    /// <see cref="System.Diagnostics.Process"/> obtained from <see cref="Process.GetProcessById(int)"/>
    /// is empty, so callers (telemetry, error messages) read it from here.
    /// </summary>
    public string FileName { get; }

    /// <summary>The original argument list. Same rationale as <see cref="FileName"/>.</summary>
    public IReadOnlyList<string> Arguments { get; private set; }

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
    public Task StandardOutputClosed { get; private set; }

    /// <summary>Stderr counterpart of <see cref="StandardOutputClosed"/>.</summary>
    public Task StandardErrorClosed { get; private set; }

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
    /// Mirrors <see cref="Process.Kill(bool)"/>.
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

        return _disposeAsync();
    }

    /// <summary>
    /// Creates and starts an <see cref="IsolatedProcess"/> from <paramref name="startInfo"/>.
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="cancellationToken">Cancellation token for asynchronous launch work.</param>
    public static async Task<IsolatedProcess> StartAsync(
        IsolatedProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var process = new IsolatedProcess(startInfo);
        await process.StartAsync(cancellationToken).ConfigureAwait(false);

        return process;
    }

    /// <summary>
    /// Mirrors <see cref="Process.Start()"/> plus the launch knobs that require platform shims on the current target framework.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for asynchronous launch work.</param>
    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException($"{nameof(IsolatedProcess)} has already been started.");
        }

        StartedProcess startedProcess;
        if (_startInfo.Detached && !OperatingSystem.IsWindows())
        {
            startedProcess = await StartDetachedUnixAsync(_startInfo, cancellationToken).ConfigureAwait(false);
        }
        // Windows parent-exit protection requires the suspended-create / assign / resume ceremony in
        // StartWindows. Route protected helpers through that path even when they do not need a
        // graceful CTRL+C console group; otherwise use the ordinary redirected Process.Start shape.
        else if (OperatingSystem.IsWindows() && (_startInfo.IsolateConsole || _startInfo.KillOnParentExit || _startInfo.Detached))
        {
            startedProcess = StartWindows(_startInfo);
        }
        else
        {
            startedProcess = StartRedirected(_startInfo, redirectStandardInput: !_startInfo.IsolateConsole);
        }

        InitializeStartedProcess(startedProcess);
        return true;
    }

    /// <summary>
    /// Cross-platform redirected spawn: a thin <see cref="Process.Start(ProcessStartInfo)"/> wrapper.
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="redirectStandardInput">
    /// <see langword="true"/> wires stdin to an empty redirected pipe (the non-isolated shape every
    /// other CLI subprocess uses). <see langword="false"/> lets the child inherit the CLI's stdin
    /// (the isolated-Unix shape).
    /// </param>
    private static StartedProcess StartRedirected(
        IsolatedProcessStartInfo startInfo,
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

        return new StartedProcess(
            process,
            process.StandardOutput,
            process.StandardError,
            ExtraDispose: null);
    }

    /// <summary>
    /// Applies the already-started process and platform-specific handle/readers to this wrapper.
    /// </summary>
    private void InitializeStartedProcess(StartedProcess startedProcess)
    {
        _process = startedProcess.Process;
        _id = startedProcess.ProcessId ?? startedProcess.Process.Id;
        Arguments = _startInfo.ArgumentList.ToArray();
        _exitCodeProvider = startedProcess.ExitCodeProvider;
        _hasExitedProvider = startedProcess.HasExitedProvider;
        _waitForExitProvider = startedProcess.WaitForExitProvider;
        _startTime = startedProcess.UseProvidedStartTime ? startedProcess.StartTime : GetStartTime(startedProcess.Process);

        WireOutputPumps(startedProcess);
    }

    private void WireOutputPumps(StartedProcess startedProcess)
    {
        var process = startedProcess.Process;
        var standardOutput = startedProcess.StandardOutput;
        var standardError = startedProcess.StandardError;
        var extraDispose = startedProcess.ExtraDispose;

        var outputTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StandardOutputClosed = outputTcs.Task;
        StandardErrorClosed = errorTcs.Task;

        async ValueTask DisposeAsync()
        {
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

        _disposeAsync = DisposeAsync;

        // The pumps capture this wrapper as the handler's "sender", matching Process output event shape.
        var outputPump = ProcessPump.Start(standardOutput, line => OutputDataReceived?.Invoke(this, line));
        var errorPump = ProcessPump.Start(standardError, line => ErrorDataReceived?.Invoke(this, line));

        _ = ForwardPumpAsync(outputPump.Completion, outputTcs);
        _ = ForwardPumpAsync(errorPump.Completion, errorTcs);
    }

    private sealed record StartedProcess(
        Process Process,
        TextReader StandardOutput,
        TextReader StandardError,
        Func<ValueTask>? ExtraDispose,
        Func<int>? ExitCodeProvider = null,
        Func<bool>? HasExitedProvider = null,
        Func<CancellationToken, Task>? WaitForExitProvider = null,
        DateTimeOffset? StartTime = null,
        bool UseProvidedStartTime = false,
        int? ProcessId = null);

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
