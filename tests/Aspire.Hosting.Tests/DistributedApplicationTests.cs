// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001

using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Aspire.TestUtilities;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Redis;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Testing.Tests;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using k8s.Models;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit.Sdk;
using TestConstants = Microsoft.AspNetCore.InternalTesting.TestConstants;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.Tests;

public class DistributedApplicationTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    private const string ReplicaIdRegex = @"[\w]+"; // Matches a replica ID that is part of a resource name.
    private const string AspireTestContainerRegistry = "netaspireci.azurecr.io";
    private const string RedisImageSource = $"{AspireTestContainerRegistry}/{RedisContainerImageTags.Image}:{RedisContainerImageTags.Tag}";

    public DistributedApplicationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task RegisteredLifecycleHookIsExecutedWhenRunAsynchronously()
    {
        var exceptionMessage = "Exception from lifecycle hook to prove it ran!";

        using var testProgram = CreateTestProgram("lifecycle-hook-executed-async");
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.AddLifecycleHook((sp) =>
        {
            return new CallbackLifecycleHook((appModel, cancellationToken) =>
            {
                Assert.NotNull(appModel);

                throw new DistributedApplicationException(exceptionMessage);
            });
        });
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(1));
            await testProgram.RunAsync(cts.Token);
        }).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        Assert.Equal(exceptionMessage, ex.Message);
    }

    [Fact]
    public async Task MultipleRegisteredLifecycleHooksAreExecuted()
    {
        var exceptionMessage = "Exception from lifecycle hook to prove it ran!";

        var signal = (FirstHookExecuted: false, SecondHookExecuted: false);

        using var testProgram = CreateTestProgram("multiple-lifecycle-hooks");

        // Lifecycle hook 1
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.AddLifecycleHook((sp) =>
        {
            return new CallbackLifecycleHook((app, cancellationToken) =>
            {
                signal.FirstHookExecuted = true;
                return Task.CompletedTask;
            });
        });
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        // Lifecycle hook 2
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.AddLifecycleHook((sp) =>
        {
            return new CallbackLifecycleHook((app, cancellationToken) =>
            {
                signal.SecondHookExecuted = true;

                // We still want to throw on the second one to block startup.
                throw new DistributedApplicationException(exceptionMessage);
            });
        });
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(async () =>
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMinutes(1));
            await testProgram.RunAsync(cts.Token);
        }).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        Assert.Equal(exceptionMessage, ex.Message);
        Assert.True(signal.FirstHookExecuted);
        Assert.True(signal.SecondHookExecuted);
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/10086")]
    public async Task StartResourceForcesStart()
    {
        using var testProgram = CreateTestProgram("force-resource-start");
        testProgram.AppBuilder.Services.AddHealthChecks().AddCheck("dummy_healthcheck", () => HealthCheckResult.Unhealthy());

        var dependentResourceName = "force-resource-start-serviceb";

        testProgram.ServiceABuilder.WithHealthCheck("dummy_healthcheck");
        testProgram.ServiceBBuilder.WaitFor(testProgram.ServiceABuilder);

        using var app = testProgram.Build();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var logger = app.Services.GetRequiredService<ILogger<DistributedApplicationTests>>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var token = cts.Token;

        var startTask = app.StartAsync(token);

        var resourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Waiting, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Force resource to start.");
        await orchestrator.StartResourceAsync(resourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Stop resource.");
        await orchestrator.StopResourceAsync(resourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Finished, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Start resource (into waiting state)");
        var restartResourceTask = orchestrator.StartResourceAsync(resourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Waiting, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Force resource to start.");
        await orchestrator.StartResourceAsync(resourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await restartResourceTask.DefaultTimeout(TestConstants.LongTimeoutDuration);
        await startTask.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task ExplicitStart_StartExecutable()
    {
        const string testName = "explicit-start-executable";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        var notStartedResourceName = $"{testName}-servicea";
        var dependentResourceName = $"{testName}-serviceb";

        testProgram.ServiceABuilder.WithExplicitStart();
        testProgram.ServiceBBuilder.WaitFor(testProgram.ServiceABuilder);

        using var app = testProgram.Build();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var logger = app.Services.GetRequiredService<ILogger<DistributedApplicationTests>>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var token = cts.Token;

        var startTask = app.StartAsync(token);

        // On start, one resource won't be started and the other is waiting on it.
        var notStartedResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        var dependentResourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Waiting, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        // Source should be populated on non-started resources.
        Assert.Contains("TestProject.ServiceA.csproj", notStartedResourceEvent.Snapshot.Properties.Single(p => p.Name == "project.path").Value?.ToString());
        Assert.Contains("TestProject.ServiceB.csproj", dependentResourceEvent.Snapshot.Properties.Single(p => p.Name == "project.path").Value?.ToString());

        Assert.Collection(notStartedResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5156", u.Url);
            Assert.Equal("http", u.Name);
            Assert.True(u.IsInactive);
        });
        Assert.Collection(dependentResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5254", u.Url);
            Assert.Equal("http", u.Name);
            Assert.True(u.IsInactive);
        });

        logger.LogInformation("Start explicit start resource.");
        await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        var runningResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        Assert.Collection(runningResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5156", u.Url);
            Assert.Equal("http", u.Name);
        });

        // Dependent resource should now run.
        var dependentResourceRunningEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        Assert.Collection(dependentResourceRunningEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5254", u.Url);
            Assert.Equal("http", u.Name);
        });

        logger.LogInformation("Stop resource.");
        await orchestrator.StopResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Finished, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Start resource again");
        await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await startTask.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    public async Task BeforeResourceStartedEvent_NotFiredForExplicitStartOnInitialCreation()
    {
        const string testName = "before-start-event-explicit-start";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        var explicitStartEventCount = 0;
        var normalEventCount = 0;

        testProgram.ServiceABuilder.WithExplicitStart()
            .OnBeforeResourceStarted((_, _, _) =>
            {
                Interlocked.Increment(ref explicitStartEventCount);
                return Task.CompletedTask;
            });

        // ServiceB is a normal resource without explicit start
        testProgram.ServiceBBuilder
            .OnBeforeResourceStarted((_, _, _) =>
            {
                Interlocked.Increment(ref normalEventCount);
                return Task.CompletedTask;
            });

        var resourcesCreatedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        testProgram.AppBuilder.Eventing.Subscribe<AfterResourcesCreatedEvent>((_, _) =>
        {
            resourcesCreatedTcs.SetResult();
            return Task.CompletedTask;
        });

        using var app = testProgram.Build();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestTimeout);
        var token = cts.Token;

        // Start app in background - it won't complete because of explicit start resource
        _ = app.StartAsync(token);

        var normalResourceName = $"{testName}-serviceb";
        var explicitStartResourceName = $"{testName}-servicea";

        // Wait for normal resource to be running
        await rns.WaitForResourceAsync(normalResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Wait for explicit start resource to be NotStarted
        await rns.WaitForResourceAsync(explicitStartResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Wait for resource to finish being created.
        await resourcesCreatedTcs.Task.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Verify BeforeResourceStartedEvent was fired for normal resource but NOT for explicit start resource
        Assert.Equal(1, normalEventCount);
        Assert.Equal(0, explicitStartEventCount);

        await app.StopAsync(token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    public async Task BeforeResourceStartedEvent_FiredWhenExplicitStartResourceIsManuallyStarted()
    {
        const string testName = "before-start-event-manual-start";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        var eventCount = 0;

        testProgram.ServiceABuilder.WithExplicitStart()
            .OnBeforeResourceStarted((_, _, _) =>
            {
                Interlocked.Increment(ref eventCount);
                return Task.CompletedTask;
            });

        using var app = testProgram.Build();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestTimeout);
        var token = cts.Token;

        // Start app in background
        var startTask = app.StartAsync(token);

        var explicitStartResourceName = $"{testName}-servicea";

        // Wait for the resource to be in NotStarted state
        var notStartedResourceEvent = await rns.WaitForResourceAsync(explicitStartResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        // Verify no event has been fired yet (initial creation should not fire the event)
        Assert.Equal(0, eventCount);

        // Manually start the resource
        await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Wait for the resource to be running
        await rns.WaitForResourceAsync(explicitStartResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        await startTask.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        // Verify that BeforeResourceStartedEvent WAS fired when manually started
        Assert.Equal(1, eventCount);

        await app.StopAsync(token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    public async Task BeforeResourceStartedEvent_FiredForNormalResourcesOnInitialStartup()
    {
        const string testName = "before-start-event-normal-resource";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        var eventCount = 0;

        // Don't call WithExplicitStart() - this is a normal resource
        testProgram.ServiceABuilder
            .OnBeforeResourceStarted((_, _, _) =>
            {
                Interlocked.Increment(ref eventCount);
                return Task.CompletedTask;
            });

        using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        Assert.Equal(1, eventCount);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task ExplicitStart_StartContainer()
    {
        const string testName = "explicit-start-container";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        var notStartedResourceName = $"{testName}-redis";
        var dependentResourceName = $"{testName}-serviceb";

        var containerBuilder = AddRedisContainer(testProgram.AppBuilder, notStartedResourceName)
            .WithEndpoint(port: 6379, targetPort: 6379, name: "tcp", env: "REDIS_PORT")
            .WithExplicitStart();

        containerBuilder.WithExplicitStart();
        testProgram.ServiceBBuilder.WaitFor(containerBuilder);

        using var app = testProgram.Build();
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var logger = app.Services.GetRequiredService<ILogger<DistributedApplicationTests>>();

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var token = cts.Token;

        var startTask = app.StartAsync(token);

        // On start, one resource won't be started and the other is waiting on it.
        var notStartedResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        var dependentResourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Waiting, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        Assert.Collection(notStartedResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("tcp://localhost:6379", u.Url);
            Assert.True(u.IsInactive);
        });
        Assert.Collection(dependentResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5254", u.Url);
            Assert.Equal("http", u.Name);
            Assert.True(u.IsInactive);
        });

        // Source should be populated on non-started resources.
        Assert.Equal(RedisImageSource, notStartedResourceEvent.Snapshot.Properties.Single(p => p.Name == "container.image").Value?.ToString());
        Assert.Contains("TestProject.ServiceB.csproj", dependentResourceEvent.Snapshot.Properties.Single(p => p.Name == "project.path").Value?.ToString());

        logger.LogInformation("Start explicit start resource.");
        await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        var runningResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        Assert.Collection(runningResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("tcp://localhost:6379", u.Url);
        });

        // Dependent resource should now run.
        var dependentRunningResourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        Assert.Collection(dependentRunningResourceEvent.Snapshot.Urls, u =>
        {
            Assert.Equal("http://localhost:5254", u.Url);
            Assert.Equal("http", u.Name);
        });

        logger.LogInformation("Stop resource.");
        await orchestrator.StopResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Exited, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        logger.LogInformation("Start resource again");
        await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await startTask.DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
        await app.StopAsync(token).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);
    }

    [Fact]
    [RequiresDocker]
    public async Task ExplicitStart_StartPersistentContainer()
    {
        foreach (var firstRun in new[] { true, false })
        {
            const string testName = "explicit-start-persistent-container";
            using var testProgram = CreateTestProgram(testName, randomizePorts: false);

            var notStartedResourceName = $"{testName}-redis";
            var dependentResourceName = $"{testName}-serviceb";

            var containerBuilder = AddRedisContainer(testProgram.AppBuilder, notStartedResourceName)
                .WithContainerName(notStartedResourceName)
                .WithLifetime(ContainerLifetime.Persistent)
                .WithEndpoint(port: 6379, targetPort: 6379, name: "tcp", env: "REDIS_PORT")
                .WithExplicitStart();

            containerBuilder.WithExplicitStart();
            testProgram.ServiceBBuilder.WaitFor(containerBuilder);

            using var app = testProgram.Build();
            var rns = app.Services.GetRequiredService<ResourceNotificationService>();
            var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
            var logger = app.Services.GetRequiredService<ILogger<DistributedApplicationTests>>();

            using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.ExtraLongTimeoutDuration);
            var token = cts.Token;

            var startTask = app.StartAsync(token);

            ResourceEvent? notStartedResourceEvent = null;
            ResourceEvent? dependentResourceEvent = null;
            if (firstRun)
            {
                // On start, one resource won't be started and the other is waiting on it.
                notStartedResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.NotStarted, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
                dependentResourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Waiting, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);

                Assert.Collection(notStartedResourceEvent.Snapshot.Urls, u =>
                {
                    Assert.Equal("tcp://localhost:6379", u.Url);
                    Assert.True(u.IsInactive);
                });
                Assert.Collection(dependentResourceEvent.Snapshot.Urls, u =>
                {
                    Assert.Equal("http://localhost:5254", u.Url);
                    Assert.Equal("http", u.Name);
                    Assert.True(u.IsInactive);
                });

                // Source should be populated on non-started resources.
                Assert.Equal(RedisImageSource, notStartedResourceEvent.Snapshot.Properties.Single(p => p.Name == "container.image").Value?.ToString());
                Assert.Contains("TestProject.ServiceB.csproj", dependentResourceEvent.Snapshot.Properties.Single(p => p.Name == "project.path").Value?.ToString());

                logger.LogInformation("Start explicit start resource.");
                await orchestrator.StartResourceAsync(notStartedResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            }

            var runningResourceEvent = await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            Assert.Collection(runningResourceEvent.Snapshot.Urls, u =>
            {
                Assert.Equal("tcp://localhost:6379", u.Url);
            });

            // Dependent resource should now run.
            var dependentRunningResourceEvent = await rns.WaitForResourceAsync(dependentResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            Assert.Collection(dependentRunningResourceEvent.Snapshot.Urls, u =>
            {
                Assert.Equal("http://localhost:5254", u.Url);
                Assert.Equal("http", u.Name);
            });

            logger.LogInformation("Stop resource.");
            await orchestrator.StopResourceAsync(runningResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Exited, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);

            // Stop the continer if this isn't the first run otherwise it'll stay running
            if (firstRun)
            {
                logger.LogInformation("Start resource again");
                await orchestrator.StartResourceAsync(runningResourceEvent.ResourceId, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
                await rns.WaitForResourceAsync(notStartedResourceName, e => e.Snapshot.State?.Text == KnownResourceStates.Running, token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            }

            await startTask.DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
            await app.StopAsync(token).DefaultTimeout(TestConstants.ExtraLongTimeoutTimeSpan);
        }
    }

    [Fact]
    public void RegisteredLifecycleHookIsExecutedWhenRunSynchronously()
    {
        var exceptionMessage = "Exception from lifecycle hook to prove it ran!";

        using var testProgram = CreateTestProgram("lifecycle-hook-executed-sync");
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.AddLifecycleHook((sp) =>
        {
            return new CallbackLifecycleHook((appModel, cancellationToken) =>
            {
                Assert.NotNull(appModel);

                throw new DistributedApplicationException(exceptionMessage);
            });
        });
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        var ex = Assert.Throws<AggregateException>(testProgram.Run);
        Assert.IsType<DistributedApplicationException>(ex.InnerExceptions.First());
        Assert.Equal(exceptionMessage, ex.InnerExceptions.First().Message);
    }

    [Fact]
    public void TryAddWillNotAddTheSameLifecycleHook()
    {
        using var testProgram = CreateTestProgram("lifecycle-hook-duplicates");

        var callback1 = (IServiceProvider sp) => new DummyLifecycleHook();
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.TryAddLifecycleHook(callback1);
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        var callback2 = (IServiceProvider sp) => new DummyLifecycleHook();
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.TryAddLifecycleHook(callback2);
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        var lifecycleHookDescriptors = testProgram.AppBuilder.Services.Where(sd => sd.ServiceType == typeof(IDistributedApplicationLifecycleHook));
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        Assert.Single(lifecycleHookDescriptors, sd => sd.ImplementationFactory == callback1);
        Assert.DoesNotContain(lifecycleHookDescriptors, sd => sd.ImplementationFactory == callback2);
    }

    [Fact]
    public async Task AllocatedPortsAssignedAfterHookRuns()
    {
        using var testProgram = CreateTestProgram("ports-assigned-after-hook-runs");
        var tcs = new TaskCompletionSource<DistributedApplicationModel>(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        testProgram.AppBuilder.Services.AddLifecycleHook(sp => new CheckAllocatedEndpointsLifecycleHook(tcs));
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var appModel = await tcs.Task.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        foreach (var item in appModel.Resources)
        {
            if (item is IResourceWithEndpoints resourceWithEndpoints)
            {
                Assert.True(resourceWithEndpoints.GetEndpoints().All(e => e.IsAllocated));
            }
        }
    }

#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
    private sealed class CheckAllocatedEndpointsLifecycleHook(TaskCompletionSource<DistributedApplicationModel> tcs) : IDistributedApplicationLifecycleHook
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
    {
        public Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
        {
            tcs.TrySetResult(appModel);

            return Task.CompletedTask;
        }
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/9340")]
    public async Task TestServicesWithMultipleReplicas()
    {
        var replicaCount = 3;

        using var testProgram = CreateTestProgram("multi-replica-svcs");

        testProgram.ServiceBBuilder.WithReplicas(replicaCount);

        await using var app = testProgram.Build();

        var logger = app.Services.GetRequiredService<ILogger<DistributedApplicationTests>>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        logger.LogInformation("Make sure services A and C are running");
        using var clientA = app.CreateHttpClient(testProgram.ServiceABuilder.Resource.Name, "http");
        using var clientC = app.CreateHttpClient(testProgram.ServiceCBuilder.Resource.Name, "http");

        await Task.WhenAll(clientA.GetStringAsync("/pid"), clientC.GetStringAsync("/pid")).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        // We should get 3 distinct PIDs from service B
        Dictionary<int, bool> pids = [];

        var uri = app.GetEndpoint(testProgram.ServiceBBuilder.Resource.Name, "http");

        var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        while (!cts.IsCancellationRequested)
        {
            using var clientB = new HttpClient();
            var url = $"{uri}pid";
            logger.LogInformation("Calling PID API at {Url}", url);
            var pidText = await clientB.GetStringAsync(url).DefaultTimeout();
            if (!string.IsNullOrEmpty(pidText))
            {
                var pid = int.Parse(pidText, CultureInfo.InvariantCulture);
                if (pids.TryAdd(pid, true))
                {
                    logger.LogInformation("PID API returned new value: {PID}", pid);

                    if (pids.Count == replicaCount)
                    {
                        logger.LogInformation("Success! We heard from all {ReplicaCount} replicas.", replicaCount);
                        break;
                    }
                }
            }

            await Task.Delay(100);
        }

        Assert.Equal(3, pids.Count);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyContainerArgs()
    {
        using var testProgram = CreateTestProgram("verify-container-args");

        AddRedisContainer(testProgram.AppBuilder, "verify-container-args-redis")
            .WithArgs("redis-cli", "-h", "host.docker.internal", "-p", "9999", "MONITOR")
            .WithContainerRuntimeArgs("--add-host", "testlocalhost:127.0.0.1");

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var list = await s.ListAsync<Container>().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.Collection(list,
            item =>
            {
                Assert.Equal(RedisImageSource, item.Spec.Image);
                Assert.Equal(["redis-cli", "-h", "host.docker.internal", "-p", "9999", "MONITOR"], item.Spec.Args);
                Assert.Equal(["--add-host", "testlocalhost:127.0.0.1"], item.Spec.RunArgs);
            });

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyContainerCreateFile()
    {
        using var testProgram = CreateTestProgram("verify-container-create-file", trustDeveloperCertificate: false);

        var destination = "/tmp";
        var umask = UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        var createFileEntries = new List<ContainerFileSystemItem>
        {
            new ContainerDirectory
            {
                Name = "test-folder",
                Owner = 1000,
                Entries = [
                    new ContainerFile
                    {
                        Name = "test.txt",
                        Contents = "Hello World!",
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    },
                    new ContainerFile
                    {
                        Name = "test2.sh",
                        SourcePath = "/tmp/test2.sh",
                        Mode = UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead,
                    },
                    new ContainerOpenSSLCertificateFile
                    {
                        Name = "testcert.pem",
                        Contents = "value",
                    },
                ],
            },
        };

        AddRedisContainer(testProgram.AppBuilder, "verify-container-create-file-redis")
            .WithContainerFiles(destination, createFileEntries, umask: umask);

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var list = await s.ListAsync<Container>().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.Collection(list,
            item =>
            {
                Assert.Equal(RedisImageSource, item.Spec.Image);
                Assert.Equal(new List<ContainerCreateFileSystem>
                {
                    new ContainerCreateFileSystem
                    {
                        Destination = destination,
                        Umask = (int?)umask,
                        Entries = createFileEntries.Select(e => e.ToContainerFileSystemEntry()).ToList(),
                    }
                },
                item.Spec.CreateFiles);
            });

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyRedisWithCertificateKeyPair()
    {
        const string testName = "verify-redis-with-certificate";
        using var testProgram = CreateTestProgram(testName, trustDeveloperCertificate: false);

        // Create a test certificate
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var redis = testProgram.AppBuilder.AddRedis($"{testName}-redis")
            .WithHttpsCertificate(cert);

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var list = await s.ListAsync<Container>().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var redisContainer = Assert.Single(list, c => c.Metadata.Name.StartsWith($"{testName}-redis"));

        Assert.NotNull(redisContainer.Spec.Args);
        Assert.Equal(2, redisContainer.Spec.Args.Count);
        // Verify that arguments are passed as shell command
        Assert.Equal("-c", redisContainer.Spec.Args[0]);
        Assert.Contains("--tls-cert-file", redisContainer.Spec.Args[1]);
        Assert.Contains("--tls-key-file", redisContainer.Spec.Args[1]);
        Assert.Contains("--tls-auth-clients", redisContainer.Spec.Args[1]);
        Assert.Contains("--tls-port", redisContainer.Spec.Args[1]);
        Assert.Contains("--port", redisContainer.Spec.Args[1]);

        // Verify both endpoints exist (TLS on 6380, non-TLS on 6379)
        Assert.NotNull(redisContainer.Spec.Ports);
        Assert.Contains(redisContainer.Spec.Ports, p => p.ContainerPort == 6380);
        Assert.Contains(redisContainer.Spec.Ports, p => p.ContainerPort == 6379);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Theory]
    [RequiresDocker]
    [RequiresDevCert]
    [InlineData(false, false, false, false, CertificateTrustScope.Append)]
    [InlineData(false, true, true, false, CertificateTrustScope.Append)]
    [InlineData(true, false, false, false, CertificateTrustScope.Append)]
    [InlineData(false, false, false, false, CertificateTrustScope.Override)]
    [InlineData(false, true, true, false, CertificateTrustScope.Override)]
    [InlineData(true, false, false, false, CertificateTrustScope.Override)]
    [InlineData(false, false, false, true, CertificateTrustScope.Override)]
    [InlineData(false, true, true, true, CertificateTrustScope.Override)]
    [InlineData(true, false, false, true, CertificateTrustScope.Override)]
    [InlineData(false, false, false, false, CertificateTrustScope.None)]
    [InlineData(false, true, false, false, CertificateTrustScope.None)]
    [InlineData(true, false, false, false, CertificateTrustScope.None)]
    public async Task VerifyContainerIncludesExpectedDevCertificateConfiguration(bool? implicitTrust, bool? explicitTrust, bool expectDevCert, bool overridePaths, CertificateTrustScope trustScope)
    {
        using var testProgram = CreateTestProgram("verify-container-dev-cert", trustDeveloperCertificate: implicitTrust);

        var container = AddRedisContainer(testProgram.AppBuilder, "verify-container-dev-cert-redis")
            .WithoutHttpsCertificate();
        if (explicitTrust.HasValue)
        {
            container.WithDeveloperCertificateTrust(explicitTrust.Value);
        }

        var expectedDestination = "/usr/lib/ssl/aspire";
        var expectedDefaultCertificateDirs = new List<string>();
        var expectedDefaultBundleFiles = new List<string>();
        if (overridePaths)
        {
            expectedDestination = "/usr/lib/ssl/someotherpath";
            expectedDefaultCertificateDirs.Add("/usr/lib/someothercertpath");
            expectedDefaultCertificateDirs.Add("/usr/share/lib/anotherpath");
            expectedDefaultBundleFiles.Add("/usr/lib/somessl/cert.pem");
            expectedDefaultBundleFiles.Add("/usr/share/certfile.pem");

            container.WithContainerCertificatePaths(customCertificatesDestination: expectedDestination, defaultCertificateBundlePaths: expectedDefaultBundleFiles, defaultCertificateDirectoryPaths: expectedDefaultCertificateDirs);
        }
        else
        {
            expectedDefaultCertificateDirs.AddRange(ContainerCertificatePathsAnnotation.DefaultCertificateDirectoriesPaths);
            expectedDefaultBundleFiles.AddRange(ContainerCertificatePathsAnnotation.DefaultCertificateBundlePaths);
        }

        container.WithCertificateTrustScope(trustScope);

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var dc = app.Services.GetRequiredService<IDeveloperCertificateService>();
        var list = await s.ListAsync<Container>().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.Collection(list,
            item =>
            {
                Assert.Equal(RedisImageSource, item.Spec.Image);

                if (expectDevCert)
                {
                    Assert.NotNull(item.Spec.Env);
                    if (trustScope == CertificateTrustScope.Append)
                    {
                        Assert.DoesNotContain(item.Spec.Env, e => e.Name == "SSL_CERT_FILE");
                    }
                    else if (trustScope == CertificateTrustScope.Override)
                    {
                        Assert.Collection(item.Spec.Env.Where(e => e.Name == "SSL_CERT_FILE"),
                            certFile =>
                            {
                                Assert.Equal("SSL_CERT_FILE", certFile.Name);
                                Assert.Equal($"{expectedDestination}/cert.pem", certFile.Value);
                            });
                    }

                    Assert.Collection(item.Spec.Env.Where(e => e.Name == "SSL_CERT_DIR"),
                        certDir =>
                        {
                            Assert.Equal("SSL_CERT_DIR", certDir.Name);
                            Assert.NotNull(certDir.Value);
                            var certDirPaths = certDir.Value.Split(':');
                            Assert.Contains($"{expectedDestination}/certs", certDirPaths);
                            if (trustScope == CertificateTrustScope.Append)
                            {
                                foreach (var expectedPath in expectedDefaultCertificateDirs)
                                {
                                    Assert.Contains(expectedPath, certDirPaths);
                                }
                            }
                        });

                    if (trustScope == CertificateTrustScope.None)
                    {
                        Assert.Empty(item.Spec?.PemCertificates?.Certificates ?? []);
                        return;
                    }

                    foreach (var cert in dc.Certificates)
                    {
                        var foundCert = Assert.Single(item.Spec?.PemCertificates?.Certificates ?? [], c => string.Equals(c.Thumbprint, cert.Thumbprint, StringComparison.Ordinal));
                        if (cert.IsAspNetCoreDevelopmentCertificate())
                        {
                            Assert.True(X509Certificate2.CreateFromPem(foundCert.Contents).IsAspNetCoreDevelopmentCertificate());
                        }
                    }

                    if (trustScope == CertificateTrustScope.Override)
                    {
                        Assert.Equal(expectedDefaultBundleFiles.Count, item.Spec?.PemCertificates?.OverwriteBundlePaths?.Count ?? 0);
                        foreach (var bundlePath in expectedDefaultBundleFiles)
                        {
                            Assert.Contains(bundlePath, item.Spec?.PemCertificates?.OverwriteBundlePaths ?? []);
                        }
                    }
                }
                else
                {
                    Assert.Empty(item.Spec?.PemCertificates?.Certificates ?? []);
                }
            });

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyContainerSucceedsWithCreateFileContinueOnError()
    {
        using var testProgram = CreateTestProgram("verify-container-continue-on-error", trustDeveloperCertificate: false);

        var container = AddRedisContainer(testProgram.AppBuilder, "verify-container-continue-on-error-redis")
            .WithContainerFiles("/tmp", [
                new ContainerOpenSSLCertificateFile
                {
                    Name = "invalid-cert.pem",
                    Contents = "not a real cert",
                    // This would normally cause the container to fail, but with ContinueOnError it should be ignored.
                    ContinueOnError = true,
                },
            ]);

        await using var app = testProgram.Build();

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        var s = app.Services.GetRequiredService<IKubernetesService>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var dependentRunningResourceEvent = await rns.WaitForResourceAsync(container.Resource.Name, e => e.Snapshot.State?.Text == KnownResourceStates.Running).DefaultTimeout(TestConstants.LongTimeoutTimeSpan);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    [RequiresDevCert]
    public async Task VerifyEnvironmentVariablesAvailableInCertificateTrustConfigCallback()
    {
        using var testProgram = CreateTestProgram("verify-env-vars-in-cert-callback", trustDeveloperCertificate: true);

        var value = "SomeValue";
        var container = AddRedisContainer(testProgram.AppBuilder, "verify-env-vars-in-cert-callback-redis")
            .WithEnvironment("INITIAL_ENV_VAR", "InitialValue")
            .WithEnvironment("INITIAL_REFERENCE_EXPRESSION", ReferenceExpression.Create($"{value}"))
            .WithCertificateTrustConfiguration(ctx =>
            {
                if (ctx.EnvironmentVariables.ContainsKey("INITIAL_ENV_VAR"))
                {
                    // Add an additional environment variable in the callback
                    ctx.EnvironmentVariables["CALLBACK_ADDED_VAR"] = "CallbackValue";
                    var initialRE = Assert.IsType<ReferenceExpression>(ctx.EnvironmentVariables["INITIAL_REFERENCE_EXPRESSION"]);
                    ctx.EnvironmentVariables["INITIAL_REFERENCE_EXPRESSION"] = ReferenceExpression.Create($"{initialRE}_AppendedInCallback");
                }

                return Task.CompletedTask;
            });

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(
            s,
            $"verify-env-vars-in-cert-callback-redis-{ReplicaIdRegex}-{suffix}",
            r => r.Spec.Env != null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer);
        Assert.NotNull(redisContainer.Spec.Env);

        // Verify both environment variables are present in the final container spec
        Assert.Single(redisContainer.Spec.Env, e => e.Name == "INITIAL_ENV_VAR" && e.Value == "InitialValue");
        Assert.Single(redisContainer.Spec.Env, e => e.Name == "CALLBACK_ADDED_VAR" && e.Value == "CallbackValue");
        Assert.Single(redisContainer.Spec.Env, e => e.Name == "INITIAL_REFERENCE_EXPRESSION" && e.Value == $"{value}_AppendedInCallback");

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyEnvironmentVariablesAppliedWithoutCertificateTrustConfig()
    {
        // Don't apply developer certificate trust so the config callback shouldn't be invoked
        using var testProgram = CreateTestProgram("verify-env-vars-in-cert-callback", trustDeveloperCertificate: false);

        var value = "SomeValue";
        var container = AddRedisContainer(testProgram.AppBuilder, "verify-env-vars-in-cert-callback-redis")
            .WithEnvironment("INITIAL_ENV_VAR", "InitialValue")
            .WithEnvironment("INITIAL_REFERENCE_EXPRESSION", ReferenceExpression.Create($"{value}"))
            .WithCertificateTrustConfiguration(ctx =>
            {
                Assert.Fail("Certificate trust configuration callback should not be invoked when developer certificate trust is not applied.");

                return Task.CompletedTask;
            });

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(
            s,
            $"verify-env-vars-in-cert-callback-redis-{ReplicaIdRegex}-{suffix}",
            r => r.Spec.Env != null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer);
        Assert.NotNull(redisContainer.Spec.Env);

        // Verify both environment variables are present in the final container spec
        Assert.Single(redisContainer.Spec.Env, e => e.Name == "INITIAL_ENV_VAR" && e.Value == "InitialValue");
        Assert.Single(redisContainer.Spec.Env, e => e.Name == "INITIAL_REFERENCE_EXPRESSION" && e.Value == $"{value}");

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyContainerStopStartWorks()
    {
        using var testProgram = CreateTestProgram("container-start-stop", randomizePorts: false);

        const string containerName = "container-start-stop-redis";
        AddRedisContainer(testProgram.AppBuilder, containerName)
            .WithEndpoint(targetPort: 6379, name: "tcp", env: "REDIS_PORT");

        await using var app = testProgram.Build();

        var events = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        var beforeResourceStartedEvents = Channel.CreateUnbounded<BeforeResourceStartedEvent>();
        events.Subscribe<BeforeResourceStartedEvent>(async (e, ct) =>
        {
            await beforeResourceStartedEvents.Writer.WriteAsync(e, ct);
        });

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        var token = cts.Token;

        var containerPattern = $"{containerName}-{ReplicaIdRegex}-{suffix}";
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(kubernetes, containerPattern, r => r.Status?.State == ContainerState.Running, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(redisContainer);

        // Initial startup event.
        await beforeResourceStartedEvents.Reader.ReadAsync().DefaultTimeout();

        await orchestrator.StopResourceAsync(redisContainer.Metadata.Name, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(kubernetes, containerPattern, r => r.Status?.State == ContainerState.Exited, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(redisContainer);

        await orchestrator.StartResourceAsync(redisContainer.Metadata.Name, token);

        // Restart event.
        await beforeResourceStartedEvents.Reader.ReadAsync().DefaultTimeout();

        redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(kubernetes, containerPattern, r => r.Status?.State == ContainerState.Running, token);
        Assert.NotNull(redisContainer);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/4651")]
    public async Task VerifyExecutableStopStartWorks()
    {
        const string testName = "executable-start-stop";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        await using var app = testProgram.Build();

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();
        var orchestrator = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        var token = cts.Token;

        await app.StartAsync(token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var executablePattern = $"{testName}-servicea-{ReplicaIdRegex}-{suffix}";
        var serviceA = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, executablePattern, r => r.Status?.State == ExecutableState.Running, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(serviceA);

        await orchestrator.StopResourceAsync(serviceA.Metadata.Name, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        serviceA = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, executablePattern, r => r.Status?.State == ExecutableState.Finished, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(serviceA);

        await orchestrator.StartResourceAsync(serviceA.Metadata.Name, token).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        serviceA = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, executablePattern, r => r.Status?.State == ExecutableState.Running, token).DefaultTimeout(TestConstants.LongTimeoutDuration);
        Assert.NotNull(serviceA);

        await app.StopAsync(token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/8871")]
    public async Task SpecifyingEnvPortInEndpointFlowsToEnv()
    {
        const string testName = "ports-flow-to-env";
        using var testProgram = CreateTestProgram(testName, randomizePorts: false);

        testProgram.ServiceABuilder
            .WithHttpEndpoint(name: "http0", env: "PORT0");

        AddRedisContainer(testProgram.AppBuilder, $"{testName}-redis")
            .WithEndpoint(targetPort: 6379, name: "tcp", env: "REDIS_PORT");

        testProgram.AppBuilder.AddNodeApp($"{testName}-nodeapp", "fakePath", "fake.js")
            .WithHttpEndpoint(port: 5031, env: "PORT");

        await using var app = testProgram.Build();

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(kubernetes, $"{testName}-redis-{ReplicaIdRegex}-{suffix}", r => r.Status?.EffectiveEnv is not null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(redisContainer);

        var serviceA = await KubernetesHelper.GetResourceByNameAsync<Executable>(kubernetes, $"{testName}-servicea", suffix!, r => r.Status?.EffectiveEnv is not null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(serviceA);

        var nodeApp = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, $"{testName}-nodeapp-{ReplicaIdRegex}-{suffix}", r => r.Status?.EffectiveEnv is not null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(nodeApp);

        Assert.Equal(RedisImageSource, redisContainer.Spec.Image);
        Assert.Equal("6379", GetEnv(redisContainer.Spec.Env, "REDIS_PORT"));
        Assert.Equal("6379", GetEnv(redisContainer.Status!.EffectiveEnv, "REDIS_PORT"));

        Assert.Equal($"{{{{- portForServing \"{testName}-servicea-http0-{suffix}\" -}}}}", GetEnv(serviceA.Spec.Env, "PORT0"));
        var serviceAPortValue = GetEnv(serviceA.Status!.EffectiveEnv, "PORT0");
        Assert.False(string.IsNullOrEmpty(serviceAPortValue));
        Assert.NotEqual(0, int.Parse(serviceAPortValue, CultureInfo.InvariantCulture));

        Assert.Equal($"{{{{- portForServing \"{testName}-nodeapp-{suffix}\" -}}}}", GetEnv(nodeApp.Spec.Env, "PORT"));
        var nodeAppPortValue = GetEnv(nodeApp.Status!.EffectiveEnv, "PORT");
        Assert.False(string.IsNullOrEmpty(nodeAppPortValue));
        Assert.NotEqual(0, int.Parse(nodeAppPortValue, CultureInfo.InvariantCulture));

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        static string? GetEnv(IEnumerable<EnvVar>? envVars, string name)
        {
            Assert.NotNull(envVars);
            return Assert.Single(envVars, e => e.Name == name).Value;
        }
    }

    [Fact]
    public async Task StartAsync_DashboardUrls_DisplayPropertiesSet()
    {
        const string testName = "dashboard-urls-display";
        var args = new string[] {
            $"{KnownConfigNames.AspNetCoreUrls}=https://localhost:0;http://localhost:0",
            $"{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}=http://localhost:0"
        };
        using var testProgram = CreateTestProgram(testName, args: args, disableDashboard: false);

        await using var app = testProgram.Build();

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var aspireDashboard = model.Resources.Single(r => r.Name == KnownResourceNames.AspireDashboard);

        var dashboardUrls = aspireDashboard.Annotations.OfType<ResourceUrlAnnotation>().ToList();
        Assert.Collection(dashboardUrls,
            u => Assert.Equal("Dashboard (https)", u.DisplayText),
            u => Assert.Equal("Dashboard (http)", u.DisplayText),
            u => Assert.Equal("otlp-grpc", u.DisplayText));

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Theory]
    [InlineData(KnownConfigNames.DashboardFrontendBrowserToken)]
    [InlineData(KnownConfigNames.Legacy.DashboardFrontendBrowserToken)]
    public async Task StartAsync_DashboardAuthConfig_PassedToDashboardProcess(string tokenEnvVarName)
    {
        const string testName = "dashboard-auth-config";
        var browserToken = "ThisIsATestToken";
        var args = new string[] {
            $"{KnownConfigNames.AspNetCoreUrls}=http://localhost:0",
            $"{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}=http://localhost:0",
            $"{tokenEnvVarName}={browserToken}"
        };
        using var testProgram = CreateTestProgram(testName, args: args, disableDashboard: false);

        await using var app = testProgram.Build();

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var aspireDashboard = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, $"aspire-dashboard-{ReplicaIdRegex}-{suffix}", r => r.Status?.EffectiveEnv is not null).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.NotNull(aspireDashboard);

        Assert.Equal("BrowserToken", GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__FRONTEND__AUTHMODE"));
        Assert.Equal("ThisIsATestToken", GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__FRONTEND__BROWSERTOKEN"));

        Assert.Equal("ApiKey", GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__OTLP__AUTHMODE"));
        var keyBytes = Convert.FromHexString(GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__OTLP__PRIMARYAPIKEY")!);
        Assert.Equal(16, keyBytes.Length);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        static string? GetEnv(IEnumerable<EnvVar>? envVars, string name)
        {
            Assert.NotNull(envVars);
            return Assert.Single(envVars, e => e.Name == name).Value;
        }
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/4651")]
    public async Task StartAsync_UnsecuredAllowAnonymous_PassedToDashboardProcess()
    {
        const string testName = "dashboard-allow-anonymous";
        var args = new string[] {
            $"{KnownConfigNames.AspNetCoreUrls}=http://localhost:0",
            $"{KnownConfigNames.DashboardOtlpGrpcEndpointUrl}=http://localhost:0",
            $"{KnownConfigNames.DashboardUnsecuredAllowAnonymous}=true"
        };
        using var testProgram = CreateTestProgram(testName, args: args, disableDashboard: false);

        await using var app = testProgram.Build();

        var kubernetes = app.Services.GetRequiredService<IKubernetesService>();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var aspireDashboard = await KubernetesHelper.GetResourceByNameMatchAsync<Executable>(kubernetes, $"aspire-dashboard-{ReplicaIdRegex}-{suffix}", r => r.Status?.EffectiveEnv is not null).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
        Assert.NotNull(aspireDashboard);

        Assert.Equal("Unsecured", GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__FRONTEND__AUTHMODE"));
        Assert.Equal("Unsecured", GetEnv(aspireDashboard.Spec.Env, "DASHBOARD__OTLP__AUTHMODE"));

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        static string? GetEnv(IEnumerable<EnvVar>? envVars, string name)
        {
            Assert.NotNull(envVars);
            return Assert.Single(envVars, e => e.Name == name).Value;
        }
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyDockerWithEntrypointWorks()
    {
        const string testName = "docker-entrypoint";
        using var testProgram = CreateTestProgram(testName);

        AddRedisContainer(testProgram.AppBuilder, $"{testName}-redis")
            .WithEntrypoint("bob");

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(s, $"{testName}-redis-{ReplicaIdRegex}-{suffix}",
            r => r.Status?.State == ContainerState.FailedToStart && (r.Status?.Message.Contains("bob") ?? false)).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer);
        Assert.Equal(RedisImageSource, redisContainer.Spec.Image);
        Assert.Equal("bob", redisContainer.Spec.Command);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyDockerWithBindMountWorksWithAbsolutePaths()
    {
        const string testName = "docker-bindmount-absolute";
        using var testProgram = CreateTestProgram(testName);

        var sourcePath = Path.GetFullPath("/etc/path-here");
        AddRedisContainer(testProgram.AppBuilder, $"{testName}-redis")
            .WithBindMount(sourcePath, "path-here");

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(
                s,
                $"{testName}-redis-{ReplicaIdRegex}-{suffix}", r => r.Spec.VolumeMounts != null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer.Spec.VolumeMounts);
        Assert.NotEmpty(redisContainer.Spec.VolumeMounts);
        Assert.Equal(sourcePath, redisContainer.Spec.VolumeMounts[0].Source);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyDockerWithBindMountWorksWithRelativePaths()
    {
        const string testName = "docker-bindmount-relative";
        using var testProgram = CreateTestProgram(testName);

        AddRedisContainer(testProgram.AppBuilder, $"{testName}-redis")
            .WithBindMount("etc/path-here", "path-here");

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(
            s,
            $"{testName}-redis-{ReplicaIdRegex}-{suffix}", r => r.Spec.VolumeMounts != null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer.Spec.VolumeMounts);
        Assert.NotEmpty(redisContainer.Spec.VolumeMounts);
        Assert.NotEqual("etc/path-here", redisContainer.Spec.VolumeMounts[0].Source);
        Assert.True(Path.IsPathRooted(redisContainer.Spec.VolumeMounts[0].Source));

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task VerifyDockerWithVolumeWorksWithName()
    {
        const string testName = "docker-volume";
        using var testProgram = CreateTestProgram(testName);

        AddRedisContainer(testProgram.AppBuilder, $"{testName}-redis")
            .WithVolume($"{testName}-volume", "/path-here");

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        var redisContainer = await KubernetesHelper.GetResourceByNameMatchAsync<Container>(
                s,
                $"{testName}-redis-{ReplicaIdRegex}-{suffix}", r => r.Spec.VolumeMounts != null).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        Assert.NotNull(redisContainer.Spec.VolumeMounts);
        Assert.NotEmpty(redisContainer.Spec.VolumeMounts);
        Assert.Equal($"{testName}-volume", redisContainer.Spec.VolumeMounts[0].Source);

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task KubernetesHasResourceNameForContainersAndExes()
    {
        const string testName = "kube-resource-names";
        using var testProgram = CreateTestProgram(testName, includeIntegrationServices: true);

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var expectedExeResources = new HashSet<string>()
        {
            $"{testName}-servicea",
            $"{testName}-serviceb",
            $"{testName}-servicec",
            $"{testName}-workera",
            $"{testName}-integrationservicea"
        };

        var expectedContainerResources = new HashSet<string>()
        {
            $"{testName}-redis",
            $"{testName}-postgres"
        };

        await foreach (var resource in s.WatchAsync<Container>().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout))
        {
            Assert.True(resource.Item2.Metadata.Annotations.TryGetValue(Container.ResourceNameAnnotation, out var value));
            if (expectedContainerResources.Contains(value))
            {
                expectedContainerResources.Remove(value);
            }

            if (expectedContainerResources.Count == 0)
            {
                break;
            }
        }

        await foreach (var resource in s.WatchAsync<Executable>().DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout))
        {
            Assert.True(resource.Item2.Metadata.Annotations.TryGetValue(Executable.ResourceNameAnnotation, out var value));
            if (expectedExeResources.Contains(value))
            {
                expectedExeResources.Remove(value);
            }

            if (expectedExeResources.Count == 0)
            {
                break;
            }
        }
    }

    [Fact]
    public async Task ReplicasAndProxylessEndpointThrows()
    {
        const string testName = "replicas-no-proxyless-endpoints";
        using var testProgram = CreateTestProgram(testName);
        testProgram.ServiceABuilder.WithReplicas(2).WithEndpoint("http", endpoint =>
        {
            endpoint.IsProxied = false;
        });

        await using var app = testProgram.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout));
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        Assert.Equal($"Resource '{testName}-servicea-{suffix}' uses multiple replicas and a proxy-less endpoint 'http'. These features do not work together.", ex.Message);
    }

    [Fact]
    public async Task ProxylessEndpointWithoutPortThrows()
    {
        const string testName = "proxyess-endpoint-without-port";
        using var testProgram = CreateTestProgram(testName);
        testProgram.ServiceABuilder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = null;
            endpoint.IsProxied = false;
        });

        await using var app = testProgram.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout));
        var suffix = app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.ResourceNameSuffix;
        Assert.Equal($"Service '{testName}-servicea-{suffix}' needs to specify a port for endpoint 'http' since it isn't using a proxy.", ex.Message);
    }

    [Fact]
    [QuarantinedTest("https://github.com/dotnet/aspire/issues/8728")]
    public async Task ProxylessEndpointWorks()
    {
        const string testName = "proxyless-endpoint-works";
        using var testProgram = CreateTestProgram(testName);

        testProgram.ServiceABuilder
            .WithEndpoint("http", e =>
            {
                e.Port = 1234;
                e.TargetPort = 1234;
                e.IsProxied = false;
            });

        await using var app = testProgram.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var client = app.CreateHttpClientWithResilience($"{testName}-servicea", "http");

        var result = await client.GetStringAsync("pid").DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        Assert.NotNull(result);

        // Check that endpoint from launchsettings doesn't work
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            using var client2 = new HttpClient(new SocketsHttpHandler
            {
                // Provide a timeout to avoid long timeout while trying to connect.
                ConnectTimeout = TimeSpan.FromSeconds(2)
            });
            await client2.GetStringAsync("http://localhost:5156/pid");
        }).DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    [Fact]
    [OuterloopTest("Long-running endpoint proxy test")]
    [RequiresSSLCertificate]
    public async Task ProxylessAndProxiedEndpointBothWorkOnSameResource()
    {
        const string testName = "proxyless-and-proxied-endpoints";
        using var testProgram = CreateTestProgram(testName);

        testProgram.ServiceABuilder
            .WithEndpoint("http", e =>
            {
                e.Port = 1234;
                e.TargetPort = 1234;
                e.IsProxied = false;
            }, createIfNotExists: false)
            .WithEndpoint("https", e =>
            {
                e.UriScheme = "https";
                e.Port = 1543;
            }, createIfNotExists: true);

        await using var app = testProgram.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        var token = cts.Token;

        // Wait for servicea to be ready
        await app.WaitForTextAsync("Content root path:", resourceName: testProgram.ServiceABuilder.Resource.Name).DefaultTimeout(TestConstants.LongTimeoutDuration);

        var urls = string.Empty;
        var httpEndPoint = app.GetEndpoint(testProgram.ServiceABuilder.Resource.Name, endpointName: "http");
        while (true)
        {
            try
            {
                using var client = new HttpClient();
                urls = await client.GetStringAsync($"{httpEndPoint}urls", token);
                break;
            }
            catch
            {
                await Task.Delay(100, token);
            }
        }

        Assert.Contains(httpEndPoint.ToString().Trim('/'), urls);

        // https endpoint is proxied so app won't have this specific endpoint
        var httpsEndpoint = app.GetEndpoint(testProgram.ServiceABuilder.Resource.Name, endpointName: "https");
        Assert.DoesNotContain(httpsEndpoint.ToString().Trim('/'), urls);

        while (true)
        {
            try
            {
                using var client = new HttpClient();
                var value = await client.GetStringAsync($"{httpsEndpoint}urls", token).DefaultTimeout();
                Assert.Equal(urls, value);
                break;
            }
            catch (Exception ex) when (ex is not EqualException)
            {
                _testOutputHelper.WriteLine($"Exception {ex} while trying to get https url");
                await Task.Delay(100, token);
            }
        }
    }

    [Fact]
    [OuterloopTest("Long-running container test")]
    [RequiresDocker]
    public async Task ProxylessContainerCanBeReferenced()
    {
        const string testName = "proxyless-container";
        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        var redis = builder.AddRedis($"{testName}-redis", 1234).WithEndpoint("tcp", endpoint =>
        {
            endpoint.IsProxied = false;
        });

        // Since port is not specified, this instance will use the container target port (6379) as the host port.
        var redisNoPort = builder.AddRedis($"{testName}-redisNoPort").WithEndpoint("tcp", endpoint =>
        {
            endpoint.IsProxied = false;
        });
        var servicea = builder.AddProject<Projects.ServiceA>($"{testName}-servicea")
            .WithReference(redis)
            .WithReference(redisNoPort);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        // Wait for the application to be ready
        await app.WaitForTextAsync("Application started.").DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        // Wait until the service itself starts.
        using var clientA = app.CreateHttpClient(servicea.Resource.Name, "http");
        await clientA.GetStringAsync("/").DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var exeList = await s.ListAsync<Executable>().DefaultTimeout();

        var service = Assert.Single(exeList, c => $"{testName}-servicea".Equals(c.AppModelResourceName));
        var env = Assert.Single(service.Spec.Env!, e => e.Name == $"ConnectionStrings__{testName}-redis");
        var sslVal = redis.Resource.TlsEnabled ? ",ssl=true" : string.Empty;
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:1234,password={redis.Resource.PasswordParameter?.Value}{sslVal}", env.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        var list = await s.ListAsync<Container>().DefaultTimeout();
        var redisContainer = Assert.Single(list, c => Regex.IsMatch(c.Name(), $"{testName}-redis-{ReplicaIdRegex}"));
        if (redis.Resource.TlsEnabled)
        {
            Assert.Equal(2, redisContainer.Spec.Ports!.Count);
            Assert.Contains(redisContainer.Spec.Ports!, p => p.HostPort == 1234);
        }
        else
        {
            Assert.Equal(1234, Assert.Single(redisContainer.Spec.Ports!).HostPort);
        }

        var otherRedisEnv = Assert.Single(service.Spec.Env!, e => e.Name == $"ConnectionStrings__{testName}-redisNoPort");
        sslVal = redisNoPort.Resource.TlsEnabled ? ",ssl=true" : string.Empty;
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:6379,password={redisNoPort.Resource.PasswordParameter?.Value}{sslVal}", otherRedisEnv.Value);
#pragma warning restore CS0618 // Type or member is obsolete
        var otherRedisContainer = Assert.Single(list, c => Regex.IsMatch(c.Name(), $"{testName}-redisNoPort-{ReplicaIdRegex}"));
        if (redisNoPort.Resource.TlsEnabled)
        {
            Assert.Equal(2, otherRedisContainer.Spec.Ports!.Count);
            Assert.Contains(otherRedisContainer.Spec.Ports!, p => p.HostPort == 6379);
        }
        else
        {
            Assert.Equal(6379, Assert.Single(otherRedisContainer.Spec.Ports!).HostPort);
        }

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [OuterloopTest("Long-running endpoint proxy test")]
    [RequiresDocker]
    public async Task WithEndpointProxySupportDisablesProxies()
    {
        const string testName = "endpoint-proxy-support";
        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        var redis = builder.AddRedis($"{testName}-redis", 1234).WithEndpointProxySupport(false);

        // Since port is not specified, this instance will use the container target port (6379) as the host port.
        var redisNoPort = builder.AddRedis($"{testName}-redisNoPort").WithEndpointProxySupport(false);

        var servicea = builder.AddProject<Projects.ServiceA>($"{testName}-servicea")
            .WithReference(redis)
            .WithReference(redisNoPort);

        using var app = builder.Build();
        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        // Wait for the application to be ready
        await app.WaitForTextAsync("Application started.").DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        // Wait until the service itself starts.
        using var clientA = app.CreateHttpClient(servicea.Resource.Name, "http");
        await clientA.GetStringAsync("/").DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();

        var serviceList = await s.ListAsync<Service>().DefaultTimeout();
        Assert.All(serviceList.Where(s => s.Metadata.Name.Contains("redis")), s => Assert.Equal(AddressAllocationModes.Proxyless, s.Spec.AddressAllocationMode));

        var exeList = await s.ListAsync<Executable>().DefaultTimeout();

        var service = Assert.Single(exeList, c => $"{testName}-servicea".Equals(c.AppModelResourceName));
        var env = Assert.Single(service.Spec.Env!, e => e.Name == $"ConnectionStrings__{testName}-redis");
        var sslVal = redis.Resource.TlsEnabled ? ",ssl=true" : string.Empty;
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:1234,password={redis.Resource.PasswordParameter!.Value}{sslVal}", env.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        var list = await s.ListAsync<Container>().DefaultTimeout();
        var redisContainer = Assert.Single(list, c => Regex.IsMatch(c.Name(), $"{testName}-redis-{ReplicaIdRegex}"));
        if (redis.Resource.TlsEnabled)
        {
            Assert.Equal(2, redisContainer.Spec.Ports!.Count);
            Assert.Contains(redisContainer.Spec.Ports!, p => p.HostPort == 1234);
        }
        else
        {
            Assert.Equal(1234, Assert.Single(redisContainer.Spec.Ports!).HostPort);
        }

        var otherRedisEnv = Assert.Single(service.Spec.Env!, e => e.Name == $"ConnectionStrings__{testName}-redisNoPort");
        sslVal = redisNoPort.Resource.TlsEnabled ? ",ssl=true" : string.Empty;
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:6379,password={redisNoPort.Resource.PasswordParameter!.Value}{sslVal}", otherRedisEnv.Value);
#pragma warning restore CS0618 // Type or member is obsolete

        var otherRedisContainer = Assert.Single(list, c => Regex.IsMatch(c.Name(), $"{testName}-redisNoPort-{ReplicaIdRegex}"));
        if (redisNoPort.Resource.TlsEnabled)
        {
            Assert.Equal(2, otherRedisContainer.Spec.Ports!.Count);
            Assert.Contains(otherRedisContainer.Spec.Ports!, p => p.HostPort == 6379);
        }
        else
        {
            Assert.Equal(6379, Assert.Single(otherRedisContainer.Spec.Ports!).HostPort);
        }

        await app.StopAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
    }

    [Fact]
    [RequiresDocker]
    public async Task ProxylessContainerWithoutPortThrows()
    {
        const string testName = "proxyless-container-without-ports";
        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        var redis = AddRedisContainer(builder, $"{testName}-redis").WithEndpoint("tcp", endpoint =>
        {
            endpoint.IsProxied = false;
        });

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout));
        Assert.Equal($"The endpoint 'tcp' for container resource '{testName}-redis' must specify the TargetPort value", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresDocker]
    public async Task PersistentNetworkCreatedIfPersistentContainers(bool createPersistentContainer)
    {
        const string testName = "persistent-network-if-persistent-containers";
        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        if (createPersistentContainer)
        {
            builder.AddContainer($"{testName}-persistent", RedisContainerImageTags.Image, RedisContainerImageTags.Tag)
                .WithLifetime(ContainerLifetime.Persistent);
        }

        builder.AddContainer($"{testName}-nonpersistent", RedisContainerImageTags.Image, RedisContainerImageTags.Tag);

        using var app = builder.Build();

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        var s = app.Services.GetRequiredService<IKubernetesService>();
        var networks = await s.ListAsync<ContainerNetwork>().DefaultTimeout();

        if (createPersistentContainer)
        {
            Assert.Single(networks, n => n.Spec.Persistent == true);
        }
        else
        {
            Assert.Single(networks, n => n.Spec.Persistent.GetValueOrDefault(false) == false);
        }
    }

    [Fact]
    [RequiresDocker]
    public async Task AfterResourcesCreatedLifecycleHookWorks()
    {
        const string testName = "lifecycle-hook-after-resource-created";
        using var builder = TestDistributedApplicationBuilder.Create(_testOutputHelper);

        builder.AddRedis($"{testName}-redis");
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        builder.Services.TryAddLifecycleHook<KubernetesTestLifecycleHook>();
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.

        using var app = builder.Build();

        var s = app.Services.GetRequiredService<IKubernetesService>();
#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        var lifecycles = app.Services.GetServices<IDistributedApplicationLifecycleHook>();
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
        var kubernetesLifecycle = (KubernetesTestLifecycleHook)lifecycles.Where(l => l.GetType() == typeof(KubernetesTestLifecycleHook)).First();
        kubernetesLifecycle.KubernetesService = s;

        await app.StartAsync().DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

        await kubernetesLifecycle.HooksCompleted.DefaultTimeout(TestConstants.DefaultOrchestratorTestTimeout);
    }

    private static IResourceBuilder<ContainerResource> AddRedisContainer(IDistributedApplicationBuilder builder, string containerName)
    {
        return builder.AddContainer(containerName, RedisContainerImageTags.Image, RedisContainerImageTags.Tag)
            .WithImageRegistry(AspireTestContainerRegistry);
    }

#pragma warning disable CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
    private sealed class KubernetesTestLifecycleHook : IDistributedApplicationLifecycleHook
#pragma warning restore CS0618 // Lifecycle hooks are obsolete, but still need to be tested until removed.
    {
        private readonly TaskCompletionSource _tcs = new();
        private readonly CountdownEvent _cevent = new(2); // AfterResourcesCreated and AfterEndpointsAllocated

        public IKubernetesService? KubernetesService { get; set; }

        public Task HooksCompleted => _tcs.Task;

        public async Task AfterEndpointsAllocatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken)
        {
            if (_cevent.Signal())
            {
                _tcs.TrySetResult();
            }
        }

        public async Task AfterResourcesCreatedAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken)
        {
            if (_cevent.Signal())
            {
                _tcs.TrySetResult();
            }
        }
    }

    private TestProgram CreateTestProgram(
        string testName,
        string[]? args = null,
        bool includeIntegrationServices = false,
        bool disableDashboard = true,
        bool randomizePorts = true,
        bool? trustDeveloperCertificate = false)
    {
        var testProgram = TestProgram.Create<DistributedApplicationTests>(
            testName,
            args,
            includeIntegrationServices: includeIntegrationServices,
            disableDashboard: disableDashboard,
            randomizePorts: randomizePorts,
            trustDeveloperCertificate: trustDeveloperCertificate);

        testProgram.AppBuilder.WithTestAndResourceLogging(_testOutputHelper);

        return testProgram;
    }
}
