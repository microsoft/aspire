// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Time.Testing;
using Polly;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Utils;

// Qualification only: forces the test thread to advance time at the moment TimerCreated is signaled,
// before Task.Delay has registered its cancellation callback.
[Trait("Partition", "4")]
public class SignalingFakeTimeProviderInterleavingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AdvanceAtTimerCreatedBeforeDelayRegistersCancellation(bool reuseTimeoutTimer, bool offsetDelaySchedule)
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var recoveryTimeout = TimeSpan.FromMilliseconds(500);
        var delayScheduleOffset = offsetDelaySchedule ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero;
        var timeProvider = new AdvanceOnSignalTimeProvider(recoveryTimeout);
        var pipeline = new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddTimeout(recoveryTimeout)
            .Build();

        if (reuseTimeoutTimer)
        {
            await pipeline.ExecuteAsync(static _ => ValueTask.CompletedTask, cts.Token).AsTask().DefaultTimeout();
        }

        var executionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startRetryDelay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = pipeline.ExecuteAsync(async cancellationToken =>
        {
            executionStarted.TrySetResult();
            await startRetryDelay.Task.WaitAsync(cancellationToken);
            await Task.Delay(recoveryTimeout, timeProvider, cancellationToken);
        }, cts.Token).AsTask();

        try
        {
            await executionStarted.Task.DefaultTimeout();
            if (delayScheduleOffset > TimeSpan.Zero)
            {
                timeProvider.Advance(delayScheduleOffset);
            }

            timeProvider.AdvanceOnSignal = recoveryTimeout - delayScheduleOffset;
            startRetryDelay.TrySetResult();
            await timeProvider.Signaled.DefaultTimeout();

            var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(() => executionTask).DefaultTimeout();
            Assert.Equal(recoveryTimeout, exception.Timeout);
        }
        finally
        {
            startRetryDelay.TrySetResult();
            cts.Cancel();
        }
    }

    private sealed class AdvanceOnSignalTimeProvider(TimeSpan signaledDueTime) : FakeTimeProvider
    {
        private readonly TaskCompletionSource _signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeSpan? AdvanceOnSignal { get; set; }

        public Task Signaled => _signaled.Task;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == signaledDueTime && AdvanceOnSignal is { } advance)
            {
                AdvanceOnSignal = null;
                Advance(advance);
                _signaled.TrySetResult();
            }

            return timer;
        }
    }
}
