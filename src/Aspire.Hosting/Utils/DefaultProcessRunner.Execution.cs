// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Runs a process with concurrent input/output and independently supervised cleanup.
/// </summary>
internal sealed partial class DefaultProcessRunner : IProcessRunner
{
    private const int ReadBufferSize = 16 * 1024;

    // Bounds queued read buffers, excluding in-flight buffers and consumer-retained output.
    private const int QueuedBufferBudget = 512 * 1024;
    private const int EventCapacity = QueuedBufferBudget / ReadBufferSize;

    /// <inheritdoc />
    public IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command)
        => new ProcessEventStream(ReadEventsAsync(command));

    private static async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> ReadEventsAsync(
        ProcessCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var events = Channel.CreateBounded<ProcessEvent<ReadOnlyMemory<byte>>>(
            new BoundedChannelOptions(EventCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var abort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = RunAsync(command, events.Writer, abort, cancellationToken);
        (int? ExitCode, Exception? Failure) result;
        try
        {
            await foreach (var item in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            // Signaling remains safe after execution has disposed its token sources.
            abort.TrySetResult();
            result = await execution.ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ExceptionDispatchInfo.Capture(result.Failure).Throw();
            }
        }
        if (result.ExitCode is { } exitCode)
        {
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.Exited(exitCode);
        }
    }

    private static async Task<(int? ExitCode, Exception? Failure)> RunAsync(
        ProcessCommand command,
        ChannelWriter<ProcessEvent<ReadOnlyMemory<byte>>> events,
        TaskCompletionSource abort,
        CancellationToken cancellationToken)
    {
        int? exitCode = null;
        List<Exception> failures = [];
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = new System.Diagnostics.Process { StartInfo = CreateStartInfo(command) };
            process.Start();

            // Share the runtime exit observation between execution and cleanup. Independent
            // WaitForExitAsync calls have lost exit notifications under cancellation on Linux.
            // https://github.com/dotnet/runtime/pull/136887
            var exit = process.WaitForExitAsync(CancellationToken.None);

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var inputStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            using var registration = stop.Token.Register(() => abort.TrySetResult());

            var outputStream = process.StandardOutput.BaseStream;
            var errorStream = process.StandardError.BaseStream;
            var inputStream = process.StandardInput.BaseStream;

            // No other writer starts until Started is queued.
            events.TryWrite(new ProcessEvent<ReadOnlyMemory<byte>>.Started(process.Id));
            var output = ReadStandardOutputAsync(outputStream, events, stop.Token);
            var error = ReadStandardErrorAsync(errorStream, events, stop.Token);
            var input = WriteInputAsync(inputStream, command.Input?.Bytes ?? default, inputStop.Token, stop.Token);
            Task[] operations = [output, error, input, exit];
            try
            {
                await SuperviseAsync(operations, exit, abort.Task, inputStop, inputStream, cancellationToken).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                if (await input.ConfigureAwait(false) is { } inputFailure && process.ExitCode == 0)
                {
                    throw inputFailure;
                }
                exitCode = process.ExitCode;
            }
            catch (Exception exception)
            {
                if (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    failures.Add(exception is OperationCanceledException
                        ? new OperationCanceledException("Process execution was canceled.", exception, cancellationToken)
                        : exception);
                }
                stop.Cancel();
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(command.KillEntireProcessTree);
                    }
                }
                catch (Exception killException) when ((killException is InvalidOperationException or Win32Exception) && process.HasExited)
                {
                    // Exit raced termination; settle the shared exit observation.
                    await exit.ConfigureAwait(false);
                }
                catch (Exception killException)
                {
                    failures.Add(killException);
                }
                // Root exit does not imply EOF: detached descendants can hold either pipe.
                outputStream.Close();
                errorStream.Close();
                inputStream.Close();
                await SettleAsync(operations, failures).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        Exception? result = failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures)
        };
        // Completion follows process/stream/token disposal even if the iterator stays paused.
        events.TryComplete(result);
        return (exitCode, result);
    }

    private static ProcessStartInfo CreateStartInfo(ProcessCommand command)
    {
        // Use the same environment snapshot for PATH lookup and process configuration.
        var environment = new Dictionary<string, string>(command.EnvironmentVariables);
        var startInfo = new ProcessStartInfo(command.ResolveExecutablePath
            ? PathLookupHelper.ResolveExecutablePath(command.ExecutablePath, environment)
            : command.ExecutablePath)
        {
            WorkingDirectory = command.WorkingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!command.InheritEnvironmentVariables)
        {
            startInfo.Environment.Clear();
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        ProcessStartInfoHelper.SetCommand(startInfo, startInfo.FileName, command.Arguments, OperatingSystem.IsWindows());
        return startInfo;
    }

    private static async Task SuperviseAsync(
        Task[] operations,
        Task exit,
        Task abort,
        CancellationTokenSource inputStop,
        Stream inputStream,
        CancellationToken cancellationToken)
    {
        // WhenAll alone can hide a failed transfer behind another task waiting
        // for the still-running child. Supervise without requiring event consumption.
        var pending = new List<Task>(operations) { abort };
        while (pending.Count > 1)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            if (finished == abort)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            pending.Remove(finished);
            await finished.ConfigureAwait(false);
            if (finished == exit)
            {
                inputStop.Cancel();
                inputStream.Close();
            }
        }
    }

    private static async Task SettleAsync(Task[] operations, List<Exception> failures)
    {
        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (Exception transferException)
        {
            foreach (var task in operations)
            {
                if (task.Exception is { } taskFailure)
                {
                    foreach (var failure in taskFailure.InnerExceptions)
                    {
                        if (!failures.Contains(failure))
                        {
                            failures.Add(failure);
                        }
                    }
                }
            }

            if (failures.Count == 0 && transferException is not OperationCanceledException)
            {
                failures.Add(transferException);
            }
        }
    }

    /// <summary>
    /// Publishes stdout buffers and EOF independently of process exit.
    /// </summary>
    internal static Task ReadStandardOutputAsync(
        Stream stream,
        ChannelWriter<ProcessEvent<ReadOnlyMemory<byte>>> events,
        CancellationToken stopToken)
        => ReadOutputAsync(stream, events,
            static content => new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(content),
            new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted(), stopToken);

    /// <summary>
    /// Publishes stderr buffers and EOF independently of process exit.
    /// </summary>
    internal static Task ReadStandardErrorAsync(
        Stream stream,
        ChannelWriter<ProcessEvent<ReadOnlyMemory<byte>>> events,
        CancellationToken stopToken)
        => ReadOutputAsync(stream, events,
            static content => new ProcessEvent<ReadOnlyMemory<byte>>.StandardError(content),
            new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted(), stopToken);

    private static async Task ReadOutputAsync(
        Stream stream,
        ChannelWriter<ProcessEvent<ReadOnlyMemory<byte>>> events,
        Func<ReadOnlyMemory<byte>, ProcessEvent<ReadOnlyMemory<byte>>> createEvent,
        ProcessEvent<ReadOnlyMemory<byte>> completedEvent,
        CancellationToken stopToken)
    {
        try
        {
            while (true)
            {
                // Each published array belongs to its event, including after disposal.
                var buffer = new byte[ReadBufferSize];
                var count = await stream.ReadAsync(buffer, stopToken).ConfigureAwait(false);
                if (count == 0)
                {
                    await events.WriteAsync(completedEvent, stopToken).ConfigureAwait(false);
                    return;
                }
                ReadOnlyMemory<byte> content = buffer.AsMemory(0, count);
                await events.WriteAsync(createEvent(content), stopToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (stopToken.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
            // Cleanup closes local pipes even if a descendant holds the write end.
            // Aborted reads must not publish a successful EOF.
            throw new OperationCanceledException("Process output reading was aborted.", exception, stopToken);
        }
    }

    /// <summary>
    /// Delivers and closes stdin, retaining delivery failures for exit-code policy.
    /// </summary>
    internal static async Task<IOException?> WriteInputAsync(
        Stream stream,
        ReadOnlyMemory<byte> input,
        CancellationToken inputStopToken,
        CancellationToken executionStopToken)
    {
        try
        {
            await stream.WriteAsync(input, inputStopToken).ConfigureAwait(false);
            return null;
        }
        catch (IOException exception) when (!executionStopToken.IsCancellationRequested)
        {
            // A tool rejecting stdin can still emit its actual failure on stderr.
            return exception;
        }
        catch (Exception exception) when (inputStopToken.IsCancellationRequested &&
            exception is OperationCanceledException or ObjectDisposedException or IOException)
        {
            return executionStopToken.IsCancellationRequested ? null
                : new IOException("The process exited before standard input was delivered.", exception);
        }
        finally
        {
            // Closing stdin signals EOF without flushing a StreamWriter into a broken pipe.
            stream.Close();
        }
    }
}
