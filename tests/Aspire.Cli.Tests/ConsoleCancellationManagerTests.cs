// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests;

public class ConsoleCancellationManagerTests
{
    [Fact]
    public void ConfigureForCommand_NegativeBudget_Throws()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        Assert.Throws<ArgumentOutOfRangeException>(() => manager.ConfigureForCommand(TimeSpan.FromMilliseconds(-1)));
    }

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
    public async Task FirstSignal_DefaultZeroBudget_ExpiresGracefulImmediately()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));

        // No ConfigureForCommand — graceful budget defaults to zero.
        // Set a handler that never completes so the drain budget governs forced termination.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);

        // The graceful token must fire essentially immediately (no Phase 1 delay to wait through).
        await manager.GracefulShutdownToken.WaitUntilCancelledAsync().DefaultTimeout();
        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task FirstSignal_NonZeroBudget_DelaysExpireUntilBudgetElapses()
    {
        // The graceful clock is armed via CancelAfter, which is a no-op while a debugger is
        // attached (developers need unlimited stepping time), so the timing this test asserts
        // never happens. Surface that as a real skip rather than a silent pass.
        if (Debugger.IsAttached)
        {
            Assert.Skip("Graceful CancelAfter timing is disabled while a debugger is attached.");
        }

        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(200));

        // Set a handler that never completes so we observe the graceful → drain timing.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        var sw = Stopwatch.StartNew();
        manager.Cancel(130);

        // Wait for the budget to elapse.
        await manager.GracefulShutdownToken.WaitUntilCancelledAsync().DefaultTimeout();
        sw.Stop();

        // We allowed 200ms of grace; allow generous slack for CI scheduling but assert we waited at least most of it.
        Assert.True(sw.ElapsedMilliseconds >= 150, $"Graceful token fired after only {sw.ElapsedMilliseconds}ms (expected ~200ms).");
    }

    [Fact]
    public async Task SecondSignal_ExpiresGracefulImmediately()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));
        // Large graceful budget — without a 2nd signal the token would not fire for 30s.
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        // Set a handler that never completes; 2nd signal should ONLY collapse graceful (not force exit).
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);

        // Under a debugger the window is never armed, so the token stays unfired until the 2nd signal.
        if (!Debugger.IsAttached)
        {
            Assert.False(manager.GracefulShutdownToken.IsCancellationRequested);
        }

        manager.Cancel(130);

        // 2nd signal expires graceful synchronously.
        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);

        // But the completion source should NOT have fired yet — forced exit now happens only when the
        // bounded final drain (here 30s) elapses, not synchronously on the 2nd signal.
        await Task.Delay(100);
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task AdditionalSignals_AfterSecond_AreIgnored()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));
        // Long graceful and drain budgets so neither elapses during the test window.
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        // Handler that never completes so the only way the completion source could fire promptly is an
        // explicit force path — which the two-press ladder no longer has.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(130);
        manager.Cancel(130);
        manager.Cancel(130);

        // Third and later signals are no-ops: exit is driven solely by the bounded final drain elapsing,
        // so the completion source must NOT have fired within this short window.
        await Task.Delay(100);
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task FirstSignal_HandlerCompletesWithinDrainBudget_DoesNotForceTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        // Set a handler that completes immediately.
        manager.SetStartedHandler(Task.FromResult(0));

        manager.Cancel(130);

        // Give the async watcher time to evaluate.
        await Task.Delay(100);

        // ProcessTerminationCompletionSource should NOT be signaled because the handler completed in time.
        Assert.False(manager.ProcessTerminationCompletionSource.Task.IsCompleted);
    }

    [Fact]
    public async Task FirstSignal_HandlerExceedsDrainBudget_ForcesTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromMilliseconds(50));

        // Set a handler that never completes.
        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public async Task FirstSignal_WithNoHandler_ForcesTerminationAfterDrainBudget()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromMilliseconds(50));

        // No handler set, watcher still has to wait out the drain budget before forcing termination.
        manager.Cancel(143);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(143, exitCode);
    }

    [Fact]
    public async Task GracefulBudgetElapses_ThenDrainBudgetElapses_FiresProcessTermination()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromMilliseconds(50));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(50));

        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);
        manager.Cancel(130);

        var exitCode = await manager.ProcessTerminationCompletionSource.Task.DefaultTimeout();
        Assert.Equal(130, exitCode);
        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Cancel_IsNonBlocking()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));
        manager.ConfigureForCommand(TimeSpan.FromSeconds(30));

        manager.SetStartedHandler(new TaskCompletionSource<int>().Task);

        var sw = Stopwatch.StartNew();
        manager.Cancel(130);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Cancel took {sw.ElapsedMilliseconds}ms, expected < 1000ms");
        Assert.True(manager.IsCancellationRequested);
    }

    [Fact]
    public async Task ProcessTermination_FiresGracefulExpiration_LaddersUnblock()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(30));

        // A ladder observing only the graceful token, awaiting a long delay.
        var gracefulToken = manager.GracefulShutdownToken;
        var ladderTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), gracefulToken);
                return false;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        });

        // Simulate Main deciding to leave NOW — completion source fires for reasons unrelated to a
        // 2nd Ctrl+C (e.g. external runtime tear-down). The graceful token must fire too so the ladder
        // unblocks in time to escalate before Main abandons it.
        manager.ProcessTerminationCompletionSource.TrySetResult(99);

        var unblocked = await ladderTask.DefaultTimeout();
        Assert.True(unblocked, "Ladder did not observe graceful token cancellation after process termination fired.");
    }

    [Fact]
    public void Dispose_AllowsSubsequentCancelWithoutException()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        manager.Dispose();

        // Cancel after dispose should not throw (signal can race with shutdown).
        manager.Cancel(130);
    }

    [Fact]
    public void Token_RemainsAccessibleAfterDispose()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        var token = manager.Token;
        manager.Dispose();

        // Token should still be accessible (stored in field before dispose).
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void GracefulShutdownToken_BeforeExpire_NotCancelled()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        Assert.False(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Expire_FiresGracefulToken()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        manager.Expire();

        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Expire_Idempotent()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        manager.Expire();
        manager.Expire();
        manager.Expire();

        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public void Expire_AfterDispose_DoesNotThrow()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        manager.Dispose();

        // Expire racing with dispose must not propagate to callers (signal handler /
        // watcher continuation contexts have nowhere meaningful to surface this).
        manager.Expire();
    }

    [Fact]
    public void GracefulShutdownToken_RemainsAccessibleAfterDispose()
    {
        var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        var token = manager.GracefulShutdownToken;
        manager.Dispose();

        // Token was captured up front; reading state after dispose must not throw.
        Assert.False(token.IsCancellationRequested);
    }

    [Fact]
    public void IsEnabled_DefaultsToFalse()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        Assert.False(manager.IsEnabled);
    }

    [Fact]
    public void IsEnabled_TrueAfterPositiveConfigure()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        manager.ConfigureForCommand(TimeSpan.FromSeconds(5));

        Assert.True(manager.IsEnabled);
    }

    [Fact]
    public void IsEnabled_FalseAfterZeroConfigure()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        manager.ConfigureForCommand(TimeSpan.Zero);

        Assert.False(manager.IsEnabled);
    }

    [Fact]
    public void BeginGracefulWindow_ZeroBudget_ExpiresImmediately()
    {
        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));

        // No ConfigureForCommand → zero budget → window is "over" the moment it begins.
        manager.BeginGracefulWindow();

        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginGracefulWindow_PositiveBudget_FiresTokenAfterBudget()
    {
        // BeginGracefulWindow arms CancelAfter, which is a no-op while a debugger is attached
        // (developers need unlimited stepping time), so the timing this test asserts never
        // happens. Surface that as a real skip rather than a silent pass.
        if (Debugger.IsAttached)
        {
            Assert.Skip("Graceful CancelAfter timing is disabled while a debugger is attached.");
        }

        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(50));

        manager.BeginGracefulWindow();
        Assert.False(manager.GracefulShutdownToken.IsCancellationRequested);

        await manager.GracefulShutdownToken.WaitUntilCancelledAsync().DefaultTimeout();

        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginGracefulWindow_SecondCall_DoesNotResetTimer()
    {
        // BeginGracefulWindow arms CancelAfter, which is a no-op while a debugger is attached
        // (developers need unlimited stepping time), so the timing this test asserts never
        // happens. Surface that as a real skip rather than a silent pass.
        if (Debugger.IsAttached)
        {
            Assert.Skip("Graceful CancelAfter timing is disabled while a debugger is attached.");
        }

        using var manager = new ConsoleCancellationManager(TimeSpan.FromSeconds(5));
        manager.ConfigureForCommand(TimeSpan.FromMilliseconds(50));

        manager.BeginGracefulWindow();
        // A second call must be idempotent and must not re-arm (which would extend the window).
        manager.BeginGracefulWindow();

        await manager.GracefulShutdownToken.WaitUntilCancelledAsync().DefaultTimeout();

        Assert.True(manager.GracefulShutdownToken.IsCancellationRequested);
    }
}
