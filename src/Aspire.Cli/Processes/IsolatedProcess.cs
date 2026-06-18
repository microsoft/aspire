// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aspire.Cli.Processes;

/// <summary>
/// Mirrors <see cref="ProcessStartInfo"/>. Differences from the BCL shape:
/// stdout/stderr are always redirected (so there is no <c>RedirectStandardOutput</c>
/// flag — see <see cref="IsolatedProcess.Start"/>), and <see cref="JobHandle"/> adds
/// the Windows-only kill-on-close safety net described in
/// <see cref="WindowsConsoleProcessJob"/>.
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
    /// Windows-only crash-time safety net. When set, the spawned child is atomically
    /// assigned to this job object via the suspended-create / assign / resume dance in
    /// <see cref="WindowsProcessInterop.SpawnConsoleIsolatedProcess"/>. Set to
    /// <see cref="WindowsConsoleProcessJob.Handle"/> from <see cref="WindowsConsoleProcessJob.Shared"/>
    /// on Windows hosts; <see langword="null"/> on non-Windows hosts (Unix process-group
    /// semantics cover the equivalent case).
    /// </summary>
    public SafeFileHandle? JobHandle { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default) the child is spawned in its own hidden console
    /// group on Windows (CREATE_NEW_CONSOLE | SW_HIDE) so DCP's <c>stop-process-tree</c> CTRL+C
    /// dance can target it without also signalling the CLI. When <see langword="false"/> the child
    /// is spawned via an ordinary redirected <see cref="Process.Start(ProcessStartInfo)"/> — no new
    /// console, no <see cref="JobHandle"/>, and stdin wired to an empty pipe — which is the shape
    /// every non-isolated CLI subprocess (build, restore, package add, …) uses. On Unix both modes
    /// are identical because SIGTERM via the process group covers teardown, so only Windows branches
    /// on this flag.
    /// </summary>
    public bool IsolateConsole { get; init; } = true;

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

    private static Dictionary<string, string?> LoadParentEnvironment()
    {
        // Snapshot the parent env on first access. ProcessStartInfo.Environment has the
        // same semantics — touching the property materializes the inherited block so the
        // caller can mutate it freely without affecting the parent process.
        var parent = System.Environment.GetEnvironmentVariables();
        // OrdinalIgnoreCase mirrors ProcessStartInfo's behavior on Windows (env vars are
        // case-insensitive). Using it on all platforms is slightly less strict than the
        // Unix kernel (which treats env names as bytes) but it matches what ProcessStartInfo
        // does and prevents the trap of accidentally having both "Path" and "PATH" entries.
        var dict = new Dictionary<string, string?>(parent.Count, StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in parent)
        {
            dict[(string)entry.Key] = entry.Value as string;
        }
        return dict;
    }
}

/// <summary>
/// Mirrors <see cref="System.Diagnostics.Process"/> for a child spawned by <see cref="Start"/>.
/// On Windows the child gets its own hidden console (CREATE_NEW_CONSOLE | SW_HIDE) so DCP's
/// <c>stop-process-tree</c> can <c>AttachConsole</c> + post <c>CTRL_C_EVENT</c> at it without
/// also signalling the CLI; on Unix it's a thin <see cref="Process.Start(ProcessStartInfo)"/>
/// wrapper because SIGTERM via the process group is enough.
/// </summary>
/// <remarks>
/// Differences from the BCL shape worth knowing about:
/// <list type="bullet">
///   <item>Stdout/stderr handlers are required at <see cref="Start"/> time — no
///   <c>OutputDataReceived</c> event you can forget to subscribe, no <c>BeginOutputReadLine</c>
///   to forget to call. Handlers receive <c>(sender, line)</c>, mirroring
///   <see cref="DataReceivedEventHandler"/>.</item>
///   <item><see cref="StandardOutputClosed"/> / <see cref="StandardErrorClosed"/> are separate
///   from <see cref="WaitForExitAsync"/> so callers can wait for the pipes to fully drain after
///   the child exits — <see cref="Process.WaitForExit()"/> can return with data still queued.</item>
/// </list>
/// </remarks>
internal sealed partial class IsolatedProcess : IAsyncDisposable
{
    private readonly Func<TimeSpan, ValueTask> _disposeAsync;
    private readonly Func<int>? _exitCodeProvider;
    private readonly Func<bool>? _hasExitedProvider;
    private int _disposed;

    private IsolatedProcess(
        Process process,
        string fileName,
        IReadOnlyList<string> arguments,
        Task standardOutputClosed,
        Task standardErrorClosed,
        Func<TimeSpan, ValueTask> disposeAsync,
        Func<int>? exitCodeProvider,
        Func<bool>? hasExitedProvider)
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
    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => Process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Mirrors <see cref="Process.Kill(bool)"/>. Defaults to <see langword="true"/>
    /// because every consumer of this type needs tree-kill semantics
    /// (graceful-shutdown failures escalate to tree kill).
    /// </summary>
    public void Kill(bool entireProcessTree = true) => Process.Kill(entireProcessTree);

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
    /// Mirrors <see cref="Process.Start(ProcessStartInfo)"/>. Spawns the child, wires the
    /// handlers, and starts the stdout/stderr pumps before returning. Throws if the child
    /// fails to spawn.
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="standardOutputHandler">
    /// Invoked once per line read from the child's stdout on a background pump. A throw
    /// is captured and surfaced via <see cref="StandardOutputClosed"/>; the pump keeps
    /// draining so the child cannot back-pressure on a full pipe.
    /// </param>
    /// <param name="standardErrorHandler">Stderr counterpart of <paramref name="standardOutputHandler"/>.</param>
    public static IsolatedProcess Start(
        IsolatedProcessStartInfo startInfo,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(standardOutputHandler);
        ArgumentNullException.ThrowIfNull(standardErrorHandler);

        // Non-isolated mode is an ordinary redirected Process.Start on every platform: no new
        // console, no JobHandle, stdin wired to an empty pipe. On Unix the isolated mode is the
        // same redirected spawn (SIGTERM via the process group is enough), so only the isolated
        // Windows path needs the dedicated new-console launcher.
        if (!startInfo.IsolateConsole)
        {
            return StartRedirected(startInfo, standardOutputHandler, standardErrorHandler, redirectStandardInput: true);
        }

        if (OperatingSystem.IsWindows())
        {
            return StartWindows(startInfo, standardOutputHandler, standardErrorHandler);
        }

        return StartRedirected(startInfo, standardOutputHandler, standardErrorHandler, redirectStandardInput: false);
    }

    /// <summary>
    /// Cross-platform redirected spawn — a thin <see cref="Process.Start(ProcessStartInfo)"/>
    /// wrapper. Used for every non-isolated child (all platforms) and for isolated children on
    /// Unix, where SIGTERM / process groups handle cooperative shutdown so the new-console
    /// gymnastics the Windows partial uses are unnecessary. <see cref="IsolatedProcessStartInfo.JobHandle"/>
    /// is ignored here (Unix process-group reparenting + signal delivery cover the crash-time case
    /// that JobHandle exists to address on Windows).
    /// </summary>
    /// <param name="startInfo">Process launch parameters.</param>
    /// <param name="standardOutputHandler">Per-line callback for stdout; receives the wrapper as sender.</param>
    /// <param name="standardErrorHandler">Per-line callback for stderr; receives the wrapper as sender.</param>
    /// <param name="redirectStandardInput">
    /// <see langword="true"/> wires stdin to an empty redirected pipe (the non-isolated shape every
    /// other CLI subprocess uses). <see langword="false"/> lets the child inherit the CLI's stdin
    /// (the isolated-Unix shape).
    /// </param>
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
            // Pin encodings so the new pump matches the existing ProcessGuestLauncher behavior
            // regardless of the ambient Console.OutputEncoding (e.g. on container hosts that
            // leave it set to ASCII).
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
        if (startInfo.HasCustomEnvironment)
        {
            psi.Environment.Clear();
            foreach (var (key, value) in startInfo.Environment)
            {
                // Match ProcessStartInfo.Environment semantics: a null value means "do not
                // set this variable in the child" — we get there by simply not adding it.
                if (value is not null)
                {
                    psi.Environment[key] = value;
                }
            }
        }

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
    private static IsolatedProcess WrapStartedProcess(
        IsolatedProcessStartInfo startInfo,
        Process process,
        TextReader standardOutput,
        TextReader standardError,
        Action<IsolatedProcess, string> standardOutputHandler,
        Action<IsolatedProcess, string> standardErrorHandler,
        Func<ValueTask>? extraDispose,
        Func<int>? exitCodeProvider = null,
        Func<bool>? hasExitedProvider = null)
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
            hasExitedProvider);

        // The pumps capture 'isolated' as the handler's "sender". The assignment is fully
        // visible to the pump's Task.Run worker by happens-before semantics.
        outputPump = ProcessPump.Start(standardOutput, line => standardOutputHandler(isolated, line));
        errorPump = ProcessPump.Start(standardError, line => standardErrorHandler(isolated, line));

        _ = ForwardPumpAsync(outputPump.Completion, outputTcs);
        _ = ForwardPumpAsync(errorPump.Completion, errorTcs);

        return isolated;
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
}
