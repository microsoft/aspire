// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
#if !NET11_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Threading.Channels;
#endif
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Shared process launch, identity, output forwarding, and handle ownership. Wraps a <see cref="Process"/> for
/// isolated-console, kill-on-parent-exit, detached, and ordinary redirected subprocesses. The child is
/// spawned lazily on <see cref="IChildProcess.StartAsync"/> so callers that build an execution but never start it (e.g.
/// the extension-host launch path, which reads <see cref="Arguments"/> /
/// <see cref="EnvironmentVariables"/> and returns before starting) don't orphan a process.
/// </summary>
internal partial class ChildProcess : IChildProcess
{
    private static readonly TimeSpan s_drainPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ProcessStartInfo _startInfo;
    protected readonly ILogger _logger;
    private readonly ChildProcessOptions _options;
    private readonly bool _isWindows;
    private readonly bool _isSupervisor;
    private readonly Lock _lifecycleLock = new();
    private Process? _process;
    private int _processId;
    private DateTimeOffset? _startTime;
    private Task _outputDrained = Task.CompletedTask;
    private bool _disposed;
    private long _lastActivityTimestamp;
    private DirectoryInfo? _completionDirectory;
    private string? _completionPath;

    internal ChildProcess(
        ProcessStartInfo startInfo,
        ILogger logger,
        ChildProcessOptions options,
        bool isWindows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.OutputDrainIdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.TerminationTimeout, TimeSpan.Zero);
        _startInfo = startInfo;
        _logger = logger;
        _options = options;
        _isWindows = isWindows;
        ArgumentException.ThrowIfNullOrEmpty(startInfo.FileName);
        if (options.Detached && options.Lifetime == ChildProcessLifetime.OwnedTree)
        {
            throw new ArgumentException("A detached process cannot belong to its launcher's owned tree.", nameof(options));
        }
        _isSupervisor = options.Lifetime == ChildProcessLifetime.OwnedTree ||
            startInfo.Environment.ContainsKey(ProcessSupervisor.CommandVariable);
        _lastActivityTimestamp = options.TimeProvider.GetTimestamp();
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
    public int ExitCode => GetExitCode(Process);

    /// <inheritdoc />
    public DateTimeOffset? StartTime
    {
        get
        {
            _ = Process;
            return _startTime;
        }
    }

    protected Process Process =>
        Volatile.Read(ref _process)
        ?? throw new InvalidOperationException($"{nameof(ChildProcess)} has not been started. Call {nameof(StartAsync)} first.");

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
                throw new InvalidOperationException($"{nameof(ChildProcess)} has already been started.");
            }

            var startInfo = _startInfo;
            if (_options.Lifetime == ChildProcessLifetime.OwnedTree)
            {
                _completionPath = _options.CompletionPath;
                if (_completionPath is null)
                {
                    _completionDirectory = Directory.CreateTempSubdirectory("aspire-process-completion-");
                    _completionPath = Path.Combine(_completionDirectory.FullName, "exit-code");
                }
                startInfo = _options.CreateSupervisorStartInfo(_startInfo, _completionPath, _options.TerminationTimeout);
            }

            // Children never consume input from their owner. A null stdin makes tools such as
            // package-manager lifecycle scripts observe EOF instead of inheriting the TTY and blocking
            // indefinitely (https://github.com/microsoft/aspire/issues/16791). A detached child
            // outlives the CLI, so nothing would be left to drain redirected output either.
#if NET11_0_OR_GREATER
            using var nullHandle = File.OpenNullHandle();
            startInfo.StandardInputHandle = nullHandle;
            if (_options.Detached)
            {
                startInfo.StandardOutputHandle = nullHandle;
                startInfo.StandardErrorHandle = nullHandle;
            }
#else
            // AppHost servers target net10, which does not expose standard-handle assignment.
            // Closing the redirected writer gives package-manager lifecycle scripts the same EOF.
            if (_options.Detached)
            {
                throw new NotSupportedException("Detached process execution requires .NET 11.");
            }
            startInfo.RedirectStandardInput = true;
#endif

            // Process.Start() only returns null for UseShellExecute, which is never used here.
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start child process: {_startInfo.FileName}");
            _processId = process.Id;
            _startTime = GetStartTime(process);
            Volatile.Write(ref _process, process);
#if !NET11_0_OR_GREATER
            // Publish ownership before closing stdin: a pipe-close failure must still leave
            // disposal able to terminate the child and release its handles.
            process.StandardInput.Close();
#endif

            // Publish the process before reading output so callbacks can read ProcessId.
            if (!_options.Detached && (startInfo.RedirectStandardOutput || startInfo.RedirectStandardError))
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
    /// Reading the pipes directly, unlike <see cref="Process.BeginOutputReadLine"/>, means
    /// <see cref="Process.WaitForExitAsync"/> does not
    /// also wait for EOF, which a grandchild holding the inherited pipe (e.g. a build server) can
    /// delay indefinitely. <see cref="DrainOutputAsync"/> bounds the wait for EOF instead.
    /// </remarks>
    private async Task ReadOutputAsync(Process process)
    {
        Exception? firstCallbackException = null;
        try
        {
#if NET11_0_OR_GREATER
            var lines = process.ReadAllLinesAsync(CancellationToken.None);
#else
            var lines = ReadAllLinesAsync(process);
#endif
            await foreach (var line in lines.ConfigureAwait(false))
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
    public async Task<int> WaitForRootExitAsync(CancellationToken cancellationToken)
    {
        var process = Process;
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return GetExitCode(process);
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
        catch (OperationCanceledException cancellationFailure)
        {
            _logger.LogDebug("{FileName}({ProcessId}) wait was canceled, stopping it", FileName, _processId);

            try
            {
                await ShutdownOnCancelAsync(process).ConfigureAwait(false);
            }
            catch (Exception terminationFailure)
            {
                _logger.LogError(terminationFailure, "Failed to stop {FileName}({ProcessId}) after cancellation.", FileName, _processId);
                throw new AggregateException(
                    $"Cancelled execution of {FileName} could not complete shutdown.",
                    cancellationFailure, terminationFailure);
            }

            // The child has now been signalled/killed by the coordinator. Drain trailing stdout/stderr
            // before propagating the cancellation so callers that observe output — or that swallow the
            // OCE and read ExitCode (e.g. the guest launcher distinguishing user-cancel from internal
            // teardown) — still get the full tail. Use a detached token + reset idle window so the drain
            // gets its whole budget even though the caller's token is already cancelled.
            RecordActivity();
            await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);

            throw;
        }

        var exitCode = GetExitCode(process);
        if (_isSupervisor)
        {
            await VerifyContainedTerminationAsync(process).ConfigureAwait(false);
        }
        _logger.LogDebug("{FileName}({ProcessId}) exited with code: {ExitCode}", FileName, _processId, exitCode);

        // Reset the idle window at exit so the drain budget is measured from "process gone", not
        // from the last line read. A consumer can block in a callback right up to exit and still
        // get the full tail — see
        // ChildProcessTests.WaitForExitAsync_DrainsBufferedTailAfterLongIdlePeriod.
        RecordActivity();
        await DrainOutputAsync(cancellationToken).ConfigureAwait(false);

        return exitCode;
    }

    private int GetExitCode(Process process)
    {
        if (_completionPath is null)
        {
            return process.ExitCode;
        }
        if (File.Exists(_completionPath))
        {
            return int.Parse(File.ReadAllText(_completionPath), CultureInfo.InvariantCulture);
        }
        if (process.ExitCode == 0)
        {
            _logger.LogError("Supervisor for {FileName}({ProcessId}) exited without reporting command completion.", FileName, _processId);
            throw new InvalidOperationException($"Supervisor for '{FileName}' exited without reporting command completion.");
        }

        return process.ExitCode;
    }

    private async Task ShutdownOnCancelAsync(Process process)
    {
        if (_options.RequestGracefulShutdownAsync is { } requestShutdown
            && _options.BeginGracefulShutdown?.Invoke() is { } gracefulToken)
        {
            await ShutdownLadderAsync(process, requestShutdown, gracefulToken).ConfigureAwait(false);
        }
        else
        {
            ForceKillChild(process);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(
                _options.TerminationTimeout, _options.TimeProvider, CancellationToken.None).ConfigureAwait(false);
        }

        if (_isSupervisor)
        {
            await VerifyContainedTerminationAsync(process).ConfigureAwait(false);
        }
    }

    private async Task ShutdownLadderAsync(Process process, Func<int, CancellationToken, Task> requestShutdown, CancellationToken gracefulToken)
    {
        // Signalling can itself await exit. Dispatch it concurrently with the same shared
        // command budget, and observe it even when the child exits before dispatch finishes.
        var signalTask = InvokeSignalerAsync(requestShutdown, _processId, gracefulToken);
        try
        {
            try
            {
                await process.WaitForExitAsync(gracefulToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
            {
            }
            if (!process.HasExited)
            {
                KillOwnedProcess(process, entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(
                    _options.TerminationTimeout, _options.TimeProvider, CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await signalTask.WaitAsync(_options.TerminationTimeout, _options.TimeProvider, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(ex, "Graceful shutdown signalling for {FileName}({ProcessId}) did not complete within {Timeout}.",
                    FileName, _processId, _options.TerminationTimeout);
                // Retain observation after the bounded drain without delaying child cleanup.
                // InvokeSignalerAsync catches and logs failures, including after this drain.
            }
        }
    }

    private async Task InvokeSignalerAsync(Func<int, CancellationToken, Task> signaler, int pid, CancellationToken gracefulToken)
    {
        try
        {
            await Task.Yield();
            await signaler(pid, gracefulToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (gracefulToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to issue graceful shutdown to {FileName} (pid {Pid}); escalating to kill.", FileName, pid);
        }
    }

    private static void KillOwnedProcess(Process process, bool entireProcessTree)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }

    private async Task VerifyContainedTerminationAsync(Process process)
    {
        try
        {
            if (!_isWindows)
            {
                RequestGroupTermination(process);
            }
            // Before setsid succeeds, no group exists. Reap the root as well, then signal
            // again to cover a guardian establishing its group during that startup race.
            KillOwnedProcess(process, entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(_options.TerminationTimeout, _options.TimeProvider).ConfigureAwait(false);
            if (!_isWindows)
            {
                RequestGroupTermination(process);
                using var deadline = new CancellationTokenSource(_options.TerminationTimeout, _options.TimeProvider);
                try
                {
                    while (ProcessGroupExists(_processId))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(25), _options.TimeProvider, deadline.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
                {
                    throw new TimeoutException($"Process group {_processId} still exists after cleanup timeout {_options.TerminationTimeout}.", ex);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to verify cleanup of {FileName}({ProcessId}) within {Timeout}.",
                FileName, _processId, _options.TerminationTimeout);
            throw;
        }
    }

    internal static void KillProcessGroup(int processGroupId)
    {
        // Negative PID targets the POSIX group, including orphaned descendants. ESRCH
        // means it is already gone: https://pubs.opengroup.org/onlinepubs/9799919799/functions/kill.html
        if (kill(-processGroupId, 9) != 0 && Marshal.GetLastPInvokeError() != 3)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Could not terminate process group {processGroupId}.");
        }
    }

    private void RequestGroupTermination(Process process)
    {
        try
        {
            KillProcessGroup(_processId);
        }
        catch (Win32Exception ex) when (OperatingSystem.IsMacOS() && ex.NativeErrorCode == 1 && process.HasExited)
        {
            // Darwin can return EPERM during orphan reaping. Only ESRCH in the
            // bounded group-exit loop establishes cleanup, not permission failure.
            _logger.LogDebug(ex, "Waiting for exited process group {ProcessId} to disappear.", _processId);
        }
    }

    private static bool ProcessGroupExists(int processGroupId)
    {
        if (kill(-processGroupId, 0) == 0)
        {
            return true;
        }
        return Marshal.GetLastPInvokeError() switch
        {
            1 => true,
            3 => false,
            var error => throw new Win32Exception(error, $"Could not verify termination of process group {processGroupId}.")
        };
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    private void ForceKillChild(Process process)
    {
        // Force-tree cleanup must not signal the root first: it could exit and reparent
        // descendants before the tree walk. Root-only Unix cleanup retains its courtesy
        // SIGTERM; graceful tree shutdown uses the separate signaler-backed ladder.
        var entireProcessTree = _options.KillEntireProcessTreeOnCancel;
        try
        {
            if (process.HasExited)
            {
                _logger.LogDebug("{FileName} process {ProcessId} already exited.", FileName, process.Id);
                return;
            }

            if (!_isWindows && !entireProcessTree)
            {
                // A root-only courtesy signal can reparent workers before the subsequent tree
                // walk. Force-tree cleanup must terminate descendants while their root is alive.
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
            _logger.LogError(
                ex,
                "Failed to terminate {FileName} process (entireProcessTree={EntireProcessTree}).",
                FileName,
                entireProcessTree);
            throw;
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
            _completionDirectory?.Delete(recursive: true);
            return;
        }

        try
        {
            if (_options.Lifetime == ChildProcessLifetime.OwnedTree && !process.HasExited)
            {
                await ShutdownOnCancelAsync(process).ConfigureAwait(false);
            }
            else if (_isSupervisor)
            {
                // Contained commands can finish cooperatively before their group is retired.
                // The same execution owns cleanup on cancellation, launch failure, and disposal.
                try
                {
                    await process.WaitForExitAsync().WaitAsync(_options.TerminationTimeout, _options.TimeProvider).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("{FileName}({ProcessId}) did not stop within {Timeout}; terminating its process scope.",
                        FileName, _processId, _options.TerminationTimeout);
                }
                await VerifyContainedTerminationAsync(process).ConfigureAwait(false);
            }
            else if (!process.HasExited)
            {
                await ShutdownOnCancelAsync(process).ConfigureAwait(false);
            }
            await DrainOutputAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            process.Dispose();
            _completionDirectory?.Delete(recursive: true);
        }
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
        if (!_isSupervisor || !ProcessSupervisorLogger.TryForward(line, _logger))
        {
            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("{FileName}({ProcessId}) stderr: {Line}", FileName, _processId, line);
            }
            _options.StandardErrorCallback?.Invoke(line);
        }
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
            if (_options.TimeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivityTimestamp)) >= _options.OutputDrainIdleTimeout)
            {
                _logger.LogWarning("{FileName}({ProcessId}) stdout/stderr did not drain within idle timeout {Timeout} after exit",
                    FileName, _processId, _options.OutputDrainIdleTimeout);
                return;
            }

            try
            {
                // Completion wakes the waiter immediately, including when a test clock is frozen.
                await drained.WaitAsync(s_drainPollInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Recheck the idle budget; a completed reader's failure is observed above.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception) when (drained.IsCompleted)
            {
                // Observe and log the reader failure on the next iteration.
            }
        }
    }

    private void RecordActivity() => Interlocked.Exchange(ref _lastActivityTimestamp, _options.TimeProvider.GetTimestamp());

#if !NET11_0_OR_GREATER
    private static async IAsyncEnumerable<OutputLine> ReadAllLinesAsync(Process process, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // net10 has no Process.ReadAllLinesAsync. Merge the pipes before invoking callbacks,
        // preserving the CLI runner's serialized callback contract and avoiding pipe-buffer deadlock.
        var lines = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false
        });
        using var readersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var readers = CompleteAsync();
        try
        {
            await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
            await readers.ConfigureAwait(false);
        }
        finally
        {
            readersCancellation.Cancel();
        }

        async Task CompleteAsync()
        {
            try
            {
                await Task.WhenAll(
                    process.StartInfo.RedirectStandardOutput ? ReadAsync(process.StandardOutput, false) : Task.CompletedTask,
                    process.StartInfo.RedirectStandardError ? ReadAsync(process.StandardError, true) : Task.CompletedTask).ConfigureAwait(false);
                lines.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                // Completion propagates read failures to the single consumer; this task never faults
                // unobserved when disposal interrupts an enumerator blocked on inherited pipes.
                lines.Writer.TryComplete(ex);
            }
        }

        async Task ReadAsync(StreamReader reader, bool standardError)
        {
            while (await reader.ReadLineAsync(readersCancellation.Token).ConfigureAwait(false) is { } line)
            {
                await lines.Writer.WriteAsync(new OutputLine(line, standardError), readersCancellation.Token).ConfigureAwait(false);
            }
        }
    }

    private readonly record struct OutputLine(string Content, bool StandardError);
#endif
}
