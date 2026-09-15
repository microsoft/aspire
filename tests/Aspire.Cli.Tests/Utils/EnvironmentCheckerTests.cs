// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Utils;

public class EnvironmentCheckerTests
{
    [Fact]
    public async Task CheckAllAsync_TimedOutCheckReportsWarningAndContinues()
    {
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
            checkTimeout: TimeSpan.FromMilliseconds(100),
            totalTimeout: TimeSpan.FromSeconds(5));

        var checkAllTask = checker.CheckAllAsync(TestContext.Current.CancellationToken);
        IReadOnlyList<EnvironmentCheckResult> results;
        try
        {
            await checkStarted.Task.DefaultTimeout();
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
    public async Task CheckAllAsync_TotalTimeoutReportsWarningAndStops()
    {
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
            totalTimeout: TimeSpan.FromMilliseconds(100));

        var results = await checker.CheckAllAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        var timeoutResult = Assert.Single(results);
        Assert.Equal("environment-checks", timeoutResult.Name);
        Assert.Equal(EnvironmentCheckStatus.Warning, timeoutResult.Status);
        Assert.False(subsequentCheckInvoked);
    }

    [Fact]
    public async Task CheckAllAsync_CallerCancellationPropagates()
    {
        var blockedCheck = new TaskCompletionSource<IReadOnlyList<EnvironmentCheckResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new EnvironmentChecker(
            [new TestEnvironmentCheck(0, _ => blockedCheck.Task)],
            NullLogger<EnvironmentChecker>.Instance,
            checkTimeout: TimeSpan.FromSeconds(5),
            totalTimeout: TimeSpan.FromSeconds(10));
        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => checker.CheckAllAsync(cancellationTokenSource.Token));
    }
}