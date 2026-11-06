// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Hosting.Dcp;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

internal sealed record DcpForkInvocation(string ExecutablePath, string SdkVersion);

internal sealed class DcpForkShutdownException : InvalidOperationException
{
    internal DcpForkShutdownException() : base(RunCommandStrings.WatchDcpShutdownFailed) { }
    internal DcpForkShutdownException(Exception inner) : base(RunCommandStrings.WatchDcpShutdownFailed, inner) { }

    internal static bool ContainsFailure(Exception exception) =>
        exception is DcpForkShutdownException ||
        (exception is AggregateException aggregate
            ? aggregate.InnerExceptions.Any(ContainsFailure)
            : exception.InnerException is { } inner && ContainsFailure(inner));
}

internal sealed class DcpForkProcessExecution(
    ProcessStartInfo command,
    DcpForkInvocation fork,
    ProcessInvocationOptions options,
    IProcessExecutionFactory factory,
    ILogger logger,
    bool isWindows,
    Func<int, IDcpForkChildProcess> openChildProcess) : IProcessExecution
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);
    internal static TimeSpan ShutdownTimeout { get; } = TimeSpan.FromSeconds(30);
    private readonly PidFrame _pid = new(options.StandardOutputCallback);
    private IProcessExecution? _transport;
    private Task<int>? _completion;
    private Task? _stop;
    private IDcpForkChildProcess? _child;
    private DcpProcessIdentity? _childIdentity;
    private DateTimeOffset? _startTime;
    private bool _started;
    private bool _disposed;
    private int _startCalled;

    public string FileName => command.FileName;
    public IReadOnlyList<string> Arguments => command.ArgumentList;
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; } =
        new ReadOnlyDictionary<string, string?>(command.Environment);
    public int ProcessId => _pid.ProcessId.Task.IsCompletedSuccessfully
        ? _pid.ProcessId.Task.Result
        : throw new InvalidOperationException("The DCP fork has not established a child PID.");
    public DateTimeOffset? StartTime => _startTime;
    public bool HasExited => Transport.HasExited;
    public int ExitCode => Transport.ExitCode;

    private IProcessExecution Transport => _transport ??
        throw new InvalidOperationException("The DCP fork has not been started.");

    public async Task<bool> StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _startCalled, 1) != 0)
        {
            throw new InvalidOperationException("The DCP fork has already been started.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        await ProbeAsync(cancellationToken).ConfigureAwait(false);

        _transport = CreateTransport(command.ArgumentList, _pid, options.StandardErrorCallback);
        _started = await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        if (!_started)
        {
            return false;
        }

        _completion = CompleteAsync(_transport, _pid);
        try
        {
            await _pid.ProcessId.Task.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
            if (isWindows)
            {
                (_child, _childIdentity) = await CaptureChildAsync(_transport, _completion, ProcessId, cancellationToken).ConfigureAwait(false);
                _startTime = _childIdentity is { } identity ? new DateTimeOffset(identity.Timestamp) : null;
            }
            else
            {
                _startTime = ProcessStartTimeHelper.TryGetProcessStartTime(ProcessId);
            }
            return true;
        }
        catch (Exception failure)
        {
            await StopAfterFailureAsync(failure, cancellationToken).ConfigureAwait(false);
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            throw new InvalidOperationException(RunCommandStrings.WatchDcpUnavailable, failure);
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        var completion = _completion ??
            throw new InvalidOperationException("The DCP fork has not been started.");
        try
        {
            return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException failure) when (cancellationToken.IsCancellationRequested)
        {
            await StopAfterFailureAsync(failure, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public Task<int> WaitForRootExitAsync(CancellationToken cancellationToken) =>
        Transport.WaitForRootExitAsync(cancellationToken);

    public void Kill(bool entireProcessTree) => Transport.Kill(entireProcessTree);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_transport is null)
        {
            return;
        }

        try
        {
            if (_started && !_transport.HasExited)
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // A prior wait already reported a failed retirement. Release exited transport handles
            // without replaying that failure from disposal and replacing the original cancellation.
            if (!_started || _transport.HasExited)
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            _child?.Dispose();
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var output = new List<string>();
        var error = new List<string>();
        Exception? outputFailure = null;
        var frame = new PidFrame(line => AddProbeLine(output, line, ref outputFailure));
        var probe = CreateTransport(["--version"], frame, line => AddProbeLine(error, line, ref outputFailure));
        var started = false;
        Task<int>? completion = null;
        IDcpForkChildProcess? child = null;
        DcpProcessIdentity? identity = null;
        Exception? failure = null;
        try
        {
            started = await probe.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!started)
            {
                throw new InvalidOperationException(RunCommandStrings.WatchDcpUnavailable);
            }
            completion = CompleteAsync(probe, frame);
            if (isWindows)
            {
                var pid = await frame.ProcessId.Task.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
                (child, identity) = await CaptureChildAsync(probe, completion, pid, cancellationToken).ConfigureAwait(false);
            }
            var exitCode = await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
            await frame.ProcessId.Task.ConfigureAwait(false);
            if (outputFailure is not null)
            {
                throw outputFailure;
            }
            if (exitCode != 0 || output.Count != 1 ||
                !string.Equals(output[0], fork.SdkVersion, StringComparison.Ordinal))
            {
                logger.LogWarning("DCP fork preflight failed: exit code {ExitCode}, SDK output {Output}, stderr {Error}.",
                    exitCode, string.Join('\n', output), string.Join('\n', error));
                throw new InvalidOperationException(RunCommandStrings.WatchDcpUnavailable);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failure = exception;
            logger.LogWarning(exception, "DCP cannot preserve the selected SDK's fork output before AppHost watch launch.");
            throw new InvalidOperationException(RunCommandStrings.WatchDcpUnavailable, exception);
        }
        catch (OperationCanceledException exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            try
            {
                if (started && completion is not null)
                {
                    await StopTransportAsync(probe, completion, child, identity).ConfigureAwait(false);
                }
                await probe.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (failure is not null)
            {
                ThrowCleanupFailure(failure, cleanupFailure, cancellationToken);
            }
            finally
            {
                child?.Dispose();
            }
        }
    }

    private IProcessExecution CreateTransport(
        IReadOnlyList<string> arguments, PidFrame frame, Action<string>? stderr)
    {
        if (!string.IsNullOrEmpty(command.Arguments))
        {
            throw new InvalidOperationException("DCP watch forks require a structured argument list.");
        }
        using var owner = Process.GetCurrentProcess();
        var identity = DcpProcessMonitor.GetMonitorProcessIdentity(owner);
        var startInfo = new ProcessStartInfo(fork.ExecutablePath,
        [
            "fork-process", "--monitor", identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            "--monitor-identity-time", FormatIdentity(identity.Timestamp),
            "--enable-child-cleanup", "--skip-descendants", "--", command.FileName, .. arguments
        ])
        {
            WorkingDirectory = command.WorkingDirectory
        };
        startInfo.Environment.Clear();
        foreach (var (name, value) in command.Environment)
        {
            startInfo.Environment[name] = value;
        }

        // DCP must outlive the CLI to retire the original watcher. Do not put the transport in an
        // OwnedTree guardian/job or let its cancellation tree-kill the AppHost's DCP descendants.
        // The original factory already logged/redacted the watcher; suppress the nested fork/PID log.
        return factory.CreateExecution(startInfo, new ProcessInvocationOptions
        {
            Lifetime = Aspire.Shared.ChildProcessLifetime.AppHost,
            KillEntireProcessTreeOnCancel = false,
            IsolateConsole = true,
            SuppressLogging = true,
            StandardOutputCallback = frame.OnLine,
            StandardErrorCallback = stderr
        });
    }

    private static async Task<int> CompleteAsync(IProcessExecution transport, PidFrame frame)
    {
        try
        {
            return await transport.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            frame.Complete();
        }
    }

    private Task StopAsync() => _stop ??= StopTransportAsync(Transport, _completion!, _child, _childIdentity);

    private async Task StopTransportAsync(
        IProcessExecution transport, Task<int> completion, IDcpForkChildProcess? child, DcpProcessIdentity? childIdentity)
    {
        using var cleanupTimeout = new CancellationTokenSource(ShutdownTimeout);
        var requestedStop = !transport.HasExited;
        if (requestedStop)
        {
            if (!isWindows)
            {
                ProcessSignaler.RequestGracefulShutdown(transport.ProcessId, transport.StartTime, logger);
            }
            else if (child is { HasExited: false } && childIdentity is not null)
            {
                await using var stop = factory.CreateExecution(fork.ExecutablePath,
                    ["stop-process-tree", "--skip-descendants", "--pid", childIdentity.ProcessId.ToString(CultureInfo.InvariantCulture),
                        "--process-start-time", FormatIdentity(childIdentity.Timestamp)],
                    env: null, new DirectoryInfo(command.WorkingDirectory), new ProcessInvocationOptions
                    {
                        SuppressLogging = true,
                        KillEntireProcessTreeOnCancel = false,
                        StandardErrorCallback = line => logger.LogWarning("DCP fork shutdown: {Line}", line)
                    });
                // Target the retained SDK root, never the fork helper. A stopper targeting the
                // helper would force-kill it at 6s/15s before its nested cleanup could finish.
                if (!await stop.StartAsync(cleanupTimeout.Token).ConfigureAwait(false) ||
                    await stop.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false) != 0)
                {
                    throw new DcpForkShutdownException();
                }
            }
        }

        try
        {
            // Do not pass the cancelled caller token to ChildProcess: its immediate force-kill
            // fallback could retire DCP before DCP cleans up the actual watcher. Its output relay
            // closes after a bounded tail, independently of descendants retaining the child's pipes.
            var exitCode = await completion.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            var confirmedChildExit = isWindows && child is { HasExited: true } &&
                exitCode != 1 && child.ExitCode == exitCode;
            if ((isWindows && child is { HasExited: false }) ||
                (requestedStop && exitCode is not (0 or 130) && !confirmedChildExit))
            {
                logger.LogError("DCP fork {Pid} exited with {ExitCode} rather than confirming cancellation cleanup.", transport.ProcessId, exitCode);
                throw new DcpForkShutdownException();
            }
        }
        catch (OperationCanceledException exception) when (cleanupTimeout.IsCancellationRequested)
        {
            // An unresponsive transport still owns the original child. Leave it monitoring the
            // CLI rather than destroying its cleanup guard and silently permitting a replacement.
            logger.LogError(exception, "DCP fork {Pid} did not confirm watch cleanup.", transport.ProcessId);
            throw new DcpForkShutdownException(exception);
        }
    }

    private async Task<(IDcpForkChildProcess? Child, DcpProcessIdentity? Identity)> CaptureChildAsync(
        IProcessExecution transport, Task<int> completion, int pid, CancellationToken cancellationToken)
    {
        if (transport.HasExited)
        {
            await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
            return (null, null);
        }
        IDcpForkChildProcess child;
        try
        {
            child = openChildProcess(pid);
        }
        catch (ArgumentException)
        {
            // A version probe or failed SDK startup can finish before the PID line is consumed.
            // Require observed original-transport completion, not a new lookup of the recycled PID.
            await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
            return (null, null);
        }
        try
        {
            if (child.HasExited)
            {
                await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
                child.Dispose();
                return (null, null);
            }
            int parent;
            DcpProcessIdentity identity;
            try
            {
                parent = child.ParentProcessId;
                if (parent != transport.ProcessId || transport.HasExited)
                {
                    throw new InvalidDataException(RunCommandStrings.WatchDcpUnavailable);
                }
                identity = child.GetIdentity();
            }
            catch (InvalidOperationException) when (child.HasExited)
            {
                await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
                child.Dispose();
                return (null, null);
            }
            if (child.HasExited)
            {
                await completion.WaitAsync(s_probeTimeout, cancellationToken).ConfigureAwait(false);
                child.Dispose();
                return (null, null);
            }
            if (transport.HasExited)
            {
                throw new InvalidDataException(RunCommandStrings.WatchDcpUnavailable);
            }
            return (child, identity);
        }
        catch
        {
            child.Dispose();
            throw;
        }
    }

    private async Task StopAfterFailureAsync(Exception failure, CancellationToken cancellationToken)
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            logger.LogError(cleanupFailure, "DCP fork cleanup failed after the watch operation failed.");
            ThrowCleanupFailure(failure, cleanupFailure, cancellationToken);
        }
    }

    private static void ThrowCleanupFailure(Exception failure, Exception cleanupFailure, CancellationToken cancellationToken)
    {
        var combined = new AggregateException(failure,
            cleanupFailure is DcpForkShutdownException ? cleanupFailure : new DcpForkShutdownException(cleanupFailure));
        if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(failure.Message, combined, cancellationToken);
        }
        throw combined;
    }

    private static string FormatIdentity(DateTime timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static void AddProbeLine(List<string> lines, string line, ref Exception? failure)
    {
        if (lines.Count >= 16 || line.Length > 4096)
        {
            failure ??= new InvalidDataException(RunCommandStrings.WatchDcpUnavailable);
            return;
        }
        lines.Add(line);
    }

    private sealed class PidFrame(Action<string>? output)
    {
        private bool _firstLine = true;
        internal TaskCompletionSource<int> ProcessId { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void OnLine(string line)
        {
            // Cleanup-enabled DCP stdout is "<positive decimal PID>\n<application stdout...>".
            // Consume exactly the first line; numeric application output must remain untouched.
            if (_firstLine)
            {
                _firstLine = false;
                if (line.Length <= 10 && int.TryParse(line, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
                {
                    ProcessId.TrySetResult(pid);
                }
                else
                {
                    ProcessId.TrySetException(new InvalidDataException(RunCommandStrings.WatchDcpUnavailable));
                }
                return;
            }
            output?.Invoke(line);
        }

        internal void Complete() =>
            ProcessId.TrySetException(new InvalidDataException(RunCommandStrings.WatchDcpUnavailable));
    }
}
