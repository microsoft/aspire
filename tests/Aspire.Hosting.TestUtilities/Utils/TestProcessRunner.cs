// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dcp.Process;

namespace Aspire.Hosting.Tests.Utils;

internal sealed class TestProcessRunner : IProcessRunner
{
    private readonly object _lock = new();
    private readonly Queue<TestProcessRun> _runs = [];
    private readonly List<ProcessSpec> _processSpecs = [];
    private readonly List<TestProcessDisposable> _disposables = [];

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

    public TaskCompletionSource<ProcessSpec> RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void EnqueueResult(
        int exitCode = 0,
        IReadOnlyList<string>? output = null,
        IReadOnlyList<string>? error = null,
        int? totalOutputLineCount = null,
        IReadOnlyList<TestProcessOutput>? outputEvents = null)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Result(exitCode, output, error, totalOutputLineCount, outputEvents));
        }
    }

    public void EnqueueException(Exception exception)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Failed(exception));
        }
    }

    public void EnqueuePending(Task<ProcessResult> processResult)
    {
        lock (_lock)
        {
            _runs.Enqueue(TestProcessRun.Pending(processResult));
        }
    }

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

        RunStarted.TrySetResult(processSpec);

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
        var processResult = new ProcessResult(run.ExitCode, processOutput, run.TotalOutputLineCount);

        return (Task.FromResult(processResult), disposable);
    }

    private sealed record TestProcessRun(
        int ExitCode,
        IReadOnlyList<TestProcessOutput> OutputEvents,
        int? TotalOutputLineCount,
        Exception? FailureException,
        Task<ProcessResult>? PendingResult)
    {
        public static TestProcessRun Result(
            int exitCode = 0,
            IReadOnlyList<string>? output = null,
            IReadOnlyList<string>? error = null,
            int? totalOutputLineCount = null,
            IReadOnlyList<TestProcessOutput>? outputEvents = null)
        {
            outputEvents ??=
            [
                .. (output ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: false, Value: value)),
                .. (error ?? Array.Empty<string>()).Select(static value => new TestProcessOutput(IsError: true, Value: value))
            ];

            return new TestProcessRun(exitCode, outputEvents, totalOutputLineCount, null, null);
        }

        public static TestProcessRun Failed(Exception exception)
            => new(0, [], null, exception, null);

        public static TestProcessRun Pending(Task<ProcessResult> pendingResult)
            => new(0, [], null, null, pendingResult);
    }
}

internal sealed record TestProcessOutput(bool IsError, string Value);

internal sealed class TestProcessDisposable : IAsyncDisposable
{
    public int DisposeCallCount { get; private set; }

    public ValueTask DisposeAsync()
    {
        DisposeCallCount++;

        return ValueTask.CompletedTask;
    }
}
