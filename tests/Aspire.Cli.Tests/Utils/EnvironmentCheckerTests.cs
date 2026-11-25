// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Utils;

public class EnvironmentCheckerTests
{
    [Fact]
    public async Task CheckAllAsync_TimedOutCheckReportsWarningAndContinues()
    {
        var timeProvider = new FakeTimeProvider();
        var checkTimeout = TimeSpan.FromMilliseconds(100);
        using var releaseCheck = new ManualResetEventSlim();
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedResult = new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = "completed",
            Status = EnvironmentCheckStatus.Pass,
            Message = "Completed"
        };
        var checker = new EnvironmentChecker(
            [
                new TestEnvironmentCheck(0, _ =>
                {
                    checkStarted.SetResult();
                    releaseCheck.Wait(CancellationToken.None);
                    checkExited.SetResult();
                    return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
                }),
                new TestEnvironmentCheck(1, _ => Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([completedResult])),
            ],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: checkTimeout,
            totalTimeout: TimeSpan.FromSeconds(5),
            timeProvider);

        var checkAllTask = checker.CheckAllAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<EnvironmentCheckResult> results;
        try
        {
            await checkStarted.Task.DefaultTimeout();
            // Only the blocked check's budget elapses. The next check starts with a fresh budget that never
            // elapses, so it can't be reported as timed out however long it takes to be scheduled.
            timeProvider.Advance(checkTimeout);
            results = await checkAllTask.DefaultTimeout();
        }
        finally
        {
            releaseCheck.Set();
        }

        await checkExited.Task.DefaultTimeout();

        Assert.Collection(
            results,
            timeoutResult =>
            {
                Assert.Equal("test-environment", timeoutResult.Name);
                Assert.Equal(EnvironmentCheckStatus.Warning, timeoutResult.Status);
                Assert.Equal(nameof(TestEnvironmentCheck), timeoutResult.Metadata!["checkType"]!.GetValue<string>());
                Assert.Equal(0.1, timeoutResult.Metadata["timeoutSeconds"]!.GetValue<double>());
            },
            result => Assert.Same(completedResult, result));
    }

    [Fact]
    public async Task CheckAllAsync_CheckIsNotTimedOutBeforeItsBudgetElapses()
    {
        var timeProvider = new FakeTimeProvider();
        var checkTimeout = TimeSpan.FromMilliseconds(100);
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completedResult = new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = "completed",
            Status = EnvironmentCheckStatus.Pass,
            Message = "Completed"
        };
        var checker = new EnvironmentChecker(
            [
                new TestEnvironmentCheck(0, async _ =>
                {
                    // Models a check that makes no progress for a while in wall-clock time, for example because
                    // the thread pool is busy on a loaded CI machine. Only the configured budget may time it out.
                    checkStarted.SetResult();
                    await releaseCheck.Task;
                    return [completedResult];
                }),
            ],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: checkTimeout,
            totalTimeout: TimeSpan.FromSeconds(5),
            timeProvider);

        var checkAllTask = checker.CheckAllAsync(TestContext.Current.CancellationToken);
        await checkStarted.Task.DefaultTimeout();

        timeProvider.Advance(checkTimeout - TimeSpan.FromTicks(1));
        Assert.False(checkAllTask.IsCompleted);

        releaseCheck.SetResult();
        var results = await checkAllTask.DefaultTimeout();

        Assert.Same(completedResult, Assert.Single(results));
    }

    [Fact]
    public async Task CheckAllAsync_TotalTimeoutReportsWarningAndStops()
    {
        var timeProvider = new FakeTimeProvider();
        var blockedCheck = new TaskCompletionSource<IReadOnlyList<EnvironmentCheckResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var subsequentCheckInvoked = false;
        var checker = new EnvironmentChecker(
            [
                new TestEnvironmentCheck(0, _ => blockedCheck.Task),
                new TestEnvironmentCheck(1, _ =>
                {
                    subsequentCheckInvoked = true;
                    return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([]);
                }),
            ],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: TimeSpan.FromSeconds(5),
            totalTimeout: TimeSpan.FromMilliseconds(100),
            timeProvider);

        var checkAllTask = checker.CheckAllAsync(TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromMilliseconds(100));
        var results = await checkAllTask.DefaultTimeout();

        var timeoutResult = Assert.Single(results);
        Assert.Equal("environment-checks", timeoutResult.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, timeoutResult.Status);
        Assert.False(subsequentCheckInvoked);
    }

    [Fact]
    public async Task CheckAllAsync_CallerCancellationPropagates()
    {
        var blockedCheck = new TaskCompletionSource<IReadOnlyList<EnvironmentCheckResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeProvider = new SignalingFakeTimeProvider(EnvironmentChecker.s_cancelledCheckCleanupTimeout);
        var checker = new EnvironmentChecker(
            [new TestEnvironmentCheck(0, _ => blockedCheck.Task)],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: TimeSpan.FromMinutes(1),
            totalTimeout: TimeSpan.FromMinutes(2),
            timeProvider);
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var checkAllTask = checker.CheckAllAsync(cancellationTokenSource.Token);

        // A check that ignores cancellation must not keep the caller waiting beyond the cleanup budget.
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(EnvironmentChecker.s_cancelledCheckCleanupTimeout);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkAllTask).DefaultTimeout();
    }

    [Fact]
    public async Task CheckAllAsync_CallerCancellationWaitsForCancelledCheckCleanup()
    {
        var checkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupCompleted = false;
        var timeProvider = new SignalingFakeTimeProvider(EnvironmentChecker.s_cancelledCheckCleanupTimeout);
        var checker = new EnvironmentChecker(
            [
                new TestEnvironmentCheck(0, async cancellationToken =>
                {
                    // Models DcpConnectionChecker: cancellation interrupts the probe, then disposal kills the
                    // DCP process tree it started. The caller must not exit while that cleanup is in progress.
                    try
                    {
                        checkStarted.SetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return [];
                    }
                    finally
                    {
                        cleanupStarted.SetResult();
                        await releaseCleanup.Task;
                        cleanupCompleted = true;
                    }
                }),
            ],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: TimeSpan.FromMinutes(1),
            totalTimeout: TimeSpan.FromMinutes(2),
            timeProvider);
        using var cancellationTokenSource = new CancellationTokenSource();

        var checkAllTask = checker.CheckAllAsync(cancellationTokenSource.Token);
        await checkStarted.Task.DefaultTimeout();

        cancellationTokenSource.Cancel();
        await cleanupStarted.Task.DefaultTimeout();

        // The checker must start waiting for the cleanup rather than completing while it is in progress.
        var firstCompleted = await Task.WhenAny(checkAllTask, timeProvider.TimerCreated.Task).DefaultTimeout();
        Assert.Same(timeProvider.TimerCreated.Task, firstCompleted);
        Assert.False(checkAllTask.IsCompleted);

        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => checkAllTask).DefaultTimeout();
        Assert.True(cleanupCompleted);
    }
}
