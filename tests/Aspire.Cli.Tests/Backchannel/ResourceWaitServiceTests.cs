// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging.Abstractions;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Backchannel;

public class ResourceWaitServiceTests
{
    [Theory]
    [InlineData("healthy")]
    [InlineData("up")]
    [InlineData("down")]
    public async Task WaitAsync_MapsTargetsAndSuccessfulResponse(string expectedStatus)
    {
        var target = expectedStatus switch
        {
            "healthy" => ResourceWaitTarget.Healthy,
            "up" => ResourceWaitTarget.Up,
            "down" => ResourceWaitTarget.Down,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedStatus))
        };
        string? actualResourceName = null;
        string? actualStatus = null;
        int? actualTimeoutSeconds = null;
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, status, timeoutSeconds, _) =>
            {
                actualResourceName = resourceName;
                actualStatus = status;
                actualTimeoutSeconds = timeoutSeconds;
                return Task.FromResult(new WaitForResourceResponse
                {
                    Success = true,
                    State = "Running"
                });
            }
        };
        var service = new ResourceWaitService(TimeProvider.System, NullLogger<ResourceWaitService>.Instance);

        var result = await service.WaitAsync(
            backchannel,
            "api",
            target,
            timeoutSeconds: 30,
            TestContext.Current.CancellationToken);

        Assert.Equal("api", actualResourceName);
        Assert.Equal(expectedStatus, actualStatus);
        Assert.Equal(30, actualTimeoutSeconds);
        Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
        Assert.Equal("Running", result.State);
        Assert.False(result.ResourceNotFound);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task WaitAsync_TreatsFailedToStartAsFailureForDownTarget()
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "FailedToStart"
            })
        };
        var service = new ResourceWaitService(TimeProvider.System, NullLogger<ResourceWaitService>.Instance);

        var result = await service.WaitAsync(
            backchannel,
            "api",
            ResourceWaitTarget.Down,
            timeoutSeconds: 30,
            TestContext.Current.CancellationToken);

        Assert.Equal(ResourceWaitOutcome.Failure, result.Outcome);
        Assert.Equal("FailedToStart", result.State);
    }

    [Theory]
    [InlineData(true, false, "Failure")]
    [InlineData(false, true, "Timeout")]
    [InlineData(false, false, "Failure")]
    public async Task WaitAsync_MapsUnsuccessfulResponses(
        bool resourceNotFound,
        bool timedOut,
        string expectedOutcomeName)
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = false,
                State = "Waiting",
                ResourceNotFound = resourceNotFound,
                TimedOut = timedOut,
                ErrorMessage = "Wait failed."
            })
        };
        var service = new ResourceWaitService(TimeProvider.System, NullLogger<ResourceWaitService>.Instance);

        var result = await service.WaitAsync(
            backchannel,
            "api",
            ResourceWaitTarget.Healthy,
            timeoutSeconds: 30,
            TestContext.Current.CancellationToken);

        Assert.Equal(Enum.Parse<ResourceWaitOutcome>(expectedOutcomeName), result.Outcome);
        Assert.Equal("Waiting", result.State);
        Assert.Equal(resourceNotFound, result.ResourceNotFound);
        Assert.Equal("Wait failed.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(-300)]
    [InlineData(300)]
    public async Task WaitForResourcesAsync_UtcClockChangesDoNotChangeTheSharedBudget(int clockChangeSeconds)
    {
        var timeProvider = new AdjustableTimeProvider();
        var timeouts = new ConcurrentQueue<int>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, timeoutSeconds, _) =>
            {
                timeouts.Enqueue(timeoutSeconds);
                if (resourceName == "api")
                {
                    // Change UTC during dispatch without adding that jump to elapsed time.
                    timeProvider.Elapsed += TimeSpan.FromSeconds(1);
                    timeProvider.UtcNow += TimeSpan.FromSeconds(1 + clockChangeSeconds);
                }

                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var results = await service.WaitForResourcesAsync(
            backchannel, ["api", "worker"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        Assert.Equal([30, 29], timeouts);
        Assert.Collection(results,
            result =>
            {
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
                Assert.Equal(TimeSpan.FromSeconds(1), result.Elapsed);
            },
            result =>
            {
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
                Assert.Equal(TimeSpan.Zero, result.Elapsed);
            });
    }

    [Theory]
    [InlineData(250, 30)]
    [InlineData(1250, 29)]
    [InlineData(30000, 0)]
    [InlineData(31000, 0)]
    public async Task WaitForResourcesAsync_RoundsRemainingBudgetAndSkipsExpiredRequests(int elapsedMilliseconds, int expectedRemainingSeconds)
    {
        var timeProvider = new AdjustableTimeProvider();
        var timeouts = new ConcurrentQueue<int>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, timeoutSeconds, _) =>
            {
                timeouts.Enqueue(timeoutSeconds);
                if (resourceName == "api")
                {
                    var elapsed = TimeSpan.FromMilliseconds(elapsedMilliseconds);
                    timeProvider.Elapsed += elapsed;
                    timeProvider.UtcNow += elapsed;
                }

                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var results = await service.WaitForResourcesAsync(
            backchannel, ["api", "worker"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        int[] expectedTimeouts = expectedRemainingSeconds == 0 ? [30] : [30, expectedRemainingSeconds];
        Assert.Equal(expectedTimeouts, timeouts);
        Assert.Collection(results,
            result => Assert.Equal(ResourceWaitOutcome.Success, result.Outcome),
            result =>
            {
                Assert.Equal(expectedRemainingSeconds == 0 ? ResourceWaitOutcome.Timeout : ResourceWaitOutcome.Success, result.Outcome);
                Assert.Equal(TimeSpan.Zero, result.Elapsed);
            });
    }

    [Fact]
    public async Task WaitUntilDoneAsync_ResolvesCompletionsFromWatchedSnapshotsWithoutReading()
    {
        var migrationWaitsFailed = 0;
        var bothMigrationWaitsFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy" };
            yield return new ResourceSnapshot { Name = "migrations1", State = "Running" };
            yield return new ResourceSnapshot { Name = "migrations2", State = "Running" };

            // Deliver the exits only after both failed waits returned, so completion has to wait for the watch.
            await bothMigrationWaitsFailed.Task.WaitAsync(cancellationToken);
            yield return new ResourceSnapshot { Name = "migrations1", State = "Finished", ExitCode = 0 };
            yield return new ResourceSnapshot { Name = "migrations2", State = "Finished", ExitCode = 0 };

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                if (resourceName == "api")
                {
                    return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" });
                }

                if (Interlocked.Increment(ref migrationWaitsFailed) == 2)
                {
                    bothMigrationWaitsFailed.TrySetResult();
                }

                return Task.FromResult(new WaitForResourceResponse
                {
                    Success = false,
                    ErrorMessage = $"Stopped waiting for resource '{resourceName}' to become healthy because it failed to start."
                });
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["migrations1", "api", "migrations2"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(
            [
                ("migrations1", ResourceWaitOutcome.Completed, "Finished", (int?)0),
                ("api", ResourceWaitOutcome.Success, "Running", null),
                ("migrations2", ResourceWaitOutcome.Completed, "Finished", 0)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.State, r.ExitCode)));
        Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
        Assert.Equal(
            [("api", "Running"), ("migrations1", "Finished"), ("migrations2", "Finished")],
            batch.Snapshots.Select(static s => (s.Name, s.State)));
    }

    [Fact]
    public async Task WaitUntilDoneAsync_SharesSnapshotReadWhenWatchIsUnavailable()
    {
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            // An empty watch behaves like an AppHost that ended the stream.
            WatchResourceSnapshotsHandler = (_, _) => AsyncEnumerable.Empty<ResourceSnapshot>(),
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "migrations1", State = "Finished", ExitCode = 0 },
                new ResourceSnapshot { Name = "migrations2", State = "Finished", ExitCode = 0 },
                new ResourceSnapshot { Name = "seed", State = "Exited", ExitCode = 2 }
            ],
            WaitForResourceHandler = (resourceName, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = false,
                ErrorMessage = $"Stopped waiting for resource '{resourceName}' to become healthy because it failed to start."
            })
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["migrations1", "migrations2", "seed"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(
            [
                ("migrations1", ResourceWaitOutcome.Completed, (int?)0),
                ("migrations2", ResourceWaitOutcome.Completed, 0),
                ("seed", ResourceWaitOutcome.Failure, 2)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.ExitCode)));
        Assert.Equal(1, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitUntilDoneAsync_PropagatesCallerCancellationWhileResolvingCompletion()
    {
        using var callerCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var waitFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // The live watch never shows the resource stopping, so resolution keeps waiting for it.
            yield return new ResourceSnapshot { Name = "migrations", State = "Running" };

            await waitFailed.Task.WaitAsync(cancellationToken);
            await callerCts.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = (_, _, _, _) =>
            {
                waitFailed.TrySetResult();
                return Task.FromResult(new WaitForResourceResponse { Success = false, ErrorMessage = "Wait failed." });
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitUntilDoneAsync(
            backchannel, ["migrations"], ResourceWaitTarget.Healthy, 30, callerCts.Token)).DefaultTimeout();
        Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
    }
}
