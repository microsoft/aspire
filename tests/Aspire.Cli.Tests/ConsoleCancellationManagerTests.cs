// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests;

public class ConsoleCancellationManagerTests
{
    [Fact]
    public void FirstSignal_RequestsCancellation()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        Assert.False(manager.IsCancellationRequested);

        manager.Cancel(130);

        Assert.True(manager.IsCancellationRequested);
    }

    [Fact]
    public void FirstSignal_TokenIsCancelled()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        var token = manager.Token;

        Assert.False(token.IsCancellationRequested);

        manager.Cancel(130);

        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task SecondSignal_ForcesImmediateTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));

        // Set a handler that never completes so the first signal doesn't resolve ProcessTerminationCompletionSource
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);
        manager.Cancel(130);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(130, exitCode);
    }

    [Fact]
    public async Task FirstSignal_WithNoHandler_ForcesTerminationAfterTimeout()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromMilliseconds(50));

        // No handler set, so ForceTerminationAfterTimeoutAsync should complete quickly
        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public async Task FirstSignal_HandlerCompletesWithinTimeout_DoesNotForceTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        // Set a handler that completes immediately
        manager.SetStartedHandler(Task.FromResult(0));

        manager.Cancel(130);

        // Give the async timeout path time to evaluate
        await Task.Delay(100);

        // ProcessTerminationCompletionSource should NOT be signaled because the handler completed in time
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task FirstSignal_HandlerExceedsTimeout_ForcesTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromMilliseconds(50));

        // Set a handler that never completes
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public void Cancel_IsNonBlocking()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));

        // Set a handler that never completes
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        // Cancel should return immediately without blocking (this would hang if Cancel were synchronous)
        var sw = System.Diagnostics.Stopwatch.StartNew();
        manager.Cancel(130);
        sw.Stop();

        // Cancel should complete in well under a second (it's non-blocking)
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Cancel took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
        Assert.True(manager.IsCancellationRequested);
    }

    [Fact]
    public async Task MultipleSignals_OnlyFirstAndSecondHaveEffect()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));

        // Set a handler that never completes
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        // Third signal should not throw or cause issues
        manager.Cancel(130);
        manager.Cancel(130);
        manager.Cancel(130);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(130, exitCode);
    }

    [Fact]
    public void Dispose_AllowsSubsequentCancelWithoutException()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        manager.Dispose();

        // Cancel after dispose should not throw (signal can race with shutdown)
        manager.Cancel(130);
    }

    [Fact]
    public void Token_RemainsAccessibleAfterDispose()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        var token = manager.Token;
        manager.Dispose();

        // Token should still be accessible (stored in field before dispose)
        Assert.False(token.IsCancellationRequested);
    }
}
