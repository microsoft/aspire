// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Polly;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Utils;

[Trait("Partition", "4")]
public class SignalingFakeTimeProviderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimerCreatedOnlySignalsRetryDelayAfterPollyTimeoutIsArmed(bool reuseTimeoutTimer)
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var recoveryTimeout = TimeSpan.FromMilliseconds(500);
        var timeProvider = new SignalingFakeTimeProvider(recoveryTimeout);
        var pipeline = new ResiliencePipelineBuilder { TimeProvider = timeProvider }
            .AddTimeout(recoveryTimeout)
            .Build();

        if (reuseTimeoutTimer)
        {
            await pipeline.ExecuteAsync(static _ => ValueTask.CompletedTask, cts.Token).AsTask().DefaultTimeout();
            Assert.False(timeProvider.TimerCreated.IsCompleted);
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
            Assert.False(timeProvider.TimerCreated.IsCompleted);
            Assert.False(executionTask.IsCompleted);

            startRetryDelay.TrySetResult();
            await timeProvider.TimerCreated.DefaultTimeout();
            Assert.False(executionTask.IsCompleted);
            timeProvider.Advance(recoveryTimeout);

            var exception = await Assert.ThrowsAsync<TimeoutRejectedException>(() => executionTask).DefaultTimeout();
            Assert.Equal(recoveryTimeout, exception.Timeout);
        }
        finally
        {
            startRetryDelay.TrySetResult();
            cts.Cancel();
        }
    }
}
