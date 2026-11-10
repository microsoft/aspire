// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Sockets;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dcp.Model.V2;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class DcpResourceLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplicaFailureDoesNotPreventOtherReplicasFromStarting(bool configurationFailure)
    {
        var resource = new ExecutableResource("app", "app", ".");
        using var first = CreateProcess(resource, "app-0");
        using var second = CreateProcess(resource, "app-1");
        var observed = new ConcurrentQueue<string>();
        var failures = new ConcurrentQueue<OnResourceFailedToStartContext>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            observed.Enqueue($"snapshot:{context.DcpResourceName}");
            return Task.CompletedTask;
        });
        events.Subscribe<OnConnectionStringAvailableContext>(_ =>
        {
            observed.Enqueue("connection");
            return Task.CompletedTask;
        });
        events.Subscribe<OnResourceStartingContext>(context =>
        {
            observed.Enqueue($"starting:{context.DcpResourceName ?? "group"}");
            return Task.CompletedTask;
        });
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failures.Enqueue(context);
            observed.Enqueue($"failed:{context.DcpResourceName}");
            return Task.CompletedTask;
        });
        var lifecycle = CreateLifecycle(events);

        await CreateProcessesAsync(lifecycle, [first, second], (replica, _, _) =>
        {
            observed.Enqueue($"create:{replica.DcpResourceName}");
            if (ReferenceEquals(replica, first))
            {
                throw configurationFailure
                    ? new FailedToApplyEnvironmentException("configuration failed")
                    : new InvalidOperationException("create failed");
            }

            return Task.CompletedTask;
        });

        Assert.Equal(
            ["snapshot:app-0", "snapshot:app-1", "connection", "starting:group", "create:app-0", "failed:app-0", "create:app-1"],
            observed);
        var failure = Assert.Single(failures);
        Assert.Equal("app-0", failure.DcpResourceName);
        Assert.Equal(configurationFailure ? "configuration failed" : null, failure.ErrorMessage);
        Assert.True(first.Initialized.IsCompletedSuccessfully);
        Assert.True(second.Initialized.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredAndDelayedStartKeepConfigurationAndStartingEventsOrdered(bool registerBeforeStart)
    {
        var resource = new ExecutableResource("app", "app", ".");
        using var process = CreateProcess(resource, "app-0");
        var observed = new ConcurrentQueue<string>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(context =>
        {
            var snapshot = context.UpdateSnapshot(new CustomResourceSnapshot { ResourceType = "Executable", Properties = [] });
            observed.Enqueue($"snapshot:{snapshot.State?.Text ?? "initial"}");
            return Task.CompletedTask;
        });
        events.Subscribe<OnResourceStartingContext>(_ =>
        {
            observed.Enqueue("starting");
            return Task.CompletedTask;
        });
        events.Subscribe<OnConnectionStringAvailableContext>(_ =>
        {
            observed.Enqueue("connection");
            return Task.CompletedTask;
        });
        var lifecycle = CreateLifecycle(events);

        await CreateProcessesAsync(lifecycle, [process], (_, _, _) =>
        {
            observed.Enqueue("create");
            return Task.CompletedTask;
        }, isReady: registerBeforeStart, isDelayed: true);

        Assert.Equal(
            registerBeforeStart
                ? ["snapshot:initial", "snapshot:NotStarted", "create", "connection"]
                : new[] { "snapshot:initial", "snapshot:NotStarted" },
            observed);
        Assert.True(process.Initialized.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SnapshotFailureStillInitializesEveryReplica()
    {
        var resource = new ExecutableResource("app", "app", ".");
        using var first = CreateProcess(resource, "app-0");
        using var second = CreateProcess(resource, "app-1");
        var failures = new ConcurrentQueue<OnResourceFailedToStartContext>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnResourceChangedContext>(_ => throw new InvalidOperationException("snapshot failed"));
        events.Subscribe<OnResourceFailedToStartContext>(context =>
        {
            failures.Enqueue(context);
            return Task.CompletedTask;
        });
        var lifecycle = CreateLifecycle(events);
        var createCount = 0;

        await CreateProcessesAsync(lifecycle, [first, second], (_, _, _) =>
        {
            createCount++;
            return Task.CompletedTask;
        });

        Assert.Equal(0, createCount);
        Assert.Null(Assert.Single(failures).DcpResourceName);
        Assert.True(first.Initialized.IsCompletedSuccessfully);
        Assert.True(second.Initialized.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task EndpointEventsDeduplicateReplicasAndPreserveCrossResourceVisibility()
    {
        var first = new ExecutableResource("first", "app", ".");
        var second = new ExecutableResource("second", "app", ".");
        var firstEndpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http");
        var secondEndpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http");
        first.Annotations.Add(firstEndpoint);
        second.Annotations.Add(secondEndpoint);
        firstEndpoint.AllocatedEndpoint = new(firstEndpoint, "localhost", 20000);
        secondEndpoint.AllocatedEndpoint = new(secondEndpoint, "localhost", 20001);
        var batches = new List<string[]>();
        var events = new DcpExecutorEvents();
        events.Subscribe<OnEndpointsAllocatedContext>(context =>
        {
            Assert.NotNull(firstEndpoint.AllocatedEndpoint);
            Assert.NotNull(secondEndpoint.AllocatedEndpoint);
            batches.Add(context.Events.Select(@event => @event.Resource.Name).ToArray());
            return Task.CompletedTask;
        });
        var lifecycle = CreateLifecycle(events);

        await lifecycle.PublishEndpointsAllocatedAsync([first, first, second], CancellationToken.None);
        await lifecycle.PublishEndpointsAllocatedAsync([second, first], CancellationToken.None);

        Assert.Collection(batches, batch => Assert.Equal(["first", "second"], batch), Assert.Empty);
    }

    [Fact]
    public async Task RestartResetsConnectionEventsAndAllExecutionCallbackCaches()
    {
        var resource = new ExecutableResource("app", "app", ".");
        var environmentCalls = 0;
        var argumentCalls = 0;
        var launchToolCalls = 0;
        var connectionEvents = 0;
        resource.Annotations.Add(new EnvironmentCallbackAnnotation("VALUE", () => $"env-{++environmentCalls}"));
        resource.Annotations.Add(new CommandLineArgsCallbackAnnotation(args => args.Add($"arg-{++argumentCalls}")));
        resource.Annotations.Add(new LaunchToolArgsCallbackAnnotation(context =>
        {
            context.Args.Add($"tool-{++launchToolCalls}");
            return Task.CompletedTask;
        }, owningLaunchConfigurationType: null, showInCommandLine: true));
        var events = new DcpExecutorEvents();
        events.Subscribe<OnConnectionStringAvailableContext>(_ =>
        {
            connectionEvents++;
            return Task.CompletedTask;
        });
        var executionContext = CreateExecutionContext();
        var lifecycle = CreateLifecycle(events, executionContext);

        var first = await ResolveConfigurationAsync();
        var cached = await ResolveConfigurationAsync();
        Assert.Equal(["tool-1", "arg-1"], first.Arguments.Select(argument => argument.Value));
        Assert.Equal(first.Arguments, cached.Arguments);
        Assert.Equal("env-1", Assert.Single(cached.EnvironmentVariables, variable => variable.Key == "VALUE").Value);
        Assert.Equal(1, connectionEvents);

        lifecycle.ResetForRestart(resource);

        var restarted = await ResolveConfigurationAsync();
        Assert.Equal(["tool-2", "arg-2"], restarted.Arguments.Select(argument => argument.Value));
        Assert.Equal("env-2", Assert.Single(restarted.EnvironmentVariables, variable => variable.Key == "VALUE").Value);
        Assert.Equal(2, connectionEvents);
        Assert.Equal((2, 2, 2), (environmentCalls, argumentCalls, launchToolCalls));

        async Task<IExecutionConfigurationResult> ResolveConfigurationAsync()
        {
            var result = await ExecutionConfigurationBuilder.Create(resource)
                .WithArgumentsConfig()
                .WithEnvironmentVariablesConfig()
                .BuildAsync(executionContext, NullLogger.Instance);
            await lifecycle.PublishConnectionStringAvailableAsync(resource, CancellationToken.None);
            return result;
        }
    }

    [Fact]
    public async Task ConnectionEventWaitsForAllEndpointsAndPublishesOnceAcrossConcurrentReplicas()
    {
        var resource = new ExecutableResource("app", "app", ".");
        var endpoint = new EndpointAnnotation(ProtocolType.Tcp, name: "http");
        resource.Annotations.Add(endpoint);
        var connectionEvents = 0;
        var events = new DcpExecutorEvents();
        events.Subscribe<OnConnectionStringAvailableContext>(_ =>
        {
            Interlocked.Increment(ref connectionEvents);
            return Task.CompletedTask;
        });
        var lifecycle = CreateLifecycle(events);

        await lifecycle.PublishConnectionStringAvailableAsync(resource, CancellationToken.None);
        Assert.Equal(0, connectionEvents);

        endpoint.AllocatedEndpoint = new(endpoint, "localhost", 20000);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(
            () => lifecycle.PublishConnectionStringAvailableAsync(resource, CancellationToken.None))));

        Assert.Equal(1, connectionEvents);
    }

    [Fact]
    public void InitialStateAndInstanceLookupUseModelAnnotations()
    {
        var resource = new ExecutableResource("app", "app", ".");
        Assert.Null(DcpResourceLifecycle.GetInitialResourceState(resource));
        Assert.Throws<DistributedApplicationException>(() => DcpResourceLifecycle.GetDcpInstance(resource, 0));
        resource.Annotations.Add(new ResourceSnapshotAnnotation(new CustomResourceSnapshot
        {
            ResourceType = KnownResourceTypes.Executable,
            Properties = [],
            State = new ResourceStateSnapshot(KnownResourceStates.Waiting, null)
        }));
        var instance = new DcpInstance("app-replica", "replica", 1);
        resource.Annotations.Add(new DcpInstancesAnnotation([instance]));

        Assert.Equal(KnownResourceStates.Waiting, DcpResourceLifecycle.GetInitialResourceState(resource));
        Assert.Same(instance, DcpResourceLifecycle.GetDcpInstance(resource, 1));
        Assert.Throws<DistributedApplicationException>(() => DcpResourceLifecycle.GetDcpInstance(resource, 0));
    }

    [Fact]
    public async Task LifecycleOperationsWaitForInitializationAndSerialize()
    {
        var resource = new ExecutableResource("app", "app", ".");
        using var process = CreateProcess(resource, "app-0");
        using var cancellation = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var pending = DcpResourceLifecycle.AcquireInitializedResourceAsync(process, cancellation.Token).AsTask();
        Assert.False(pending.IsCompleted);

        process.MarkInitialized();
        var first = await pending.DefaultTimeout();
        try
        {
            var second = DcpResourceLifecycle.AcquireInitializedResourceAsync(process, cancellation.Token).AsTask();
            Assert.False(second.IsCompleted);
            first.Dispose();
            using var secondLease = await second.DefaultTimeout();
        }
        finally
        {
            first.Dispose();
        }

        Assert.Equal(1, process.SerializedOpSemaphore.CurrentCount);
    }

    private static RenderedModelResource<LogicalProcess, EndpointAnnotation> CreateProcess(IResource resource, string name)
        => new(resource, LogicalProcess.Create(name, "application"));

    private static DistributedApplicationExecutionContext CreateExecutionContext()
        => new(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
        {
            Services = TestServiceProvider.Instance
        });

    private static DcpResourceLifecycle CreateLifecycle(DcpExecutorEvents events, DistributedApplicationExecutionContext? executionContext = null)
        => new(new ConfigurationManager(), new ResourceLoggerService(), events, executionContext ?? CreateExecutionContext());

    private static Task CreateProcessesAsync(
        DcpResourceLifecycle lifecycle,
        RenderedModelResource<LogicalProcess, EndpointAnnotation>[] processes,
        Func<RenderedModelResource<LogicalProcess, EndpointAnnotation>, ILogger, CancellationToken, Task> create,
        bool isReady = true,
        bool isDelayed = false)
        => lifecycle.CreateResourcesAsync(
            processes, _ => KnownResourceTypes.Executable, _ => isReady, _ => isDelayed,
            _ => snapshot => snapshot, create, CancellationToken.None, CancellationToken.None);
}
