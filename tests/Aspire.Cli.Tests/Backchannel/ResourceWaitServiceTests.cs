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
    public async Task WaitUntilDoneAsync_ResolvesCompletionsFromWatchedSnapshotsWithoutReadingPerResource()
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

        // Only the read that reconciles the batch once every wait returned.
        Assert.Equal(1, backchannel.GetResourceSnapshotsCallCount);
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

    [Fact]
    public async Task WaitUntilDoneAsync_IgnoresStaleExitReplayedAfterNewerSnapshot()
    {
        var staleExitObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy", Version = 5 };
            yield return new ResourceSnapshot { Name = "api", State = "Exited", ExitCode = 1, Version = 3 };

            // The watcher only asks for the next snapshot once it has processed the previous one.
            staleExitObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = async (_, _, _, cancellationToken) =>
            {
                await staleExitObserved.Task.WaitAsync(cancellationToken);
                return new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" };
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["api"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(
            [("api", ResourceWaitOutcome.Success, "Running", (int?)null)],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.State, r.ExitCode)));
        Assert.Equal(
            [("api", "Running", 5L)],
            batch.Snapshots.Select(static s => (s.Name, s.State, s.Version)));
    }

    [Fact]
    public async Task WaitUntilDoneAsync_UsesExitCodeFromWaitResponseWhenWatchIsDelayed()
    {
        static async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // The AppHost is still building another snapshot, so the exit never arrives on the watch.
            yield return new ResourceSnapshot { Name = "migrations", State = "Running" };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = false,
                State = "Finished",
                ExitCode = 0,
                ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
            })
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["migrations"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        var result = Assert.Single(batch.Results);
        Assert.Equal((ResourceWaitOutcome.Completed, "Finished", (int?)0, (string?)null), (result.Outcome, result.State, result.ExitCode, result.ErrorMessage));
        Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitUntilDoneAsync_ReadsSnapshotsWhenWatchFallsBehindOlderAppHost()
    {
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(2));

        static async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "migrations", State = "Running" };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            ResourceSnapshots = [new ResourceSnapshot { Name = "migrations", State = "Finished", ExitCode = 0 }],

            // Older AppHosts return neither the state nor the exit code when the healthy wait stops early.
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = false,
                ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
            })
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var batchTask = service.WaitUntilDoneAsync(
            backchannel, ["migrations"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        // Expire the grace period for the watch, as if it stayed behind for longer.
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var batch = await batchTask.DefaultTimeout();

        var result = Assert.Single(batch.Results);
        Assert.Equal((ResourceWaitOutcome.Completed, "Finished", (int?)0), (result.Outcome, result.State, result.ExitCode));
        Assert.Equal(1, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitUntilDoneAsync_LaterFailuresSharePendingSnapshotRead()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterWaitsFailed = 0;
        var allLaterWaitsFailed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<ResourceSnapshot> snapshots =
        [
            new ResourceSnapshot { Name = "migrations1", State = "Finished", ExitCode = 0 },
            new ResourceSnapshot { Name = "migrations2", State = "Finished", ExitCode = 0 },
            new ResourceSnapshot { Name = "migrations3", State = "Finished", ExitCode = 0 }
        ];

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, _) => AsyncEnumerable.Empty<ResourceSnapshot>(),
            GetResourceSnapshotsHandler = async cancellationToken =>
            {
                readStarted.TrySetResult();

                // Keep the first read pending until the other waits failed after it started.
                await allLaterWaitsFailed.Task.WaitAsync(cancellationToken);
                return snapshots;
            },
            WaitForResourceHandler = async (resourceName, _, _, cancellationToken) =>
            {
                if (resourceName != "migrations1")
                {
                    await readStarted.Task.WaitAsync(cancellationToken);
                    if (Interlocked.Increment(ref laterWaitsFailed) == 2)
                    {
                        allLaterWaitsFailed.TrySetResult();
                    }
                }

                return new WaitForResourceResponse { Success = false, ErrorMessage = "Wait failed." };
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["migrations1", "migrations2", "migrations3"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(
            [
                ("migrations1", ResourceWaitOutcome.Completed),
                ("migrations2", ResourceWaitOutcome.Completed),
                ("migrations3", ResourceWaitOutcome.Completed)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome)));
        Assert.Equal(1, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitUntilDoneAsync_WaitsForExitCodeOfResourceThatStoppedAfterItsWaitSucceeded()
    {
        var stopObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "seed", State = "Running", HealthStatus = "Healthy", Version = 1 };

            // DCP publishes the stop before the exit code.
            yield return new ResourceSnapshot { Name = "seed", State = "Finished", HealthStatus = "Healthy", Version = 2 };
            stopObserved.TrySetResult();

            await waitReturned.Task.WaitAsync(cancellationToken);
            yield return new ResourceSnapshot { Name = "seed", State = "Finished", HealthStatus = "Healthy", ExitCode = 0, Version = 3 };
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = async (_, _, _, cancellationToken) =>
            {
                await stopObserved.Task.WaitAsync(cancellationToken);
                waitReturned.TrySetResult();
                return new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" };
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["seed"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        var result = Assert.Single(batch.Results);
        Assert.Equal((ResourceWaitOutcome.Completed, "Finished", (int?)0), (result.Outcome, result.State, result.ExitCode));
    }

    [Fact]
    public async Task WaitUntilDoneAsync_FailedWaitWaitsForDelayedExitCodeWithoutFailingTheBatch()
    {
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(2));
        var readCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exitCodeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy", Version = 1 };
            yield return new ResourceSnapshot { Name = "migrations", State = "Running", Version = 1 };

            // The watch falls behind until the snapshot read showed the stop without an exit code.
            await readCompleted.Task.WaitAsync(cancellationToken);
            yield return new ResourceSnapshot { Name = "migrations", State = "Finished", ExitCode = 0, Version = 3 };
            exitCodeObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            GetResourceSnapshotsHandler = _ =>
            {
                readCompleted.TrySetResult();
                return Task.FromResult<List<ResourceSnapshot>>(
                [
                    new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy", Version = 1 },
                    new ResourceSnapshot { Name = "migrations", State = "Finished", Version = 2 }
                ]);
            },
            WaitForResourceHandler = async (resourceName, _, _, cancellationToken) =>
            {
                if (resourceName == "api")
                {
                    // A failure of migrations would cancel this wait before the exit code arrives.
                    await exitCodeObserved.Task.WaitAsync(cancellationToken);
                    return new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" };
                }

                return new WaitForResourceResponse
                {
                    Success = false,
                    State = "Finished",
                    ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
                };
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var batchTask = service.WaitUntilDoneAsync(
            backchannel, ["migrations", "api"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        // Expire the grace period so the stop is only learned from the snapshot read.
        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var batch = await batchTask.DefaultTimeout();

        Assert.Equal(
            [
                ("migrations", ResourceWaitOutcome.Completed, "Finished", (int?)0),
                ("api", ResourceWaitOutcome.Success, "Running", null)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.State, r.ExitCode)));

        // The read that resolved migrations, then the one that reconciles the batch once every wait returned.
        Assert.Equal(2, backchannel.GetResourceSnapshotsCallCount);
    }

    [Fact]
    public async Task WaitUntilDoneAsync_ReportsTimeoutWhenExitCodeIsStillUnknownAtDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var stopsObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "seed", State = "Running", HealthStatus = "Healthy", Version = 1 };
            yield return new ResourceSnapshot { Name = "migrations", State = "Running", Version = 1 };
            yield return new ResourceSnapshot { Name = "seed", State = "Finished", HealthStatus = "Healthy", Version = 2 };
            yield return new ResourceSnapshot { Name = "migrations", State = "Finished", Version = 2 };
            stopsObserved.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            WaitForResourceHandler = async (resourceName, _, _, cancellationToken) =>
            {
                await stopsObserved.Task.WaitAsync(cancellationToken);
                return resourceName == "seed"
                    ? new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
                    : new WaitForResourceResponse
                    {
                        Success = false,
                        State = "Finished",
                        ErrorMessage = "Stopped waiting for resource 'migrations' to become healthy because it failed to start."
                    };
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var batchTask = service.WaitUntilDoneAsync(
            backchannel, ["seed", "migrations"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        await stopsObserved.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var batch = await batchTask.DefaultTimeout();

        Assert.Equal(
            [
                ("seed", ResourceWaitOutcome.Timeout, "Finished", (int?)null, (string?)null),
                ("migrations", ResourceWaitOutcome.Timeout, "Finished", null, null)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.State, r.ExitCode, r.ErrorMessage)));
    }

    [Fact]
    public async Task WaitForResourcesAsync_ReportsTimeoutWhenAppHostNeverAnswers()
    {
        // The local deadline allows the AppHost 2 seconds past the 30 second budget to answer first.
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(32));
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = NeverAnswerAsync
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var resultsTask = service.WaitForResourcesAsync(
            backchannel, ["api"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(31));
        Assert.False(resultsTask.IsCompleted);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        var results = await resultsTask.DefaultTimeout();

        var result = Assert.Single(results);
        Assert.Equal((ResourceWaitOutcome.Timeout, (string?)null, (string?)null), (result.Outcome, result.State, result.ErrorMessage));
    }

    [Fact]
    public async Task WaitUntilDoneAsync_ReportsTimeoutWhenAppHostNeverAnswers()
    {
        var timeProvider = new SignalingFakeTimeProvider(TimeSpan.FromSeconds(32));
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, _) => AsyncEnumerable.Empty<ResourceSnapshot>(),
            WaitForResourceHandler = NeverAnswerAsync
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var batchTask = service.WaitUntilDoneAsync(
            backchannel, ["api"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken);

        await timeProvider.TimerCreated.Task.DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(32));
        var batch = await batchTask.DefaultTimeout();

        var result = Assert.Single(batch.Results);
        Assert.Equal((ResourceWaitOutcome.Timeout, (string?)null, (string?)null), (result.Outcome, result.State, result.ErrorMessage));
    }

    [Fact]
    public async Task WaitUntilDoneAsync_FailsBatchForExitTheWatchHasNotDeliveredWhenWaitsReturn()
    {
        static async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new ResourceSnapshot { Name = "api", State = "Running", HealthStatus = "Healthy", Version = 1 };
            yield return new ResourceSnapshot { Name = "worker", State = "Running", HealthStatus = "Healthy", Version = 1 };

            // The AppHost already published the crash of api, but the watch is held up and never delivers it.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "api", State = "Exited", HealthStatus = "Unhealthy", ExitCode = 1, Version = 2 },
                new ResourceSnapshot { Name = "worker", State = "Running", HealthStatus = "Healthy", Version = 1 }
            ],
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "Running",
                HealthStatus = "Healthy"
            })
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["api", "worker"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(
            [
                ("api", ResourceWaitOutcome.Failure, "Exited", (int?)1),
                ("worker", ResourceWaitOutcome.Success, "Running", null)
            ],
            batch.Results.Select(static r => (r.ResourceName, r.Outcome, r.State, r.ExitCode)));
        Assert.Equal(1, backchannel.GetResourceSnapshotsCallCount);
        Assert.Equal(
            [("api", "Exited"), ("worker", "Running")],
            batch.Snapshots.Select(static s => (s.Name, s.State)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitUntilDoneAsync_KeepsAmbiguousNameErrorWhenWatchReplayedOneReplica(bool appHostReportsAmbiguity)
    {
        const string ambiguousError = "Resource 'api' is ambiguous because it has multiple replicas. Specify the exact instance name.";
        var replicaReplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<ResourceSnapshot> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // The watch replayed the replica that ran to completion, but not yet its sibling.
            yield return new ResourceSnapshot { Name = "api-abc123", DisplayName = "api", State = "Finished", ExitCode = 0 };

            // The watcher only asks for the next snapshot once it has processed the previous one.
            replicaReplayed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WatchResourceSnapshotsHandler = (_, cancellationToken) => WatchAsync(cancellationToken),
            ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "api-abc123", DisplayName = "api", State = "Finished", ExitCode = 0 },
                new ResourceSnapshot { Name = "api-def456", DisplayName = "api", State = "Running" }
            ],
            WaitForResourceHandler = async (_, _, _, cancellationToken) =>
            {
                await replicaReplayed.Task.WaitAsync(cancellationToken);

                // Older AppHosts only describe the ambiguity in the error message.
                return new WaitForResourceResponse
                {
                    Success = false,
                    ResourceAmbiguous = appHostReportsAmbiguity,
                    ErrorMessage = ambiguousError
                };
            }
        };
        var service = new ResourceWaitService(new FakeTimeProvider(), NullLogger<ResourceWaitService>.Instance);

        var batch = await service.WaitUntilDoneAsync(
            backchannel, ["api"], ResourceWaitTarget.Healthy, 30, TestContext.Current.CancellationToken).DefaultTimeout();

        var result = Assert.Single(batch.Results);
        Assert.Equal(
            (ResourceWaitOutcome.Failure, (string?)null, (int?)null, ambiguousError),
            (result.Outcome, result.State, result.ExitCode, result.ErrorMessage));

        // Only an older AppHost needs the snapshots read to show that several replicas share the name.
        Assert.Equal(appHostReportsAmbiguity ? 0 : 1, backchannel.GetResourceSnapshotsCallCount);
    }

    private static async Task<WaitForResourceResponse> NeverAnswerAsync(string resourceName, string status, int timeoutSeconds, CancellationToken cancellationToken)
    {
        // The AppHost stalled or its reply was lost, so only the caller's deadline ends the call.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return new WaitForResourceResponse { Success = true };
    }
}
