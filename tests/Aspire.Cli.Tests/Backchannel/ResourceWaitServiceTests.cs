// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
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
        var timeProvider = new FakeTimeProvider();
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
                timeProvider.Advance(TimeSpan.FromMilliseconds(1250));
                return Task.FromResult(new WaitForResourceResponse
                {
                    Success = true,
                    State = "Running",
                    HealthStatus = "Healthy"
                });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

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
        Assert.Equal("api", result.ResourceName);
        Assert.Equal("Running", result.State);
        Assert.Equal("Healthy", result.Health);
        Assert.False(result.ResourceNotFound);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(TimeSpan.FromMilliseconds(1250), result.Elapsed);
    }

    [Fact]
    public async Task WaitAsync_TreatsFailedToStartAsFailureForDownTarget()
    {
        var timeProvider = new FakeTimeProvider();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "FailedToStart"
            })
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

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
        var timeProvider = new FakeTimeProvider();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (_, _, _, _) => Task.FromResult(new WaitForResourceResponse
            {
                Success = false,
                State = "Waiting",
                HealthStatus = "Unhealthy",
                ResourceNotFound = resourceNotFound,
                TimedOut = timedOut,
                ErrorMessage = "Wait failed."
            })
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var result = await service.WaitAsync(
            backchannel,
            "api",
            ResourceWaitTarget.Healthy,
            timeoutSeconds: 30,
            TestContext.Current.CancellationToken);

        Assert.Equal(Enum.Parse<ResourceWaitOutcome>(expectedOutcomeName), result.Outcome);
        Assert.Equal("api", result.ResourceName);
        Assert.Equal("Waiting", result.State);
        Assert.Equal("Unhealthy", result.Health);
        Assert.Equal(resourceNotFound, result.ResourceNotFound);
        Assert.Equal("Wait failed.", result.ErrorMessage);
    }

    [Fact]
    public async Task WaitForResourcesAsync_WaitsConcurrentlyWithOneDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeouts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = async (resourceName, _, timeoutSeconds, cancellationToken) =>
            {
                timeouts[resourceName] = timeoutSeconds;
                if (resourceName == "first")
                {
                    // The handler runs synchronously until its first await, so the second dispatch
                    // must calculate its timeout from the deadline captured before the first dispatch.
                    timeProvider.Advance(TimeSpan.FromMilliseconds(1100));
                    firstEntered.TrySetResult();
                }
                else
                {
                    secondEntered.TrySetResult();
                }

                await release.Task.WaitAsync(cancellationToken);
                return new WaitForResourceResponse { Success = true, State = "Running" };
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var waitTask = service.WaitForResourcesAsync(
            backchannel,
            ["first", "second"],
            ResourceWaitTarget.Up,
            timeoutSeconds: 10,
            TestContext.Current.CancellationToken);

        await Task.WhenAll(firstEntered.Task, secondEntered.Task).DefaultTimeout();
        Assert.False(waitTask.IsCompleted);

        release.TrySetResult();
        var results = await waitTask.DefaultTimeout();

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("first", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
            },
            result =>
            {
                Assert.Equal("second", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
            });
        Assert.Equal(10, timeouts["first"]);
        Assert.Equal(9, timeouts["second"]);
    }

    [Fact]
    public async Task WaitForResourcesAsync_UsesMonotonicSharedBudget()
    {
        var timeProvider = new ManualMonotonicTimeProvider();
        var timeouts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, timeoutSeconds, _) =>
            {
                timeouts[resourceName] = timeoutSeconds;
                if (resourceName == "first")
                {
                    timeProvider.Advance(TimeSpan.FromMilliseconds(1250));
                }

                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        _ = await service.WaitForResourcesAsync(
            backchannel,
            ["first", "second"],
            ResourceWaitTarget.Up,
            timeoutSeconds: 10,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(10, timeouts["first"]);
        Assert.Equal(9, timeouts["second"]);
    }

    [Fact]
    public async Task WaitForResourcesAsync_ReturnsTimeoutWithoutRpcAfterDeadlineExpires()
    {
        var timeProvider = new FakeTimeProvider();
        var calledResources = new ConcurrentQueue<string>();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                calledResources.Enqueue(resourceName);
                timeProvider.Advance(TimeSpan.FromSeconds(10));
                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var results = await service.WaitForResourcesAsync(
            backchannel,
            ["first", "expired"],
            ResourceWaitTarget.Up,
            timeoutSeconds: 10,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("first", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
            },
            result =>
            {
                Assert.Equal("expired", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Timeout, result.Outcome);
            });
        Assert.Collection(calledResources, resourceName => Assert.Equal("first", resourceName));
    }

    [Fact]
    public async Task WaitForResourcesAsync_PropagatesCancellationWhenDeadlineExpired()
    {
        var timeProvider = new FakeTimeProvider();
        var calledResources = new ConcurrentQueue<string>();
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, _, _) =>
            {
                calledResources.Enqueue(resourceName);
                if (resourceName == "first")
                {
                    timeProvider.Advance(TimeSpan.FromSeconds(11));
                    cancellationTokenSource.Cancel();
                }

                return Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" });
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var waitTask = service.WaitForResourcesAsync(
            backchannel,
            ["first", "expired"],
            ResourceWaitTarget.Up,
            timeoutSeconds: 10,
            cancellationTokenSource.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask).DefaultTimeout();
        Assert.Collection(calledResources, resourceName => Assert.Equal("first", resourceName));
    }

    [Fact]
    public async Task WaitForResourcesAsync_ConvertsIndependentExceptionsToFailures()
    {
        var timeProvider = new FakeTimeProvider();
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = (resourceName, _, _, _) => resourceName == "broken"
                ? Task.FromException<WaitForResourceResponse>(new InvalidOperationException("Wait failed."))
                : Task.FromResult(new WaitForResourceResponse { Success = true, State = "Running" })
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);

        var results = await service.WaitForResourcesAsync(
            backchannel,
            ["broken", "healthy"],
            ResourceWaitTarget.Healthy,
            timeoutSeconds: 30,
            TestContext.Current.CancellationToken);

        Assert.Collection(
            results,
            result =>
            {
                Assert.Equal("broken", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Failure, result.Outcome);
                Assert.Null(result.ErrorMessage);
            },
            result =>
            {
                Assert.Equal("healthy", result.ResourceName);
                Assert.Equal(ResourceWaitOutcome.Success, result.Outcome);
                Assert.Equal("Running", result.State);
            });
    }

    [Fact]
    public async Task WaitForResourcesAsync_PropagatesCancellation()
    {
        var timeProvider = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backchannel = new TestAppHostAuxiliaryBackchannel
        {
            WaitForResourceHandler = async (_, _, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return new WaitForResourceResponse { Success = true };
            }
        };
        var service = new ResourceWaitService(timeProvider, NullLogger<ResourceWaitService>.Instance);
        using var cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var waitTask = service.WaitForResourcesAsync(
            backchannel,
            ["api"],
            ResourceWaitTarget.Healthy,
            timeoutSeconds: 30,
            cancellationTokenSource.Token);

        await entered.Task.DefaultTimeout();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask).DefaultTimeout();
    }

    private sealed class ManualMonotonicTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            throw new InvalidOperationException("Wall-clock time must not be used for the shared timeout budget.");
        }

        public override long GetTimestamp()
        {
            return Interlocked.Read(ref _timestamp);
        }

        public void Advance(TimeSpan elapsed)
        {
            Interlocked.Add(ref _timestamp, elapsed.Ticks);
        }
    }
}
