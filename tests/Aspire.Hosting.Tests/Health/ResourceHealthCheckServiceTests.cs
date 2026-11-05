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
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Hosting.Tests.Health;

[Trait("Partition", "6")]
public class ResourceHealthCheckServiceTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ImmediatePreparedRestartUsesNewReadiness(bool replicas, bool previousReadyFailed, bool healthCheck)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var readyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var resource = builder.AddResource(new ParentResource("resource"))
            .OnResourceReady(async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref readyCount) == 1)
                {
                    if (previousReadyFailed)
                    {
                        throw new InvalidOperationException("Previous readiness failed.");
                    }
                    return;
                }

                readyEntered.TrySetResult();
                await releaseReady.Task.WaitAsync(cancellationToken);
            });
        if (replicas)
        {
            resource.WithAnnotation(new DcpInstancesAnnotation([
                new DcpInstance("resource-first", "first", 0),
                new DcpInstance("resource-second", "second", 1)
            ]));
        }
        if (healthCheck)
        {
            builder.Services.AddHealthChecks().AddCheck("resource-check", () => HealthCheckResult.Healthy());
            resource.WithHealthCheck("resource-check");
        }
        var dependent = builder.AddResource(new ParentResource("dependent"))
            .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy));
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        await healthService.StartAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pendingStarts = new List<IDisposable>();
        try
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                var initial = await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name,
                    e => e.ResourceId == name && e.Snapshot.ResourceReadyEvent is not null &&
                        (!healthCheck || e.Snapshot.HealthStatus == HealthStatus.Healthy), cts.Token).DefaultTimeout();
                Assert.Equal(previousReadyFailed, initial.Snapshot.ResourceReadyEvent!.EventTask.IsFaulted);
            }

            var previousMonitor = healthService.GetResourceMonitorState(resource.Resource.Name)!;
            await using var pause = await ResourceHealthMonitorPause.CreateAsync(healthService);
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 0 });
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                pendingStarts.Add(await app.ResourceNotifications.BeginResourceStartAsync(resource.Resource, name));
            }
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                app.ResourceNotifications.CompleteResourceStart(resource.Resource, name);
                await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, name,
                    snapshot => snapshot with { State = KnownResourceStates.Running });
            }
            Assert.False(previousMonitor.CancellationToken.IsCancellationRequested);
            await pause.DisposeAsync();

            await readyEntered.Task.DefaultTimeout();
            var wait = app.ResourceNotifications.WaitForDependenciesAsync(dependent.Resource, cts.Token);
            Assert.False(wait.IsCompleted);
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                Assert.True(app.ResourceNotifications.TryGetCurrentState(name, out var current));
                Assert.Null(current.Snapshot.ResourceReadyEvent);
            }
            Assert.True(previousMonitor.CancellationToken.IsCancellationRequested);
            Assert.NotSame(previousMonitor, healthService.GetResourceMonitorState(resource.Resource.Name));

            releaseReady.TrySetResult();
            await wait.DefaultTimeout();
            Assert.Equal(2, readyCount);
        }
        finally
        {
            releaseReady.TrySetResult();
            await cts.CancelAsync();
            foreach (var pendingStart in pendingStarts)
            {
                pendingStart.Dispose();
            }
            await healthService.StopAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ImmediatePreparedRestartIgnoresPreviousReadinessCompletion(bool replicas, bool previousReadyFailed)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var previousReadyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReadyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreviousReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var resource = builder.AddResource(new ParentResource("resource"))
            .OnResourceReady(async (_, _, cancellationToken) =>
            {
                if (Interlocked.Increment(ref readyCount) == 1)
                {
                    previousReadyEntered.TrySetResult();
                    // Deliberately ignore cancellation, as a user subscriber can do.
                    await releasePreviousReady.Task;
                    if (previousReadyFailed)
                    {
                        throw new InvalidOperationException("Previous readiness failed.");
                    }
                    return;
                }

                newReadyEntered.TrySetResult();
                await releaseNewReady.Task.WaitAsync(cancellationToken);
            });
        if (replicas)
        {
            resource.WithAnnotation(new DcpInstancesAnnotation([
                new DcpInstance("resource-first", "first", 0),
                new DcpInstance("resource-second", "second", 1)
            ]));
        }
        var dependent = builder.AddResource(new ParentResource("dependent"))
            .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy));
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        await healthService.StartAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pendingStarts = new List<IDisposable>();
        try
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            await previousReadyEntered.Task.DefaultTimeout();
            var previousMonitor = healthService.GetResourceMonitorState(resource.Resource.Name)!;
            var previousReadyTask = previousMonitor.ResourceReadyTask;
            await using var pause = await ResourceHealthMonitorPause.CreateAsync(healthService);
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 0 });
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                pendingStarts.Add(await app.ResourceNotifications.BeginResourceStartAsync(resource.Resource, name));
            }
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });

            releasePreviousReady.TrySetResult();
            // Await the old monitor's entire publication, not just the subscriber callback.
            await previousReadyTask.DefaultTimeout();
            Assert.False(previousMonitor.CancellationToken.IsCancellationRequested);
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                Assert.True(app.ResourceNotifications.TryGetCurrentState(name, out var current));
                Assert.Null(current.Snapshot.ResourceReadyEvent);
            }

            await pause.DisposeAsync();
            await newReadyEntered.Task.DefaultTimeout();
            var wait = app.ResourceNotifications.WaitForDependenciesAsync(dependent.Resource, cts.Token);
            Assert.False(wait.IsCompleted);
            releaseNewReady.TrySetResult();
            await wait.DefaultTimeout();
            Assert.Equal(2, readyCount);
        }
        finally
        {
            releasePreviousReady.TrySetResult();
            releaseNewReady.TrySetResult();
            await cts.CancelAsync();
            foreach (var pendingStart in pendingStarts)
            {
                pendingStart.Dispose();
            }
            await healthService.StopAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        }
    }

    [Theory]
    [InlineData("Running", false)]
    [InlineData("Running", true)]
    [InlineData("Starting", false)]
    [InlineData("Starting", true)]
    [InlineData("Stopping", false)]
    [InlineData("Stopping", true)]
    public async Task PreparedReplicaRestartRetainsPendingSiblingReadiness(string siblingState, bool healthCheck)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var readyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checkEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new DcpInstance("resource-first", "first", 0),
                new DcpInstance("resource-second", "second", 1)
            ]))
            .OnResourceReady(async (_, _, cancellationToken) =>
            {
                Interlocked.Increment(ref readyCount);
                readyEntered.TrySetResult();
                await releaseReady.Task.WaitAsync(cancellationToken);
            });
        if (healthCheck)
        {
            var checkCount = 0;
            builder.Services.AddHealthChecks().AddAsyncCheck("resource-check", async () =>
            {
                if (Interlocked.Increment(ref checkCount) > 1)
                {
                    checkEntered.TrySetResult();
                    await releaseCheck.Task;
                }
                return HealthCheckResult.Healthy();
            });
            resource.WithHealthCheck("resource-check");
        }
        var marker = builder.AddResource(new ParentResource("marker"))
            .OnResourceReady((_, _, _) =>
            {
                consumerFlushed.TrySetResult();
                return Task.CompletedTask;
            });
        var dependent = builder.AddResource(new ParentResource("dependent"))
            .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy));
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        // Only shorten routine polling; checkEntered gates the restart on an explicit signal.
        healthService.HealthyHealthCheckInterval = TimeSpan.FromMilliseconds(10);
        await healthService.StartAsync(TestContext.Current.CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            await readyEntered.Task.DefaultTimeout();
            var previousMonitor = healthService.GetResourceMonitorState(resource.Resource.Name)!;
            if (healthCheck)
            {
                foreach (var name in resource.Resource.GetResolvedResourceNames())
                {
                    await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name,
                        e => e.ResourceId == name && e.Snapshot.HealthStatus == HealthStatus.Healthy, cts.Token).DefaultTimeout();
                }
                // Keep a real check in flight across the replica restart. Its result must remain
                // valid because the sibling preserves this monitor's readiness generation.
                await checkEntered.Task.DefaultTimeout();
            }
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-second",
                snapshot => snapshot with { State = siblingState });
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-first",
                snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 0 });
            using var pendingStart = await app.ResourceNotifications.BeginResourceStartAsync(resource.Resource, "resource-first");
            app.ResourceNotifications.CompleteResourceStart(resource.Resource, "resource-first");
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });

            // The marker is queued after the restart. Its callback proves the real consumer has
            // processed the preceding notifications before monitor identity is asserted.
            await app.ResourceNotifications.PublishUpdateAsync(marker.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            await consumerFlushed.Task.DefaultTimeout();
            Assert.Same(previousMonitor, healthService.GetResourceMonitorState(resource.Resource.Name));
            Assert.False(previousMonitor.CancellationToken.IsCancellationRequested);
            var wait = app.ResourceNotifications.WaitForDependenciesAsync(dependent.Resource, cts.Token);
            Assert.False(wait.IsCompleted);

            releaseCheck.TrySetResult();
            releaseReady.TrySetResult();
            await wait.DefaultTimeout();
            Assert.Equal(1, readyCount);
        }
        finally
        {
            releaseCheck.TrySetResult();
            releaseReady.TrySetResult();
            await cts.CancelAsync();
            await healthService.StopAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        }
    }

    [Fact]
    public async Task PartialPreparedRestartIgnoresTerminalSiblingFromPreviousGeneration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var consumerFlushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var resource = builder.AddResource(new ParentResource("resource"))
            .WithAnnotation(new DcpInstancesAnnotation([
                new DcpInstance("resource-first", "first", 0),
                new DcpInstance("resource-second", "second", 1)
            ]))
            .OnResourceReady((_, _, _) =>
            {
                Interlocked.Increment(ref readyCount);
                return Task.CompletedTask;
            });
        var marker = builder.AddResource(new ParentResource("marker"))
            .OnResourceReady((_, _, _) =>
            {
                consumerFlushed.TrySetResult();
                return Task.CompletedTask;
            });
        var dependent = builder.AddResource(new ParentResource("dependent"))
            .WithAnnotation(new WaitAnnotation(resource.Resource, WaitType.WaitUntilHealthy));
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var healthService = app.Services.GetRequiredService<ResourceHealthCheckService>();
        await healthService.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            foreach (var name in resource.Resource.GetResolvedResourceNames())
            {
                await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name,
                    e => e.ResourceId == name && e.Snapshot.ResourceReadyEvent is not null,
                    TestContext.Current.CancellationToken).DefaultTimeout();
            }
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Exited, ExitCode = 0 });
            using var firstStart = await app.ResourceNotifications.BeginResourceStartAsync(resource.Resource, "resource-first");
            app.ResourceNotifications.CompleteResourceStart(resource.Resource, "resource-first");
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-first",
                snapshot => snapshot with { State = KnownResourceStates.Running });
            var firstReady = await app.ResourceNotifications.WaitForResourceAsync(resource.Resource.Name,
                e => e.ResourceId == "resource-first" && e.Snapshot.ReadinessGeneration is not null &&
                    e.Snapshot.ResourceReadyEvent is not null, TestContext.Current.CancellationToken).DefaultTimeout();
            var monitor = healthService.GetResourceMonitorState(resource.Resource.Name)!;

            // DCP can finish updating a terminal replica's exit metadata after its sibling restarts.
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-second",
                snapshot => snapshot with { ExitCode = 7 });
            await app.ResourceNotifications.PublishUpdateAsync(marker.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Running });
            await consumerFlushed.Task.DefaultTimeout();
            Assert.Same(monitor, healthService.GetResourceMonitorState(resource.Resource.Name));
            Assert.False(monitor.CancellationToken.IsCancellationRequested);

            using var secondStart = await app.ResourceNotifications.BeginResourceStartAsync(resource.Resource, "resource-second");
            Assert.True(app.ResourceNotifications.TryGetCurrentState("resource-second", out var secondStarting));
            Assert.Same(firstReady.Snapshot.ReadinessGeneration, secondStarting.Snapshot.ReadinessGeneration);
            Assert.Same(firstReady.Snapshot.ResourceReadyEvent, secondStarting.Snapshot.ResourceReadyEvent);
            app.ResourceNotifications.CompleteResourceStart(resource.Resource, "resource-second");
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, "resource-second",
                snapshot => snapshot with { State = KnownResourceStates.Running });
            await app.ResourceNotifications.WaitForDependenciesAsync(dependent.Resource, TestContext.Current.CancellationToken).DefaultTimeout();
            Assert.Equal(2, readyCount);
        }
        finally
        {
            await healthService.StopAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        }
    }

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

    [Fact]
    public async Task ResourceReadyEventFailureIsLoggedToResourceLogger()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var resource = builder.AddResource(new ParentResource("resource"));
        builder.Eventing.Subscribe<ResourceReadyEvent>(resource.Resource, (_, _) => throw new InvalidOperationException("ResourceReadyEvent failed"));

        await using var app = await builder.BuildAsync().DefaultTimeout();
        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();

        await app.StartAsync().DefaultTimeout();

        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null)
        }).DefaultTimeout();

        await foreach (var batch in resourceLoggerService.WatchAsync(resource.Resource).DefaultTimeout())
        {
            var errorLine = batch.FirstOrDefault(l => l.IsErrorMessage && l.Content.Contains("ResourceReadyEvent subscriber for 'resource' failed."));
            if (errorLine.Content is not null)
            {
                Assert.Contains("ResourceReadyEvent failed", errorLine.Content);
                break;
            }
        }

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
