// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class ProcessEventExtensionsTests
{
    [Fact]
    public async Task FakeExecutionIsDeferredAndDerivedPipelinesShareItsClaim()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(output: ["first"], error: ["warning"]);
        runner.EnqueueResult(output: ["second"]);
        var command = new ProcessCommand("scripted-command");
        var execution = runner.CreateExecution(command);
        var decoded = execution.DecodeText(Encoding.UTF8);
        Assert.Empty(runner.Commands);
        Assert.Empty(runner.Disposables);
        Assert.False(runner.RunStarted.Task.IsCompleted);

        await using (var enumerator = decoded.GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.Empty(runner.Commands);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Started(1), enumerator.Current);
            using var rejectedCancellation = new CancellationTokenSource();
            rejectedCancellation.Cancel();
            Assert.Throws<InvalidOperationException>(() => execution.GetAsyncEnumerator(rejectedCancellation.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.OnStandardError(_ => { }).ExecuteAsync());

            var remaining = new List<ProcessEvent<ReadOnlyMemory<char>>>();
            while (await enumerator.MoveNextAsync())
            {
                remaining.Add(enumerator.Current);
            }

            Assert.Collection(remaining,
                item => Assert.Equal("first\n", Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>(item).Content.ToString()),
                item => Assert.Equal("warning\n", Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardError>(item).Content.ToString()),
                item => Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted>(item),
                item => Assert.IsType<ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted>(item),
                item => Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), item));
        }

        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);

        var second = await runner.CreateExecution(command).DecodeText(Encoding.UTF8)
            .ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ProcessCaptureResult(0, "second\n", ""), second);
        Assert.Equal([command, command], runner.Commands);
        Assert.All(runner.Disposables, disposable => Assert.Equal(1, disposable.DisposeCallCount));
    }

    [Fact]
    public async Task ObserversForwardEveryEventUnchangedAndPreserveObserverOrder()
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(outputEvents:
        [
            new TestProcessOutput(false, "first"),
            new TestProcessOutput(true, "warning"),
            new TestProcessOutput(false, "last")
        ]);
        var observed = new List<string>();
        var originals = new List<ProcessEvent<ReadOnlyMemory<char>>>();
        var pipeline = runner.CreateExecution(new ProcessCommand("scripted-command"))
            .DecodeText(Encoding.UTF8)
            .Select(item =>
            {
                originals.Add(item);
                return item;
            })
            .OnStandardOutput(text => observed.Add("output-1:" + text.ToString()))
            .OnStandardOutput(text => observed.Add("output-2:" + text.ToString()))
            .OnStandardError(text => observed.Add("error:" + text.ToString()));
        Assert.Empty(observed);

        var events = await CollectAsync(pipeline);

        Assert.Equal(7, events.Count);
        Assert.Equal(originals.Count, events.Count);
        for (var index = 0; index < events.Count; index++)
        {
            Assert.Same(originals[index], events[index]);
        }

        Assert.Equal(
            ["output-1:first\n", "output-2:first\n", "error:warning\n", "output-1:last\n", "output-2:last\n"],
            observed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(17)]
    public async Task TerminalRequiresKnownExitAfterCleanup(int? exitCode)
    {
        var runner = new TestProcessRunner();
        runner.EnqueueResult(exitCode: exitCode, output: ["drain this"]);
        var execution = runner.CreateExecution(new ProcessCommand("scripted-command"));
        Assert.Empty(runner.Disposables);

        if (exitCode is { } known)
        {
            Assert.Equal(known, await execution.ExecuteAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => execution.ExecuteAsync(TestContext.Current.CancellationToken));
        }

        Assert.Equal(1, Assert.Single(runner.Disposables).DisposeCallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalsAwaitDisposalEvenAfterAnExitEvent(bool capture)
    {
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FailingEventStream([(byte)'x'], cleanup: cleanup.Task);
        Assert.Equal(0, source.DisposeCount);
        Task completion = capture
            ? source.DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken)
            : source.ExecuteAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(1, source.DisposeCount);
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            cleanup.SetResult();
        }

        await completion;
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("text")]
    public async Task ObserverAndCleanupFailuresSurviveComposition(string stage)
    {
        var observerFailure = new InvalidOperationException("Observation failed.");
        var cleanupFailure = new IOException("Disposal failed.");
        var source = new FailingEventStream(Encoding.UTF8.GetBytes("line\n"), cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var pipeline = stage switch
        {
            "bytes" => source.OnStandardOutput(_ => throw observerFailure).DecodeText(Encoding.UTF8),
            "text" => source.DecodeText(Encoding.UTF8).OnStandardOutput(_ => throw observerFailure),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(() => pipeline.ExecuteAsync(TestContext.Current.CancellationToken));

        Assert.Equal([observerFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesObserverAndCleanupFailuresAndPartialDiagnostics(bool capture)
    {
        using var cancellation = new CancellationTokenSource();
        var observerFailure = new IOException("Observation failed while canceled.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FailingEventStream([(byte)'x'], cleanupFailure: cleanupFailure);
        var retained = new StringBuilder();
        var pipeline = source.DecodeText(Encoding.UTF8)
            .OnStandardOutput(text =>
            {
                retained.Append(text.Span);
                cancellation.Cancel();
                throw observerFailure;
            });
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Equal(0, retained.Length);
        Task completion = capture
            ? pipeline.ExecuteAndCaptureTextAsync(cancellation.Token)
            : pipeline.ExecuteAsync(cancellation.Token);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion);

        Assert.Equal(cancellation.Token, failure.CancellationToken);
        Assert.Equal([observerFailure, cleanupFailure], Assert.IsType<AggregateException>(failure.InnerException).Flatten().InnerExceptions);
        Assert.Equal("x", retained.ToString());
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n\nA\rB\r\n日本語\0last")]
    public async Task DecoderPreservesSplitCharactersAndExactText(string text)
    {
        var events = await CollectAsync(FragmentedEvents(text).DecodeText(Encoding.UTF8));
        var retained = events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutput>()
            .Select(item => item.Content).ToArray();

        // Inspect retained memory after all later reads and disposal, without line normalization.
        Assert.Equal(text, string.Concat(retained.Select(content => content.ToString())));
        Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Started(1), events[0]);
        Assert.Single(events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted>());
        Assert.Single(events.OfType<ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted>());
        Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), events[^1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DecoderAndCleanupFailuresAreBothPreserved(bool invalidEncoding, bool failedCleanup)
    {
        var cleanupFailure = failedCleanup ? new IOException("Disposal failed.") : null;
        var source = new FailingEventStream([0xFF], cleanupFailure: cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        var decoded = source.DecodeText(new UTF8Encoding(false, invalidEncoding));
        if (invalidEncoding && failedCleanup)
        {
            var failure = await Assert.ThrowsAsync<AggregateException>(() => CollectAsync(decoded));
            Assert.IsType<DecoderFallbackException>(failure.InnerExceptions[0]);
            Assert.Same(cleanupFailure, failure.InnerExceptions[1]);
        }
        else if (invalidEncoding)
        {
            await Assert.ThrowsAsync<DecoderFallbackException>(() => CollectAsync(decoded));
        }
        else if (failedCleanup)
        {
            Assert.Same(cleanupFailure, await Assert.ThrowsAsync<IOException>(() => CollectAsync(decoded)));
        }
        else
        {
            var events = await CollectAsync(decoded);
            Assert.Equal(new ProcessEvent<ReadOnlyMemory<char>>.Exited(0), events[^1]);
        }

        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("capture")]
    [InlineData("observer")]
    [InlineData("execute")]
    public async Task UpstreamAndCleanupFailuresSurviveAdapters(string adapter)
    {
        var readFailure = new IOException("Reading failed.");
        var cleanupFailure = new InvalidOperationException("Disposal failed.");
        var source = new FailingEventStream([(byte)'x'], readFailure, cleanupFailure);
        Assert.Equal(0, source.DisposeCount);
        Func<Task> read = adapter switch
        {
            "text" => () => CollectAsync(source.DecodeText(Encoding.UTF8)),
            "capture" => () => source.DecodeText(Encoding.UTF8).ExecuteAndCaptureTextAsync(TestContext.Current.CancellationToken),
            "observer" => () => source.DecodeText(Encoding.UTF8).OnStandardOutput(_ => { }).ExecuteAsync(TestContext.Current.CancellationToken),
            "execute" => () => source.ExecuteAsync(TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(adapter))
        };

        var failure = await Assert.ThrowsAsync<AggregateException>(read);

        Assert.Equal([readFailure, cleanupFailure], failure.Flatten().InnerExceptions);
        Assert.Equal(1, source.DisposeCount);
    }

    private static async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> FragmentedEvents(string text)
    {
        await Task.CompletedTask;
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(new byte[] { value });
        }

        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted();
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted();
        yield return new ProcessEvent<ReadOnlyMemory<byte>>.Exited(0);
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class FailingEventStream(byte[] content, Exception? readFailure = null, Exception? cleanupFailure = null,
        Task? cleanup = null)
        : IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>>
    {
        public int DisposeCount { get; private set; }

        public IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new Enumerator(this, content, readFailure, cleanupFailure, cleanup, cancellationToken);

        private sealed class Enumerator(FailingEventStream owner, byte[] content, Exception? readFailure,
            Exception? cleanupFailure, Task? cleanup, CancellationToken cancellationToken) : IAsyncEnumerator<ProcessEvent<ReadOnlyMemory<byte>>>
        {
            private int _index;

            public ProcessEvent<ReadOnlyMemory<byte>> Current { get; private set; } = new ProcessEvent<ReadOnlyMemory<byte>>.Started(1);

            public ValueTask<bool> MoveNextAsync()
            {
                cancellationToken.ThrowIfCancellationRequested();
                Current = _index++ switch
                {
                    0 => new ProcessEvent<ReadOnlyMemory<byte>>.Started(1),
                    1 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput(content),
                    2 when readFailure is not null => throw readFailure,
                    2 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted(),
                    3 => new ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted(),
                    _ => new ProcessEvent<ReadOnlyMemory<byte>>.Exited(0)
                };

                return ValueTask.FromResult(_index <= 5);
            }

            public ValueTask DisposeAsync()
            {
                owner.DisposeCount++;
                if (cleanup is not null)
                {
                    return new ValueTask(cleanup);
                }

                return cleanupFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(cleanupFailure);
            }
        }
    }
}
