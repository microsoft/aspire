// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Hosting.Health;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests.Health;

[Trait("Partition", "6")]
public class ResourceHealthCheckServiceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task ResourcesWithoutHealthCheck_HealthyWhenRunning()
    {
        var testSink = new TestSink();

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));

        var resource = builder.AddResource(new ParentResource("resource"));

        await using var app = await builder.BuildAsync().DefaultTimeout();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Starting, null)
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();
        Assert.Null(startingEvent.Snapshot.HealthStatus);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        var healthyEvent = await app.ResourceNotifications.WaitForResourceHealthyAsync("resource").DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, healthyEvent.Snapshot.HealthStatus);

        await app.StopAsync().TimeoutAfter(TestConstants.LongTimeoutTimeSpan);

        Assert.Contains(testSink.Writes, w => w.Message == "Resource 'resource' has no health checks to monitor.");
    }

    [Fact]
    public async Task ResourcesWithHealthCheck_NotHealthyUntilCheckSucceeds()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().AddAsyncCheck("healthcheck_a", async () =>
        {
            await tcs.Task;
            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a");

        await using var app = await builder.BuildAsync().DefaultTimeout();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Starting, null)
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();
        Assert.Null(startingEvent.Snapshot.HealthStatus);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Running).DefaultTimeout();
        // Resource is unhealthy because it has health reports that haven't run yet.
        Assert.Equal(HealthStatus.Unhealthy, runningEvent.Snapshot.HealthStatus);

        // Allow health check to report success.
        tcs.SetResult();

        await app.ResourceNotifications.WaitForResourceHealthyAsync("resource").DefaultTimeout();

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourcesWithHealthCheck_CreationErrorIsReported()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            "healthcheck_a",
            services => throw new InvalidOperationException("An error!"),
            null,
            null,
            null));

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a");

        await using var app = await builder.BuildAsync().DefaultTimeout();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Starting, null)
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();
        Assert.Null(startingEvent.Snapshot.HealthStatus);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync("resource",
            e => e.Snapshot.State?.Text == KnownResourceStates.Running && e.Snapshot.HealthReports.Single().Status == HealthStatus.Unhealthy).DefaultTimeout();

        Assert.Equal(HealthStatus.Unhealthy, runningEvent.Snapshot.HealthStatus);
        Assert.Equal("Error calling HealthCheckService.", runningEvent.Snapshot.HealthReports.Single().Description);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourcesWithHealthCheck_StopsAndRestartsMonitoringWithResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.Services.AddHealthChecks().AddCheck("healthcheck_a", () =>
        {
            return HealthCheckResult.Healthy();
        });

        var channel = Channel.CreateUnbounded<ResourceReadyEvent>();
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a")
            .OnResourceReady((_, @event, _) =>
            {
                channel.Writer.TryWrite(@event);
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();

        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        await app.ResourceNotifications.WaitForResourceHealthyAsync("resource").DefaultTimeout();

        // Verify resource ready event called.
        var e1 = await channel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(resource.Resource, e1.Resource);

        var monitor1 = healthService.GetResourceMonitorState("resource")!;
        var monitorStoppedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor1.CancellationToken.Register(monitorStoppedTcs.SetResult);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Exited, null)
        });

        // Wait for the health monitor to be stopped.
        await monitorStoppedTcs.Task.DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null),
            HealthReports = [new HealthReportSnapshot("healthcheck_a", Status: null, Description: null, ExceptionText: null)]
        });

        await app.ResourceNotifications.WaitForResourceHealthyAsync("resource").DefaultTimeout();

        var monitor2 = healthService.GetResourceMonitorState("resource")!;
        Assert.NotEqual(monitor1, monitor2);
        Assert.False(monitor2.CancellationToken.IsCancellationRequested);

        // Verify resource ready event called after restart.
        var e2 = await channel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(resource.Resource, e2.Resource);

        await app.StopAsync().DefaultTimeout();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitiallyRunningResourceReadyEventUsesGenerationZero(bool hasHealthCheck)
    {
        var readyEventFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        if (hasHealthCheck)
        {
            builder.Services.AddHealthChecks().AddCheck("healthcheck_a", () => HealthCheckResult.Healthy());
        }

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "ParentResource",
                State = KnownResourceStates.Running,
                Properties = []
            })
            .OnResourceReady((_, _, _) =>
            {
                readyEventFired.TrySetResult();
                return Task.CompletedTask;
            });

        if (hasHealthCheck)
        {
            resource.WithHealthCheck("healthcheck_a");
        }

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await readyEventFired.Task.DefaultTimeout();
        var readyEvent = await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name).DefaultTimeout();

        Assert.Equal(0, readyEvent.Snapshot.ResourceGeneration);
        Assert.Equal(readyEvent.Snapshot.ResourceGeneration, readyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourceReadyEventFromPreviousRunIsNotPublishedAfterRestart()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        builder.Services.AddHealthChecks().AddCheck("healthcheck_a", () => HealthCheckResult.Healthy());

        var firstReadyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventCount = 0;
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a")
            .OnResourceReady(async (_, _, _) =>
            {
                if (Interlocked.Increment(ref readyEventCount) == 1)
                {
                    firstReadyStarted.SetResult();
                    // Simulate a user callback that doesn't observe cancellation and outlives the process.
                    await releaseFirstReady.Task;
                }
                else
                {
                    secondReadyStarted.SetResult();
                    await releaseSecondReady.Task;
                }
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await firstReadyStarted.Task.DefaultTimeout();
        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var firstRunningEvent));
        var firstGeneration = firstRunningEvent.Snapshot.ResourceGeneration;

        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        var firstMonitor = healthService.GetResourceMonitorState(resource.Resource.Name)!;
        var firstMonitorStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstMonitor.CancellationToken.Register(firstMonitorStopped.SetResult);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await firstMonitorStopped.Task.DefaultTimeout();
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => healthService.GetResourceMonitorState(resource.Resource.Name) is null,
            "Wait for the first resource monitor to be removed.");

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
        {
            State = KnownResourceStates.Running,
            HealthReports = [new HealthReportSnapshot("healthcheck_a", Status: null, Description: null, ExceptionText: null)]
        }).DefaultTimeout();

        await secondReadyStarted.Task.DefaultTimeout();
        var secondRunHealthyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent =>
                resourceEvent.Snapshot.ResourceGeneration > firstGeneration &&
                resourceEvent.Snapshot.HealthReports.Single().Status == HealthStatus.Healthy).DefaultTimeout();
        Assert.Null(secondRunHealthyEvent.Snapshot.ResourceReadyEvent);

        var secondWaitTask = app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name);
        Assert.False(secondWaitTask.IsCompleted);

        var firstReadyResultProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        healthService.ResourceReadyEventResultProcessed += (resourceId, generation) =>
        {
            if (resourceId == firstRunningEvent.ResourceId && generation == firstGeneration)
            {
                firstReadyResultProcessed.TrySetResult();
            }
        };
        releaseFirstReady.SetResult();

        await firstReadyResultProcessed.Task.DefaultTimeout();
        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var staleReadyPublishEvent));
        Assert.Equal(secondRunHealthyEvent.Snapshot.ResourceGeneration, staleReadyPublishEvent.Snapshot.ResourceGeneration);
        Assert.Null(staleReadyPublishEvent.Snapshot.ResourceReadyEvent);
        Assert.False(secondWaitTask.IsCompleted);

        releaseSecondReady.SetResult();

        var secondReadyEvent = await secondWaitTask.DefaultTimeout();
        Assert.Equal(secondReadyEvent.Snapshot.ResourceGeneration, secondReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task OlderReplicaReadyCallbackCannotClearNewGenerationReadyEvent()
    {
        var firstReadyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventCount = 0;

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new("resource-one", "one", 0),
                new("resource-two", "two", 1)
            ]))
            .OnResourceReady(async (_, _, _) =>
            {
                switch (Interlocked.Increment(ref readyEventCount))
                {
                    case 1:
                        firstReadyStarted.SetResult();
                        await releaseFirstReady.Task;
                        break;
                    case 2:
                        secondReadyStarted.SetResult();
                        await releaseSecondReady.Task;
                        break;
                    default:
                        throw new InvalidOperationException("ResourceReadyEvent fired more than once for the same generation.");
                }
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await firstReadyStarted.Task.DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await secondReadyStarted.Task.DefaultTimeout();
        releaseSecondReady.SetResult();

        var secondGenerationReadyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent =>
                resourceEvent.ResourceId == "resource-two" &&
                resourceEvent.Snapshot.ResourceGeneration == 2 &&
                resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();
        var secondGenerationReadyTask = secondGenerationReadyEvent.Snapshot.ResourceReadyEvent!.EventTask;

        var staleReadyResultProcessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        healthService.ResourceReadyEventResultProcessed += (resourceId, generation) =>
        {
            if (resourceId == "resource-two" && generation == 1)
            {
                staleReadyResultProcessed.TrySetResult();
            }
        };
        releaseFirstReady.SetResult();

        await staleReadyResultProcessed.Task.DefaultTimeout();
        Assert.True(app.ResourceNotifications.TryGetCurrentState("resource-two", out var stalePublishEvent));
        Assert.Equal(2, stalePublishEvent.Snapshot.ResourceGeneration);
        Assert.Same(secondGenerationReadyTask, stalePublishEvent.Snapshot.ResourceReadyEvent?.EventTask);

        var firstReplicaReadyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-one" && resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();

        Assert.Equal(firstReplicaReadyEvent.Snapshot.ResourceGeneration, firstReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);
        Assert.Equal(2, Volatile.Read(ref readyEventCount));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task HealthCheckFromPreviousReplicaGenerationDoesNotPublishReadyEvent()
    {
        var firstCheckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCheckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthCheckCount = 0;

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().AddAsyncCheck("healthcheck_a", async () =>
        {
            switch (Interlocked.Increment(ref healthCheckCount))
            {
                case 1:
                    firstCheckStarted.SetResult();
                    await releaseFirstCheck.Task;
                    break;
                case 2:
                    secondCheckStarted.SetResult();
                    await releaseSecondCheck.Task;
                    break;
            }

            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new("resource-one", "one", 0),
                new("resource-two", "two", 1)
            ]))
            .WithHealthCheck("healthcheck_a")
            .OnResourceReady((_, _, _) =>
            {
                readyEventStarted.TrySetResult();
                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await firstCheckStarted.Task.DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        releaseFirstCheck.SetResult();

        Assert.NotSame(
            secondCheckStarted.Task,
            await Task.WhenAny(secondCheckStarted.Task, Task.Delay(TimeSpan.FromMilliseconds(100))).DefaultTimeout());

        await secondCheckStarted.Task.DefaultTimeout();
        Assert.False(readyEventStarted.Task.IsCompleted);

        releaseSecondCheck.SetResult();

        await readyEventStarted.Task.DefaultTimeout();
        var secondReplicaReadyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent =>
                resourceEvent.ResourceId == "resource-two" &&
                resourceEvent.Snapshot.ResourceGeneration == 2 &&
                resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();
        Assert.Equal(2, secondReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourceReadyEventIsPublishedForInitialReplicasAsTheyStart()
    {
        var readyEventStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventCount = 0;

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new("resource-one", "one", 0),
                new("resource-two", "two", 1)
            ]))
            .OnResourceReady(async (_, _, _) =>
            {
                Interlocked.Increment(ref readyEventCount);
                readyEventStarted.TrySetResult();
                await readyEventCanComplete.Task;
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await readyEventStarted.Task.DefaultTimeout();
        readyEventCanComplete.SetResult();

        var firstReplicaReadyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-one" && resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();

        Assert.True(app.ResourceNotifications.TryGetCurrentState("resource-two", out var secondReplicaPendingEvent));
        Assert.Equal(0, secondReplicaPendingEvent.Snapshot.ResourceGeneration);
        Assert.Null(secondReplicaPendingEvent.Snapshot.ResourceReadyEvent);

        var secondReplicaReadyTask = app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-two" && resourceEvent.Snapshot.ResourceReadyEvent is not null);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        var secondReplicaReadyEvent = await secondReplicaReadyTask.DefaultTimeout();

        Assert.Equal(1, Volatile.Read(ref readyEventCount));
        Assert.Equal(firstReplicaReadyEvent.Snapshot.ResourceGeneration, firstReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);
        Assert.Equal(secondReplicaReadyEvent.Snapshot.ResourceGeneration, secondReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);
        Assert.Same(firstReplicaReadyEvent.Snapshot.ResourceReadyEvent?.EventTask, secondReplicaReadyEvent.Snapshot.ResourceReadyEvent?.EventTask);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task HealthCheckedReplicaRestartFiresNewReadyEventWhileSiblingRuns()
    {
        var firstReadyEventFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReadyEventFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyEventCount = 0;

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().AddCheck("healthcheck_a", () => HealthCheckResult.Healthy());

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new("resource-one", "one", 0),
                new("resource-two", "two", 1)
            ]))
            .WithHealthCheck("healthcheck_a")
            .OnResourceReady((_, _, _) =>
            {
                if (Interlocked.Increment(ref readyEventCount) == 1)
                {
                    firstReadyEventFired.TrySetResult();
                }
                else
                {
                    secondReadyEventFired.TrySetResult();
                }

                return Task.CompletedTask;
            });

        await using var app = await builder.BuildAsync().DefaultTimeout();
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        healthService.HealthyHealthCheckInterval = TimeSpan.FromHours(1);

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await firstReadyEventFired.Task.DefaultTimeout();
        await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-two" && resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await secondReadyEventFired.Task.DefaultTimeout();
        var restartedReplicaReadyEvent = await app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent =>
                resourceEvent.ResourceId == "resource-two" &&
                resourceEvent.Snapshot.ResourceGeneration == 2 &&
                resourceEvent.Snapshot.ResourceReadyEvent is not null).DefaultTimeout();

        Assert.Equal(restartedReplicaReadyEvent.Snapshot.ResourceGeneration, restartedReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);
        Assert.Equal(2, Volatile.Read(ref readyEventCount));

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourceReadyEventUsesCurrentGenerationForEachReplica()
    {
        var healthCheckCanComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().AddAsyncCheck("healthcheck_a", async () =>
        {
            await healthCheckCanComplete.Task;
            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new("resource-one", "one", 0),
                new("resource-two", "two", 1)
            ]))
            .WithHealthCheck("healthcheck_a");

        await using var app = await builder.BuildAsync().DefaultTimeout();
        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        Assert.True(app.ResourceNotifications.TryGetCurrentState("resource-one", out var firstReplicaRunningEvent));
        Assert.True(app.ResourceNotifications.TryGetCurrentState("resource-two", out var secondReplicaRunningEvent));
        Assert.NotEqual(firstReplicaRunningEvent.Snapshot.ResourceGeneration, secondReplicaRunningEvent.Snapshot.ResourceGeneration);

        var firstReplicaReadyTask = app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-one" && resourceEvent.Snapshot.ResourceReadyEvent is not null);
        var secondReplicaReadyTask = app.ResourceNotifications.WaitForResourceAsync(
            resource.Resource.Name,
            resourceEvent => resourceEvent.ResourceId == "resource-two" && resourceEvent.Snapshot.ResourceReadyEvent is not null);

        healthCheckCanComplete.SetResult();

        var firstReplicaReadyEvent = await firstReplicaReadyTask.DefaultTimeout();
        var secondReplicaReadyEvent = await secondReplicaReadyTask.DefaultTimeout();

        Assert.Equal(firstReplicaReadyEvent.Snapshot.ResourceGeneration, firstReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);
        Assert.Equal(secondReplicaReadyEvent.Snapshot.ResourceGeneration, secondReplicaReadyEvent.Snapshot.ResourceReadyEvent?.ResourceGeneration);

        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task QueuedRunningGenerationInvalidatesHealthCheckBeforeWatcherProcessesEvent()
    {
        var resource = new ParentResource("resource");
        resource.Annotations.Add(new DcpInstancesAnnotation([
            new("resource-one", "one", 0),
            new("resource-two", "two", 1)
        ]));

        var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(resource, "resource-one", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        Assert.True(notifications.TryGetCurrentState("resource-one", out var initialEvent));

        var completionState = new ResourceHealthCheckService.ResourceMonitorState(
            NullLogger.Instance,
            initialEvent,
            CancellationToken.None);
        var captureState = new ResourceHealthCheckService.ResourceMonitorState(
            NullLogger.Instance,
            initialEvent,
            CancellationToken.None);
        var completionGeneration = completionState.BeginHealthCheck(notifications);
        var captureGeneration = captureState.BeginHealthCheck(notifications);

        // Do not call SetLatestEvent. This models the notification store being updated before
        // ResourceHealthCheckService's background watcher consumes the queued notifications.
        await notifications.PublishUpdateAsync(resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await notifications.PublishUpdateAsync(resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        }).DefaultTimeout();
        await notifications.PublishUpdateAsync(resource, "resource-two", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        Assert.False(completionState.IsHealthCheckGenerationCurrent(completionGeneration, notifications));
        Assert.Empty(captureState.CaptureResourceReadyEventTargets(notifications, captureGeneration));

        completionState.StopResourceMonitor();
        captureState.StopResourceMonitor();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task DelayRegistrationOnlyInterruptsForNewGeneration(long resourceGeneration, bool expected)
    {
        var resource = new ParentResource("resource");
        var initialEvent = new ResourceEvent(
            resource,
            resource.Name,
            new CustomResourceSnapshot
            {
                ResourceType = "ParentResource",
                State = KnownResourceStates.Running,
                ResourceGeneration = 1,
                Version = 1,
                Properties = []
            });
        var monitorState = new ResourceHealthCheckService.ResourceMonitorState(
            NullLogger.Instance,
            initialEvent,
            CancellationToken.None);

        monitorState.BeginHealthCheck(ResourceNotificationServiceTestHelpers.Create());
        monitorState.SetLatestEvent(new ResourceEvent(
            resource,
            resource.Name,
            initialEvent.Snapshot with
            {
                ResourceGeneration = resourceGeneration,
                Version = 2
            }));

        var interrupted = await monitorState.DelayAsync(
            initialEvent,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.Equal(expected, interrupted);

        monitorState.StopResourceMonitor();
    }

    [Fact]
    public async Task HealthCheckIntervalSlowsAfterSteadyHealthyState()
    {
        var testSink = new TestSink();

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));

        builder.Services.AddHealthChecks().AddCheck("resource_check", () =>
        {
            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
                              .WithHealthCheck("resource_check");

        using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<ResourceHealthCheckServiceTests>>();
        var rhcs = app.Services.GetRequiredService<ResourceHealthCheckService>();

        var abortTokenSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        await app.StartAsync(abortTokenSource.Token).DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.WaitForResourceHealthyAsync(resource.Resource.Name, abortTokenSource.Token).DefaultTimeout();

        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () =>
            {
                return testSink.Writes.Any(w => w.Message?.Contains($"Resource 'resource' health check monitoring loop starting delay of {rhcs.HealthyHealthCheckInterval}.") ?? false);
            },
            "Wait for healthy delay.", logger);

        await app.StopAsync(abortTokenSource.Token).TimeoutAfter(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task HealthCheckIntervalIncreasesAfterNonHealthyState()
    {
        var testSink = new TestSink();

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddLogging(logging => logging.AddProvider(new TestLoggerProvider(testSink)));

        builder.Services.AddHealthChecks().AddCheck("resource_check", () =>
        {
            return HealthCheckResult.Unhealthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
                              .WithHealthCheck("resource_check");

        using var app = builder.Build();
        var logger = app.Services.GetRequiredService<ILogger<ResourceHealthCheckServiceTests>>();
        var rhcs = app.Services.GetRequiredService<ResourceHealthCheckService>();
        rhcs.NonHealthyHealthCheckStepInterval = TimeSpan.FromMilliseconds(10);

        var abortTokenSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        await app.StartAsync(abortTokenSource.Token).DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        for (var i = 1; i <= 5; i++)
        {
            await AsyncTestHelpers.AssertIsTrueRetryAsync(
                () =>
                {
                    return testSink.Writes.Any(w => w.Message?.Contains($"Resource 'resource' health check monitoring loop starting delay of {(rhcs.NonHealthyHealthCheckStepInterval * i)}.") ?? false);
                },
                "Wait for nonhealthy delay.", logger);
        }

        await app.StopAsync(abortTokenSource.Token).DefaultTimeout();
    }

    [Fact]
    public async Task HealthCheckIntervalDoesNotSlowBeforeSteadyHealthyState()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var channel = Channel.CreateUnbounded<DateTimeOffset>();

        var timeProvider = new FakeTimeProvider(DateTimeOffset.Now);

        builder.Services.AddSingleton<TimeProvider>(timeProvider);
        builder.Services.AddHealthChecks().AddAsyncCheck("resource_check", async () =>
        {
            await channel.Writer.WriteAsync(timeProvider.GetUtcNow());
            return HealthCheckResult.Unhealthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
                              .WithHealthCheck("resource_check");

        using var app = builder.Build();

        var abortTokenSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);

        await app.StartAsync(abortTokenSource.Token).DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();
        await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name, KnownResourceStates.Running, abortTokenSource.Token).DefaultTimeout();

        var firstCheck = await channel.Reader.ReadAsync(abortTokenSource.Token).DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var secondCheck = await channel.Reader.ReadAsync(abortTokenSource.Token).DefaultTimeout();
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        var thirdCheck = await channel.Reader.ReadAsync(abortTokenSource.Token).DefaultTimeout();

        await app.StopAsync(abortTokenSource.Token).DefaultTimeout();

        var duration = thirdCheck - firstCheck;
        Assert.Equal(TimeSpan.FromSeconds(10), duration);
    }

    [Fact]
    public async Task ResourcesWithoutHealthCheckAnnotationsGetReadyEventFired()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var blockAssert = new TaskCompletionSource<ResourceReadyEvent>();
        var resource = builder.AddResource(new ParentResource("resource"))
                              .OnResourceReady((_, @event, _) =>
                              {
                                  blockAssert.SetResult(@event);
                                  return Task.CompletedTask;
                              });

        using var app = builder.Build();
        var pendingStart = app.StartAsync();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        var @event = await blockAssert.Task.DefaultTimeout();
        Assert.Equal(resource.Resource, @event.Resource);

        await pendingStart.DefaultTimeout();
        await app.StopAsync().TimeoutAfter(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task PoorlyImplementedHealthChecksDontCauseMonitoringLoopToCrashout()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var hitCount = 0;
        builder.Services.AddHealthChecks().AddCheck("resource_check", (check) =>
        {
            Interlocked.Increment(ref hitCount);
            throw new InvalidOperationException("Random failure instead of result!");
        });

        var resource = builder.AddResource(new ParentResource("resource"))
                              .WithHealthCheck("resource_check");

        using var app = builder.Build();

        var pendingStart = app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        while (!pendingStart.IsCanceled)
        {
            if (hitCount > 2)
            {
                break;
            }
            await Task.Delay(100);
        }

        await pendingStart; // already has a timeout
        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourceHealthCheckServiceDoesNotRunHealthChecksUnlessResourceIsRunning()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // The custom resource we are using for our test.
        var hitCount = 0;
        var checkStatus = HealthCheckResult.Unhealthy();
        builder.Services.AddHealthChecks().AddCheck("parent_test", () =>
        {
            Interlocked.Increment(ref hitCount);
            return checkStatus;
        });

        // Handle ResourceReadyEvent and use it to control when we drop through to do our assert
        // on the health test being executed.
        var resourceReadyEventFired = new TaskCompletionSource<ResourceReadyEvent>();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithHealthCheck("parent_test")
                            .OnResourceReady((_, @event, _) =>
                            {
                                resourceReadyEventFired.SetResult(@event);
                                return Task.CompletedTask;
                            });

        using var app = builder.Build();
        var pendingStart = app.StartAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        // Verify that the health check does not get run before we move the resource into the
        // the running state. There isn't a great way to do this using a completition source
        // so I'm just going to spin for up to ten seconds to be sure that no local perf
        // issues lead to a false pass here.
        var giveUpAfter = DateTime.UtcNow.AddSeconds(5);
        while (!pendingStart.IsCanceled)
        {
            Assert.Equal(0, hitCount);
            await Task.Delay(100);

            if (DateTime.UtcNow > giveUpAfter)
            {
                break;
            }
        }

        await app.ResourceNotifications.PublishUpdateAsync(parent.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        // Wait for the ResourceReadyEvent
        checkStatus = HealthCheckResult.Healthy();
        await Task.WhenAll([resourceReadyEventFired.Task]).DefaultTimeout();
        Assert.True(hitCount > 0);

        await pendingStart; // already has a timeout
        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task ResourceHealthCheckServiceOnlyRaisesResourceReadyOnce()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        // The custom resource we are using for our test.
        var healthCheckHits = 0;
        builder.Services.AddHealthChecks().AddCheck("parent_test", () =>
        {
            Interlocked.Increment(ref healthCheckHits);
            return HealthCheckResult.Healthy();
        });

        // Handle ResourceReadyEvent and use it to control when we drop through to do our assert
        // on the health test being executed.
        var eventHits = 0;
        var resourceReadyEventFired = new TaskCompletionSource<ResourceReadyEvent>();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithHealthCheck("parent_test")
                            .OnResourceReady((_, @event, _) =>
                            {
                                Interlocked.Increment(ref eventHits);
                                return Task.CompletedTask;
                            });

        using var app = builder.Build();
        var pendingStart = app.StartAsync().DefaultTimeout();
        var rhcs = app.Services.GetRequiredService<ResourceHealthCheckService>();
        rhcs.HealthyHealthCheckInterval = TimeSpan.FromSeconds(1);

        // Get the custom resource to a running state.
        await app.ResourceNotifications.PublishUpdateAsync(parent.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        while (!pendingStart.IsCanceled)
        {
            // We wait for this hit count to reach 3
            // because it means that we've had a chance
            // to fire the ready event twice.
            if (healthCheckHits > 2)
            {
                break;
            }
            await Task.Delay(100);
        }

        Assert.Equal(1, eventHits);

        await pendingStart; // already has a timeout
        await app.StopAsync().DefaultTimeout();
    }

    [Fact]
    public async Task VerifyThatChildResourceWillBecomeHealthyOnceParentBecomesHealthy()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);

        builder.Services.AddHealthChecks().AddCheck("parent_test", () => HealthCheckResult.Healthy());

        var parentReady = new TaskCompletionSource<ResourceReadyEvent>();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithHealthCheck("parent_test")
                            .OnResourceReady((_, @event, _) =>
                            {
                                parentReady.SetResult(@event);
                                return Task.CompletedTask;
                            });

        var childReady = new TaskCompletionSource<ResourceReadyEvent>();
        var child = builder.AddResource(new ChildResource("child", parent.Resource))
                           .OnResourceReady((_, @event, _) =>
                           {
                               childReady.SetResult(@event);
                               return Task.CompletedTask;
                           });

        using var app = builder.Build();
        var pendingStart = app.StartAsync();

        // Get the custom resource to a running state.
        await app.ResourceNotifications.PublishUpdateAsync(parent.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        // ... only need to do this with custom resources, for containers this
        // is handled by app executor. When we get operators we won't need to do
        // this at all.
        await app.ResourceNotifications.PublishUpdateAsync(child.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        var parentReadyEvent = await parentReady.Task.DefaultTimeout();
        Assert.Equal(parentReadyEvent.Resource, parent.Resource);

        var childReadyEvent = await childReady.Task.DefaultTimeout();
        Assert.Equal(childReadyEvent.Resource, child.Resource);

        await pendingStart; // already has a timeout
        await app.StopAsync().TimeoutAfter(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task ResourceNotHealthyIfResourceReadyEventIsRunning()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Create a healthcheck that waits before returning healthy
        builder.Services.AddHealthChecks().AddAsyncCheck("healthcheck_a", async () =>
        {
            await tcs.Task;
            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a");

        await using var app = await builder.BuildAsync().DefaultTimeout();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Starting, null)
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();
        Assert.Null(startingEvent.Snapshot.HealthStatus);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        // Resource is unhealthy because ResourceReadyEvent is running.
        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Running).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, runningEvent.Snapshot.HealthStatus);

        // Allow health check to complete successfully
        tcs.SetResult();

        // Resource is now healthy
        var healthyEvent = await app.ResourceNotifications.WaitForResourceHealthyAsync("resource").DefaultTimeout();
        Assert.Equal(HealthStatus.Healthy, healthyEvent.Snapshot.HealthStatus);

        await app.StopAsync().TimeoutAfter(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task ResourceRemainsUnhealthyIfResourceReadyEventFails()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Services.AddHealthChecks().AddAsyncCheck("healthcheck_a", async () =>
        {
            await tcs.Task;
            return HealthCheckResult.Healthy();
        });

        var resource = builder.AddResource(new ParentResource("resource"))
            .WithHealthCheck("healthcheck_a");

        await using var app = await builder.BuildAsync().DefaultTimeout();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Starting, null)
        }).DefaultTimeout();

        var startingEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Starting).DefaultTimeout();
        Assert.Null(startingEvent.Snapshot.HealthStatus);

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        });

        var runningEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.State?.Text == KnownResourceStates.Running).DefaultTimeout();
        // Resource is unhealthy because ResourceReadyEvent is running.
        Assert.Equal(HealthStatus.Unhealthy, runningEvent.Snapshot.HealthStatus);

        // Fail the ResourceReadyEvent
        tcs.SetException(new InvalidOperationException("ResourceReadyEvent failed"));

        // Resource is still unhealthy
        var unhealthyEvent = await app.ResourceNotifications.WaitForResourceAsync("resource", e => e.Snapshot.HealthStatus == HealthStatus.Unhealthy).DefaultTimeout();
        Assert.Equal(HealthStatus.Unhealthy, unhealthyEvent.Snapshot.HealthStatus);

        await app.StopAsync().TimeoutAfter(TestConstants.LongTimeoutTimeSpan);
    }

    private sealed class ParentResource(string name) : Resource(name)
    {
    }

    private sealed class ChildResource(string name, ParentResource parent) : Resource(name), IResourceWithParent<ParentResource>
    {
        public ParentResource Parent => parent;
    }
}
