// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;
using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestProcessRunner : IProcessRunner
{
    private readonly object _lock = new();
    private readonly Queue<TestProcessRun> _runs = [];
    private readonly List<ProcessCommand> _commands = [];
    private readonly List<TestProcessDisposable> _disposables = [];

    private readonly List<ProcessSpec> _processSpecs = [];

    public IReadOnlyList<ProcessSpec> ProcessSpecs
    {
        get
        {
            lock (_lock)
            {
                return [.. _processSpecs];
            }
        }
    }

    public Action<ProcessSpec>? RunCallback { get; set; }

    public IReadOnlyList<ProcessCommand> Commands
    {
        get
        {
            lock (_lock)
            {
                return [.. _commands];
            }
        }
    }

    public IReadOnlyList<TestProcessDisposable> Disposables
    {
        get
        {
            lock (_lock)
            {
                return [.. _disposables];
            }
        }
    }

    public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<ProcessCommand>? ExecuteCallback { get; set; }

    public void EnqueueResult(
        int? exitCode = 0,
        IReadOnlyList<string>? output = null,
        IReadOnlyList<string>? error = null,
        int? totalOutputLineCount = null,
        IReadOnlyList<TestProcessOutput>? outputEvents = null)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result(exitCode, output, error, outputEvents) with { TotalOutputLineCount = totalOutputLineCount });
        }
    }

    public void EnqueueException(Exception exception)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Failed(exception));
        }
    }

    public void EnqueuePending(Task<int> exitCode, IReadOnlyList<string>? output = null)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result(output: output) with { PendingExitCode = exitCode });
        }
    }

    public void EnqueuePending(Task<ProcessResult> processResult)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result() with { PendingResult = processResult });
        }
    }

    public Task<ProcessCaptureResult> RunAndCaptureTextAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        ReadOnlyMemory<byte> standardInput = default,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("This test runner supports only line execution.");

    public (Task<ProcessResult>, IAsyncDisposable) Run(ProcessSpec processSpec)
    {
        TestProcessRun run;
        TestProcessDisposable disposable;
        lock (_lock)
        {
            _processSpecs.Add(processSpec);

            disposable = new TestProcessDisposable();
            _disposables.Add(disposable);

            run = _runs.Count > 0 ? _runs.Dequeue() : TestProcessRun.Result();
        }

        RunCallback?.Invoke(processSpec);
        RunStarted.TrySetResult();

        if (run.FailureException is { } exception)
        {
            throw exception;
        }

        if (run.PendingResult is { } pendingResult)
        {
            return (pendingResult, disposable);
        }

        foreach (var output in run.OutputEvents)
        {
            if (output.IsError)
            {
                processSpec.OnErrorData?.Invoke(output.Value);
            }
            else
            {
                processSpec.OnOutputData?.Invoke(output.Value);
            }
        }

        var processOutput = run.OutputEvents.Select(static output => output.Value).ToArray();
        var totalOutputLineCount = run.TotalOutputLineCount ?? processOutput.Length;
        if (processSpec.RetainedOutputLineCount is { } retainedOutputLineCount)
        {
            processOutput = processOutput.TakeLast(retainedOutputLineCount).ToArray();
        }

        var processResult = new ProcessResult(run.ExitCode ?? throw new InvalidOperationException("Legacy test execution requires a known exit code."), processOutput, totalOutputLineCount);

        return (Task.FromResult(processResult), disposable);
    }

    public IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> CreateExecution(ProcessCommand command)
        => new ProcessEventStream(ReadEventsAsync(command));

    private async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> ReadEventsAsync(
        ProcessCommand command, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TestProcessRun run;
        TestProcessDisposable disposable;
        lock (_lock)
        {
            _commands.Add(command);
            disposable = new TestProcessDisposable();
            _disposables.Add(disposable);
            run = _runs.Count > 0 ? _runs.Dequeue() : TestProcessRun.Result();
        }
        var exitCode = run.ExitCode;
        await using (disposable.ConfigureAwait(false))
        {
            RunStarted.TrySetResult();
            if (run.FailureException is { } exception)
            {
                throw exception;
            }
            ExecuteCallback?.Invoke(command);
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);
            var outputEvents = run.OutputEvents;
            if (run.PendingResult is { } pendingResult)
            {
                var result = await pendingResult.WaitAsync(cancellationToken).ConfigureAwait(false);
                outputEvents = result.ProcessOutput.Select(static line => new TestProcessOutput(false, line)).ToArray();
                exitCode = result.ExitCode;
            }
            if (run.PendingExitCode is { } pending)
            {
                exitCode = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var output in outputEvents)
            {
                var bytes = Encoding.UTF8.GetBytes(output.Value + "\n");
                yield return output.IsError
                    ? new ProcessEvent<ReadOnlyMemory<byte>>.StandardError(bytes)
                    : new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(bytes);
            }
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted();
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted();
        }
        if (exitCode is { } code)
        {
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.Exited(code);
        }
    }

    private sealed record TestProcessRun(
        int? ExitCode,
        IReadOnlyList<TestProcessOutput> OutputEvents,
        Exception? FailureException,
        Task<int>? PendingExitCode)
    {
        public Task<ProcessResult>? PendingResult { get; init; }

        public int? TotalOutputLineCount { get; init; }

        public static TestProcessRun Result(
            int? exitCode = 0,
            IReadOnlyList<string>? output = null,
            IReadOnlyList<string>? error = null,
            IReadOnlyList<TestProcessOutput>? outputEvents = null)
        {
            outputEvents ??=
            [
                .. (output ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: false, Value: value)),
                .. (error ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: true, Value: value))
            ];

            return new TestProcessRun(exitCode, outputEvents, null, null);
        }

        public static TestProcessRun Failed(Exception exception)
            => new(0, [], exception, null);
    }
}

internal sealed record TestProcessOutput(bool IsError, string Value);

internal sealed class TestProcessDisposable : IAsyncDisposable
{
    public int DisposeCallCount { get; private set; }

    public Func<ValueTask>? OnDisposeAsync { get; set; }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;

        return OnDisposeAsync?.Invoke() ?? ValueTask.CompletedTask;
    }
}
