// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Terminal;
using Hex1b.Tokens;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Terminal;

public class TerminalThroughputFilterTests
{
    [Fact]
    public async Task SamplesRatesIdleIntervalsAndFinalPartialInterval()
    {
        var time = new FakeTimeProvider();
        var sink = new TestSink();
        var firstSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var idleSample = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.MessageLogged += _ =>
        {
            if (sink.Writes.Count == 1)
            {
                firstSample.TrySetResult();
            }
            else if (sink.Writes.Count == 2)
            {
                idleSample.TrySetResult();
            }
        };
        await using var filter = new TerminalThroughputFilter(new TestLogger("terminal", sink, enabled: true), "view-1", time);
        await filter.OnSessionStartAsync(80, 24, time.GetUtcNow());
        time.Advance(TimeSpan.FromSeconds(1));
        await filter.OnOutputAsync([new TextToken("secret-output"), new TextToken("x")], TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(1));
        await filter.OnOutputAsync([new TextToken("y")], TimeSpan.Zero);
        await filter.OnInputAsync([new TextToken("secret-input")], TimeSpan.Zero);
        Assert.Empty(sink.Writes);

        time.Advance(TimeSpan.FromSeconds(3));
        await firstSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var sample = Assert.Single(sink.Writes);
        Assert.Equal(LogLevel.Information, sample.LogLevel);
        Assert.Equal("view-1", LogTestHelpers.GetValue(sample, "ConnectionId"));
        Assert.Equal(3L, LogTestHelpers.GetValue(sample, "Tokens"));
        Assert.Equal(5d, LogTestHelpers.GetValue(sample, "Seconds"));
        Assert.Equal(0.6d, LogTestHelpers.GetValue(sample, "TokensPerSecond"));
        Assert.Equal(2L, LogTestHelpers.GetValue(sample, "Batches"));
        Assert.Equal(0.4d, LogTestHelpers.GetValue(sample, "BatchesPerSecond"));
        Assert.Equal(2, LogTestHelpers.GetValue(sample, "MaxBatchTokens"));
        Assert.Equal(1000d, LogTestHelpers.GetValue(sample, "MaxBatchGapMs"));
        Assert.Equal(3000d, LogTestHelpers.GetValue(sample, "LastOutputAgeMs"));
        Assert.Equal(1L, LogTestHelpers.GetValue(sample, "InputTokens"));
        Assert.Equal(3L, LogTestHelpers.GetValue(sample, "TotalTokens"));
        Assert.Equal(false, LogTestHelpers.GetValue(sample, "IsFinal"));

        time.Advance(TimeSpan.FromSeconds(5));
        await idleSample.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var idle = sink.Writes.ElementAt(1);
        Assert.Equal(0L, LogTestHelpers.GetValue(idle, "Tokens"));
        Assert.Equal(0d, LogTestHelpers.GetValue(idle, "TokensPerSecond"));
        Assert.Equal(0L, LogTestHelpers.GetValue(idle, "Batches"));
        Assert.Equal(0, LogTestHelpers.GetValue(idle, "MaxBatchTokens"));
        Assert.Equal(0d, LogTestHelpers.GetValue(idle, "MaxBatchGapMs"));
        Assert.Equal(8000d, LogTestHelpers.GetValue(idle, "LastOutputAgeMs"));
        Assert.Equal(0L, LogTestHelpers.GetValue(idle, "InputTokens"));
        Assert.Equal(3L, LogTestHelpers.GetValue(idle, "TotalTokens"));

        await filter.OnOutputAsync([new TextToken("z")], TimeSpan.Zero);
        time.Advance(TimeSpan.FromSeconds(1));
        await filter.DisposeAsync();
        var final = sink.Writes.ElementAt(2);
        Assert.Equal(1L, LogTestHelpers.GetValue(final, "Tokens"));
        Assert.Equal(1d, LogTestHelpers.GetValue(final, "Seconds"));
        Assert.Equal(1d, LogTestHelpers.GetValue(final, "TokensPerSecond"));
        Assert.Equal(8000d, LogTestHelpers.GetValue(final, "MaxBatchGapMs"));
        Assert.Equal(1000d, LogTestHelpers.GetValue(final, "LastOutputAgeMs"));
        Assert.Equal(4L, LogTestHelpers.GetValue(final, "TotalTokens"));
        Assert.Equal(true, LogTestHelpers.GetValue(final, "IsFinal"));

        time.Advance(TimeSpan.FromMinutes(1));
        await filter.DisposeAsync();
        Assert.Equal(3, sink.Writes.Count);
    }

    [Fact]
    public async Task ConcurrentCallbacksAreCountedWithoutLoggingTokenContent()
    {
        var time = new FakeTimeProvider();
        var sink = new TestSink();
        await using var filter = new TerminalThroughputFilter(new TestLogger("terminal", sink, enabled: true), "view-2", time);
        await filter.OnSessionStartAsync(80, 24, time.GetUtcNow());
        AnsiToken[] tokens = [new TextToken("never-log-this")];
        await Parallel.ForAsync(0, 1000, async (_, cancellationToken) =>
        {
            await filter.OnOutputAsync(tokens, TimeSpan.Zero, cancellationToken);
            await filter.OnInputAsync(tokens, TimeSpan.Zero, cancellationToken);
        });
        time.Advance(TimeSpan.FromSeconds(2));
        await filter.DisposeAsync();

        var sample = Assert.Single(sink.Writes);
        Assert.Equal(1000L, LogTestHelpers.GetValue(sample, "Tokens"));
        Assert.Equal(1000L, LogTestHelpers.GetValue(sample, "InputTokens"));
        Assert.Equal(1000L, LogTestHelpers.GetValue(sample, "Batches"));
        Assert.Equal(500d, LogTestHelpers.GetValue(sample, "TokensPerSecond"));
        Assert.Equal(
            "Terminal token throughput (view-2): 1000 tokens in 2.000s (500.0 tokens/s), " +
            "1000 batches (500.0 batches/s), max batch 1 tokens, max batch gap 0.000ms, " +
            "last output 2000.000ms ago, 1000 input tokens, 1000 total output tokens, final=True.",
            sample.Message);
    }

    [Fact]
    public async Task DelayedSampleUsesActualElapsedTime()
    {
        var time = new FakeTimeProvider();
        var sink = new TestSink();
        var sampled = new TaskCompletionSource<WriteContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        sink.MessageLogged += context => sampled.TrySetResult(context);
        await using var filter = new TerminalThroughputFilter(new TestLogger("terminal", sink, enabled: true), "delayed", time);
        await filter.OnSessionStartAsync(80, 24, time.GetUtcNow());
        await filter.OnOutputAsync([new TextToken("x")], TimeSpan.Zero);

        time.Advance(TimeSpan.FromSeconds(7));
        var sample = await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(7d, LogTestHelpers.GetValue(sample, "Seconds"));
        Assert.Equal(1d / 7, LogTestHelpers.GetValue(sample, "TokensPerSecond"));
    }

    [Fact]
    public async Task NoOutputHasNoLastOutputAge()
    {
        var time = new FakeTimeProvider();
        var sink = new TestSink();
        await using var filter = new TerminalThroughputFilter(new TestLogger("terminal", sink, enabled: true), "idle", time);
        await filter.OnSessionStartAsync(80, 24, time.GetUtcNow());

        time.Advance(TimeSpan.FromSeconds(1));
        await filter.DisposeAsync();

        var sample = Assert.Single(sink.Writes);
        Assert.Equal(0L, LogTestHelpers.GetValue(sample, "Tokens"));
        Assert.Equal(0d, LogTestHelpers.GetValue(sample, "TokensPerSecond"));
        Assert.Null(LogTestHelpers.GetValue(sample, "LastOutputAgeMs"));
    }

    [Fact]
    public async Task DisposingBeforeSessionStartsDoesNotLog()
    {
        var sink = new TestSink();
        var filter = new TerminalThroughputFilter(new TestLogger("terminal", sink, enabled: true), "unused", new FakeTimeProvider());

        await filter.DisposeAsync();

        Assert.Empty(sink.Writes);
    }
}
