// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Orchestrator;

[Trait("Partition", "3")]
public class ApplicationOrchestratorTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("Waiting")]
    [InlineData("Starting")]
    [InlineData("Running")]
    public async Task ResourceStoppedEventIsNotRepeatedWhenHandlerChangesSnapshotState(string snapshotState)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var resource = builder.AddExecutable("resource", "unused", ".");
        var events = new DcpExecutorEvents();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        var stoppedCount = 0;

        resource.OnResourceStopped(async (_, @event, _) =>
        {
            Interlocked.Increment(ref stoppedCount);
            Assert.Equal(KnownResourceStates.Finished, @event.ResourceEvent.Snapshot.State?.Text);
            Assert.Equal(0, @event.ResourceEvent.Snapshot.ExitCode);
            await notifications.PublishUpdateAsync(resource.Resource, resource.Resource.Name,
                snapshot => snapshot with { State = snapshotState });
        });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var orchestrator = CreateOrchestrator(model, notificationService: notifications, dcpEvents: events, applicationEventing: builder.Eventing);
        await orchestrator.RunApplicationAsync();

        await PublishStateAsync(null, null);
        await PublishStateAsync(KnownResourceStates.Running, null);
        await PublishStateAsync(KnownResourceStates.Finished, KnownResourceStates.Running);
        Assert.Equal(1, stoppedCount);

        await PublishStateAsync(KnownResourceStates.Finished, KnownResourceStates.Finished);
        Assert.Equal(1, stoppedCount);

        await PublishStateAsync(KnownResourceStates.Exited, KnownResourceStates.Finished);
        await PublishStateAsync(KnownResourceStates.FailedToStart, KnownResourceStates.Exited);
        Assert.Equal(1, stoppedCount);

        await PublishStateAsync(KnownResourceStates.Running, KnownResourceStates.FailedToStart);
        await PublishStateAsync(KnownResourceStates.Finished, KnownResourceStates.Running);
        Assert.Equal(2, stoppedCount);

        Task PublishStateAsync(string? state, string? previousState) => events.PublishAsync(new OnResourceChangedContext(
            CancellationToken.None, KnownResourceTypes.Executable, resource.Resource, resource.Resource.Name,
            new ResourceStatus(state, null, null), previousState,
            snapshot => snapshot with { State = state is not null ? new(state, null) : snapshot.State, ExitCode = state == KnownResourceStates.Finished ? 0 : null }));
    }

    [Fact]
    public async Task ParentPropertySetOnChildResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentResource = builder.AddContainer("database", "image");
        var childResource = builder.AddResource(new CustomChildResource("child", parentResource.Resource));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentResourceId = null;
        string? childParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parentResource.Resource)
                {
                    parentResourceId = item.ResourceId;
                }
                else if (item.Resource == childResource.Resource)
                {
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentResourceId != null && childParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentResourceId, childParentResourceId);
    }

    [Fact]
    public async Task ParentAnnotationOnChildResource()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentResource = builder.AddResource(new CustomResource("parent"));
        var childResource = builder.AddResource(new CustomResource("child"))
            .WithParentRelationship(parentResource);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentResourceId = null;
        string? childParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parentResource.Resource)
                {
                    parentResourceId = item.ResourceId;
                }
                else if (item.Resource == childResource.Resource)
                {
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentResourceId != null && childParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentResourceId, childParentResourceId);
    }

    [Fact]
    public async Task InitializeResourceEventPublished()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var resource = builder.AddResource(new CustomResource("resource"));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var applicationEventing = builder.Eventing;

        var initResourceTcs = new TaskCompletionSource();
        InitializeResourceEvent? initEvent = null;
        resource.OnInitializeResource((_, @event, _) =>
        {
            initEvent = @event;
            initResourceTcs.SetResult();
            return Task.CompletedTask;
        });

        applicationEventing.Subscribe<InitializeResourceEvent>(resource.Resource, (@event, ct) =>
        {
            initEvent = @event;
            initResourceTcs.SetResult();
            return Task.CompletedTask;
        });

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: applicationEventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await initResourceTcs.Task; //.DefaultTimeout();

        Assert.True(initResourceTcs.Task.IsCompletedSuccessfully);
        Assert.NotNull(initEvent);
        Assert.NotNull(initEvent.Logger);
        Assert.NotNull(initEvent.Services);
        Assert.Equal(resource.Resource, initEvent.Resource);
        Assert.Equal(resourceNotificationService, initEvent.Notifications);
        Assert.Equal(applicationEventing, initEvent.Eventing);
    }

    [Fact]
    public async Task WithParentRelationshipSetsParentPropertyCorrectly()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parent = builder.AddContainer("parent", "image");
        var child = builder.AddContainer("child", "image").WithParentRelationship(parent);
        var child2 = builder.AddContainer("child2", "image").WithParentRelationship(parent);

        var nestedChild = builder.AddContainer("nested-child", "image").WithParentRelationship(child);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentResourceId = null;
        string? childResourceId = null;
        string? childParentResourceId = null;
        string? child2ParentResourceId = null;
        string? nestedChildParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parent.Resource)
                {
                    parentResourceId = item.ResourceId;
                }
                else if (item.Resource == child.Resource)
                {
                    childResourceId = item.ResourceId;
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }
                else if (item.Resource == nestedChild.Resource)
                {
                    nestedChildParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }
                else if (item.Resource == child2.Resource)
                {
                    child2ParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentResourceId != null && childParentResourceId != null && nestedChildParentResourceId != null && child2ParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentResourceId, childParentResourceId);
        Assert.Equal(parentResourceId, child2ParentResourceId);

        // Nested child should be parented on the direct parent
        Assert.Equal(childResourceId, nestedChildParentResourceId);
    }

    [Fact]
    public async Task LastWithParentRelationshipWins()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var firstParent = builder.AddContainer("firstParent", "image");
        var secondParent = builder.AddContainer("secondParent", "image");

        var child = builder.AddContainer("child", "image");

        child.WithParentRelationship(firstParent);
        child.WithParentRelationship(secondParent);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? firstParentResourceId = null;
        string? secondParentResourceId = null;
        string? childParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == firstParent.Resource)
                {
                    firstParentResourceId = item.ResourceId;
                }
                else if (item.Resource == secondParent.Resource)
                {
                    secondParentResourceId = item.ResourceId;
                }
                else if (item.Resource == child.Resource)
                {
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (firstParentResourceId != null && secondParentResourceId != null && childParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        // child should be parented to the last parent set
        Assert.Equal(secondParentResourceId, childParentResourceId);
    }

    [Fact]
    public async Task WithParentRelationshipWorksWithProjects()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var projectA = builder.AddProject<ProjectA>("projecta");
        var projectB = builder.AddProject<ProjectB>("projectb").WithParentRelationship(projectA);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? projectAResourceId = null;
        string? projectBParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == projectA.Resource)
                {
                    projectAResourceId = item.ResourceId;
                }
                else if (item.Resource == projectB.Resource)
                {
                    projectBParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (projectAResourceId != null && projectBParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(projectAResourceId, projectBParentResourceId);
    }

    [Fact]
    public void DetectsCircularDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container1 = builder.AddContainer("container1", "image");
        var container2 = builder.AddContainer("container2", "image2");
        var container3 = builder.AddContainer("container3", "image3");

        container1.WithParentRelationship(container2);
        container2.WithParentRelationship(container3);
        container3.WithParentRelationship(container1);

        using var app = builder.Build();

        var e = Assert.Throws<InvalidOperationException>(() => app.Services.GetService<ApplicationOrchestrator>());
        Assert.Contains("Circular dependency detected", e.Message);
    }

    [Fact]
    public async Task GrandChildResourceWithConnectionString()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentResource = builder.AddResource(new ParentResourceWithConnectionString("parent"));
        var childResource = builder.AddResource(
            new ChildResourceWithConnectionString("child", new Dictionary<string, string> { { "Namespace", "ns" } }, parentResource.Resource)
        );
        var grandChildResource = builder.AddResource(
            new ChildResourceWithConnectionString("grand-child", new Dictionary<string, string> { { "Database", "db" } }, childResource.Resource)
        );

        await using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var applicationEventing = new DistributedApplicationEventing();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: applicationEventing);
        await appOrchestrator.RunApplicationAsync();

        bool parentConnectionStringAvailable = false;
        bool childConnectionStringAvailable = false;
        bool grandChildConnectionStringAvailable = false;

        applicationEventing.Subscribe<ConnectionStringAvailableEvent>(parentResource.Resource, (_, _) =>
        {
            parentConnectionStringAvailable = true;
            return Task.CompletedTask;
        });
        applicationEventing.Subscribe<ConnectionStringAvailableEvent>(childResource.Resource, (_, _) =>
        {
            childConnectionStringAvailable = true;
            return Task.CompletedTask;
        });
        applicationEventing.Subscribe<ConnectionStringAvailableEvent>(grandChildResource.Resource, (_, _) =>
        {
            grandChildConnectionStringAvailable = true;
            return Task.CompletedTask;
        });

        await events.PublishAsync(new OnConnectionStringAvailableContext(CancellationToken.None, parentResource.Resource));

        Assert.True(parentConnectionStringAvailable);
        Assert.True(childConnectionStringAvailable);
        Assert.True(grandChildConnectionStringAvailable);
    }

    [Fact]
    public async Task ConnectionStringAvailableEventPublishesBeforeBeforeResourceStartedEvent()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var resource = builder.AddResource(new TestResourceWithConnectionString("test-resource", "Server=localhost:5432;Database=testdb"));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var applicationEventing = new DistributedApplicationEventing();
        var observedEvents = new List<string>();

        applicationEventing.Subscribe<ConnectionStringAvailableEvent>(resource.Resource, (_, _) =>
        {
            observedEvents.Add(nameof(ConnectionStringAvailableEvent));
            return Task.CompletedTask;
        });
        applicationEventing.Subscribe<BeforeResourceStartedEvent>(resource.Resource, (_, _) =>
        {
            observedEvents.Add(nameof(BeforeResourceStartedEvent));
            return Task.CompletedTask;
        });

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: applicationEventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Executable, resource.Resource, "test-resource-dcp"));
        await events.PublishAsync(new OnConnectionStringAvailableContext(CancellationToken.None, resource.Resource));
        await events.PublishAsync(new OnResourceBeforeStartContext(CancellationToken.None, resource.Resource, "test-resource-dcp"));

        Assert.Collection(
            observedEvents,
            eventName => Assert.Equal(nameof(ConnectionStringAvailableEvent), eventName),
            eventName => Assert.Equal(nameof(BeforeResourceStartedEvent), eventName));
    }

    [Fact]
    public async Task ConnectionStringAvailableEventUpdatesConnectionStringAndResolvablePropertiesWithoutDuplicates()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var unresolvedProperty = ReferenceExpression.Create($"{new ThrowingValueProvider()}");
        var resource = builder.AddResource(new TestResourceWithConnectionString("test-resource", "Server=localhost:5432;Database=testdb", "testdb", "localhost", unresolvedProperty));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var applicationEventing = new DistributedApplicationEventing();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: applicationEventing);
        await appOrchestrator.RunApplicationAsync();

        await applicationEventing.PublishAsync(new ConnectionStringAvailableEvent(resource.Resource, app.Services), CancellationToken.None);
        await applicationEventing.PublishAsync(new ConnectionStringAvailableEvent(resource.Resource, app.Services), CancellationToken.None);

        Assert.True(resourceNotificationService.TryGetCurrentState(resource.Resource.Name, out var currentState));
        Assert.Collection(
            currentState.Snapshot.Properties,
            connectionStringProperty =>
            {
                Assert.Equal(KnownProperties.Resource.ConnectionString, connectionStringProperty.Name);
                Assert.Equal("Server=localhost:5432;Database=testdb", connectionStringProperty.Value);
                Assert.True(connectionStringProperty.IsSensitive);
            },
            connectionPropertiesProperty =>
            {
                Assert.Equal(KnownProperties.Resource.ConnectionProperties, connectionPropertiesProperty.Name);
                Assert.Equal(
                    new Dictionary<string, string?>
                    {
                        ["DatabaseName"] = "testdb",
                        ["Host"] = "localhost"
                    },
                    Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(connectionPropertiesProperty.Value));
                Assert.True(connectionPropertiesProperty.IsSensitive);
            });
    }

    [Fact]
    public async Task OnResourceFailedToStart_WithErrorMessage_SetsErrorStyleOnState()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var container = builder.AddContainer("api", "test-image");

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourceFailedToStartContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            container.Resource,
            "api-dcp",
            ErrorMessage: "The endpoint `https` is not defined for the resource `api`. Available endpoints: `http`."));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var snapshotEvent));
        Assert.Equal(KnownResourceStates.FailedToStart, snapshotEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, snapshotEvent.Snapshot.State?.Style);
    }

    [Fact]
    public async Task OnResourceFailedToStart_WithoutErrorMessage_DoesNotSetErrorStyle()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var container = builder.AddContainer("api", "test-image");

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourceFailedToStartContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            container.Resource,
            "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var snapshotEvent));
        Assert.Equal(KnownResourceStates.FailedToStart, snapshotEvent.Snapshot.State?.Text);
        Assert.Null(snapshotEvent.Snapshot.State?.Style);
    }

    [Fact]
    public async Task OnResourceStarting_ToolResourceType_TransitionsToStarting()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var resource = builder.AddResource(new CustomResource("tool"));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Tool, resource.Resource, "tool-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("tool-dcp", out var snapshotEvent));
        Assert.Equal(KnownResourceStates.Starting, snapshotEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task SelfDrivenResourceWaitsForDependencyBeforeStarting()
    {
        // Custom resources that drive their own startup publish BeforeResourceStartedEvent from
        // OnInitializeResource, so they are still in the initial "NotStarted" state when the wait is
        // published rather than having been moved to "Starting" by the orchestrator first.
        // https://github.com/microsoft/aspire/issues/17453
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var blockerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = AddSelfDrivenResource(builder, "blocker", blockerReleased.Task);
        var waiter = AddSelfDrivenResource(builder, "waiter").WaitFor(blocker);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Waiting, TestContext.Current.CancellationToken).DefaultTimeout();

        // The blocker is still inside its initialize callback, so the waiter must not have started.
        Assert.True(resourceNotificationService.TryGetCurrentState(waiter.Resource.Name, out var waitingEvent));
        Assert.Equal(KnownResourceStates.Waiting, waitingEvent.Snapshot.State?.Text);

        blockerReleased.SetResult();

        await resourceNotificationService.WaitForResourceAsync(blocker.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
    }

    [Fact]
    public async Task ResourceWaitingOnSelfDrivenResourceWaitsForItToStart()
    {
        // The reciprocal of SelfDrivenResourceWaitsForDependencyBeforeStarting: the dependency is the resource
        // that drives its own startup, while the waiter is a regular DCP-managed resource that the orchestrator
        // starts through OnResourceStarting. https://github.com/microsoft/aspire/issues/17453
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var dependencyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependency = AddSelfDrivenResource(builder, "dependency", dependencyReleased.Task);
        var waiter = builder.AddResource(new CustomResourceWithWaitSupport("waiter")).WaitFor(dependency);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        // Stand in for DCP publishing the starting state and then waiting before launch. The resource name is
        // used as the DCP name so the per-instance starting update and model-level wait share a snapshot.
        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, waiter.Resource.Name));
        var startingTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, waiter.Resource.Name));

        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Waiting, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState(dependency.Resource.Name, out var dependencyEvent));
        Assert.NotEqual(KnownResourceStates.Running, dependencyEvent.Snapshot.State?.Text);

        dependencyReleased.SetResult();

        await resourceNotificationService.WaitForResourceAsync(dependency.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
        await startingTask.DefaultTimeout();
    }

    [Fact]
    public async Task WaitIsNotReleasedByAnotherReplicaLeavingWaitingState()
    {
        // The wait is released when the resource is forced out of "Waiting" by the start command, but
        // WaitForResourceAsync only filters on the model resource name, so the release signal has to be scoped
        // to the replica that was forced out. Here the model-level waiting update also re-publishes the running
        // sibling replica, and that must not be mistaken for a force-start of the waiting replica.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var dependencyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependency = AddSelfDrivenResource(builder, "dependency", dependencyReleased.Task);
        var waiter = builder.AddResource(new CustomResourceWithWaitSupport("waiter")).WaitFor(dependency);
        waiter.Resource.Annotations.Add(new DcpInstancesAnnotation([
            new DcpInstance("waiter-abc123", "abc123", 0),
            new DcpInstance("waiter-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        // The second replica is already running and is not part of this start attempt.
        await resourceNotificationService.PublishUpdateAsync(waiter.Resource, "waiter-def456", s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, "waiter-abc123"));
        var startingTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, "waiter-abc123"));

        await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            e => e.ResourceId == "waiter-abc123" && e.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        // The rebuild command moves non-waiting replicas to "Building" while deliberately leaving waiting
        // replicas alone, so this is a state change a waiting replica really does observe from a sibling.
        await resourceNotificationService.PublishUpdateAsync(waiter.Resource, "waiter-def456", s => s with
        {
            State = KnownResourceStates.Building
        }).DefaultTimeout();

        // Negative assertion: give the wait a chance to be released incorrectly. This can only ever produce a
        // false pass, never a false failure - with a model-wide release flag the wait is abandoned immediately.
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        Assert.False(startingTask.IsCompleted, "The dependency wait must not be released by a sibling replica leaving the waiting state.");

        dependencyReleased.SetResult();

        await startingTask.DefaultTimeout();
    }

    [Fact]
    public async Task WaitingUpdateDoesNotDisturbNotStartedReplicas()
    {
        // A replicated resource that uses WithExplicitStart is started one replica at a time, so only the replica
        // named in the start request is moved to "Starting". The waiting update, however, is published as a
        // model-level update that reaches every replica, so a sibling that is still "NotStarted" - deliberately not
        // started - must be left alone.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var dependencyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependency = AddSelfDrivenResource(builder, "dependency", dependencyReleased.Task);
        var waiter = builder.AddResource(new CustomResourceWithWaitSupport("waiter")).WaitFor(dependency).WithExplicitStart();
        waiter.Resource.Annotations.Add(new DcpInstancesAnnotation([
            new DcpInstance("waiter-abc123", "abc123", 0),
            new DcpInstance("waiter-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        // Both replicas of an explicit start resource report "NotStarted" until someone starts them.
        await PublishReplicaStatesAsync(resourceNotificationService, waiter.Resource, KnownResourceStates.NotStarted);

        // Only the first replica is being started.
        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, "waiter-abc123"));
        var startingTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, "waiter-abc123"));

        await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            e => e.ResourceId == "waiter-abc123" && e.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState("waiter-def456", out var siblingEvent));
        Assert.Equal(KnownResourceStates.NotStarted, siblingEvent.Snapshot.State?.Text);

        dependencyReleased.SetResult();

        await startingTask.DefaultTimeout();
    }

    [Fact]
    public async Task StartingNotStartedReplicaIsForwardedToDcpWhileSiblingWaits()
    {
        // The consequence of WaitingUpdateDoesNotDisturbNotStartedReplicas: StartResourceAsync reads "Waiting" as
        // proof that startup is already in flight and only updates the snapshot, skipping DCP. If the waiting update
        // relabelled the untouched replica, its start command would silently do nothing - the dashboard would show
        // "Starting" while no process was ever launched.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var dependencyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependency = AddSelfDrivenResource(builder, "dependency", dependencyReleased.Task);
        var waiter = builder.AddResource(new CustomResourceWithWaitSupport("waiter")).WaitFor(dependency).WithExplicitStart();
        waiter.Resource.Annotations.Add(new DcpInstancesAnnotation([
            new DcpInstance("waiter-abc123", "abc123", 0),
            new DcpInstance("waiter-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var dcpExecutor = new TestDcpExecutor();
        dcpExecutor.AddResource(waiter.Resource, "waiter-abc123");
        dcpExecutor.AddResource(waiter.Resource, "waiter-def456");

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing, dcpExecutor: dcpExecutor);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await PublishReplicaStatesAsync(resourceNotificationService, waiter.Resource, KnownResourceStates.NotStarted);

        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, "waiter-abc123"));
        var startingTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, "waiter-abc123"));

        await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            e => e.ResourceId == "waiter-abc123" && e.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        // Stand in for the user invoking the start command on the replica that was never started.
        await appOrchestrator.StartResourceAsync("waiter-def456", TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.Equal(["waiter-def456"], dcpExecutor.StartedResources);

        dependencyReleased.SetResult();

        await startingTask.DefaultTimeout();
    }

    [Fact]
    public async Task ForceStartingOneReplicaDoesNotReleaseAnotherReplicasWait()
    {
        // Each individually started replica gets its own BeforeResourceStartedEvent, but WaitForResourceAsync
        // filters only on the model resource name, so every one of those waits is fed events for every replica.
        // Forcing replica B out of "Waiting" with the start command must not release replica A's wait - the user
        // only asked for B, and A would otherwise start with unmet dependencies.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var dependencyReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependency = AddSelfDrivenResource(builder, "dependency", dependencyReleased.Task);
        var waiter = builder.AddResource(new CustomResourceWithWaitSupport("waiter")).WaitFor(dependency);
        waiter.Resource.Annotations.Add(new DcpInstancesAnnotation([
            new DcpInstance("waiter-abc123", "abc123", 0),
            new DcpInstance("waiter-def456", "def456", 1)
        ]));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var dcpExecutor = new TestDcpExecutor();
        dcpExecutor.AddResource(waiter.Resource, "waiter-abc123");
        dcpExecutor.AddResource(waiter.Resource, "waiter-def456");

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing, dcpExecutor: dcpExecutor);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await PublishReplicaStatesAsync(resourceNotificationService, waiter.Resource, KnownResourceStates.NotStarted);

        // Both replicas are started, so both end up waiting on the same dependency.
        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, "waiter-abc123"));
        var startingFirstReplicaTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, "waiter-abc123"));

        await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            e => e.ResourceId == "waiter-abc123" && e.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, waiter.Resource, "waiter-def456"));
        var startingSecondReplicaTask = events.PublishAsync(new OnResourceBeforeStartContext(
            CancellationToken.None, waiter.Resource, "waiter-def456"));

        await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            e => e.ResourceId == "waiter-def456" && e.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        // Stand in for the user invoking the start command on the second replica to force it past the wait.
        await appOrchestrator.StartResourceAsync("waiter-def456", TestContext.Current.CancellationToken).DefaultTimeout();

        // The replica was already waiting, so the start was served by moving it to "Starting" rather than by DCP.
        Assert.Empty(dcpExecutor.StartedResources);

        await startingSecondReplicaTask.DefaultTimeout();

        // Negative assertion: give the first replica's wait a chance to be released incorrectly. This can only
        // ever produce a false pass, never a false failure - with a model-wide release flag the wait is abandoned
        // as soon as the second replica leaves "Waiting".
        await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        Assert.False(startingFirstReplicaTask.IsCompleted, "Force-starting one replica must not release another replica's dependency wait.");

        dependencyReleased.SetResult();

        await startingFirstReplicaTask.DefaultTimeout();
    }

    [Fact]
    public async Task ResourceForcedOutOfWaitingStopsWaitingForItsDependency()
    {
        // The Start command forces a waiting resource to start by moving it from "Waiting" to "Starting"
        // (see ApplicationOrchestrator.StartResourceAsync). BeforeResourceStartedEvent has to stop waiting
        // when that happens, even though the dependency is still not ready.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var blockerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = AddSelfDrivenResource(builder, "blocker", blockerReleased.Task);
        var waiter = AddSelfDrivenResource(builder, "waiter").WaitFor(blocker);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Waiting, TestContext.Current.CancellationToken).DefaultTimeout();

        // Stand in for the Start command.
        await resourceNotificationService.PublishUpdateAsync(waiter.Resource, s => s with { State = KnownResourceStates.Starting }).DefaultTimeout();

        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState(blocker.Resource.Name, out var blockerEvent));
        Assert.NotEqual(KnownResourceStates.Running, blockerEvent.Snapshot.State?.Text);

        blockerReleased.SetResult();
    }

    [Fact]
    public async Task ResourceForcedOutOfWaitingImmediatelyStopsWaitingForItsDependency()
    {
        // Regression test for a race between publishing "Waiting" and subscribing to state changes.
        // WaitForInBeforeResourceStartedEvent detects a force-start by observing the resource leave "Waiting",
        // and ResourceNotificationService.WatchAsync only replays the latest snapshot of each resource rather
        // than the full history. If the Start command lands in the window between the wait publishing "Waiting"
        // and the orchestrator subscribing, a replay-only view shows "Starting" without ever showing "Waiting",
        // and the force-start signal is lost - the resource stays blocked on a dependency that never becomes
        // ready. https://github.com/microsoft/aspire/pull/18930#discussion_r3677777200
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var blockerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = AddSelfDrivenResource(builder, "blocker", blockerReleased.Task);
        var waiter = AddSelfDrivenResource(builder, "waiter").WaitFor(blocker);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        // Stand in for the Start command, firing as soon as the waiting state is stored. PublishUpdateAsync
        // stores the snapshot synchronously, so a tight poll observes "Waiting" well before any watcher started
        // afterwards could - which is exactly the window the fix has to cover. A delay-based poll or a watcher
        // subscription would only see "Waiting" after the orchestrator had already subscribed, so the regression
        // would go unnoticed. Losing the poll only ever produces a false pass, never a false failure.
        var cancellationToken = TestContext.Current.CancellationToken;
        var forceStartTask = Task.Run(async () =>
        {
            // Wait for the waiter's initial snapshot first so the tight poll below only spins across the short
            // window in which the dependency wait is set up, rather than for the whole of application startup.
            // "Waiting" is tolerated as well: if the poll is late the snapshot has already moved on, and the
            // loop below exits immediately.
            await resourceNotificationService.WaitForResourceAsync(
                waiter.Resource.Name,
                [KnownResourceStates.NotStarted, KnownResourceStates.Waiting],
                cancellationToken);

            while (!resourceNotificationService.TryGetCurrentState(waiter.Resource.Name, out var waiterEvent)
                || waiterEvent.Snapshot.State?.Text != KnownResourceStates.Waiting)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            await resourceNotificationService.PublishUpdateAsync(waiter.Resource, s => s with { State = KnownResourceStates.Starting });
        }, cancellationToken);

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await forceStartTask.DefaultTimeout();

        // The waiter has to start even though the blocker is still inside its initialize callback.
        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Running, cancellationToken).DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState(blocker.Resource.Name, out var blockerEvent));
        Assert.NotEqual(KnownResourceStates.Running, blockerEvent.Snapshot.State?.Text);

        blockerReleased.SetResult();
    }

    [Fact]
    public async Task ConnectionStringResourceWaitsForReferencedResourceBeforeBecomingAvailable()
    {
        // AddConnectionString has the same shape as a third-party custom resource: it starts in "NotStarted"
        // and publishes BeforeResourceStartedEvent from OnInitializeResource, and it implicitly adds a
        // WaitForStart on every resource its expression references. It must not report a connection string
        // before those resources have started. https://github.com/microsoft/aspire/issues/17453
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var referencedReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var referenced = AddSelfDrivenResource(builder, "referenced", referencedReleased.Task);
        var connectionString = builder.AddConnectionString("cs", ReferenceExpression.Create($"Endpoint={referenced.Resource}"));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await resourceNotificationService.WaitForResourceAsync(connectionString.Resource.Name, KnownResourceStates.Waiting, TestContext.Current.CancellationToken).DefaultTimeout();

        referencedReleased.SetResult();

        await resourceNotificationService.WaitForResourceAsync(referenced.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
        await resourceNotificationService.WaitForResourceAsync(connectionString.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
    }

    [Fact]
    public async Task ExplicitStartResourceWithoutDcpInstancesReportsWaitingForItsDependency()
    {
        // Resources not managed by Aspire (no instances) should transition to "Waiting" directly from NotStarted,
        // regardless whether they have ExplicitStartAnnotation or not. The annotation does not really makes a difference here
        // because it is only used to control the behavior of the orchestrator when starting a resource
        // and since the resource has no instances, it is not subject to the orchestrator's start logic.
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var blockerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = AddSelfDrivenResource(builder, "blocker", blockerReleased.Task);
        var waiter = builder.AddConnectionString("waiter", ReferenceExpression.Create($"Host=localhost;Port=5678"))
            .WaitFor(blocker)
            .WithExplicitStart();

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: builder.Eventing);
        await appOrchestrator.RunApplicationAsync();

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        var waitingEvent = await resourceNotificationService.WaitForResourceAsync(
            waiter.Resource.Name,
            re => re.Snapshot.State?.Text == KnownResourceStates.Waiting,
            TestContext.Current.CancellationToken).DefaultTimeout();

        var waitingFor = waitingEvent.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.WaitingFor)?.Value;
        Assert.Equal(new[] { blocker.Resource.Name }, Assert.IsAssignableFrom<IEnumerable<string>>(waitingFor));

        blockerReleased.SetResult();

        // The resource starts without anyone issuing a start command, which is what makes "Waiting" - rather than
        // "NotStarted" - the honest label for the period above.
        await resourceNotificationService.WaitForResourceAsync(blocker.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
        await resourceNotificationService.WaitForResourceAsync(waiter.Resource.Name, KnownResourceStates.Running, TestContext.Current.CancellationToken).DefaultTimeout();
    }

    /// <summary>
    /// Publishes <paramref name="state"/> for every DCP instance (replica) of <paramref name="resource"/>, as an
    /// individual per-instance update rather than a model-level one.
    /// </summary>
    private static async Task PublishReplicaStatesAsync(ResourceNotificationService notificationService, IResource resource, string state)
    {
        Assert.True(resource.TryGetInstances(out var instances));

        foreach (var instance in instances)
        {
            await notificationService.PublishUpdateAsync(resource, instance.Name, s => s with { State = state }).DefaultTimeout();
        }
    }

    /// <summary>
    /// Adds a resource shaped like a custom hosting integration that drives its own startup: it starts in
    /// <see cref="KnownResourceStates.NotStarted"/> and publishes <see cref="BeforeResourceStartedEvent"/> itself
    /// instead of being started by DCP.
    /// </summary>
    private static IResourceBuilder<CustomResourceWithWaitSupport> AddSelfDrivenResource(IDistributedApplicationBuilder builder, string name, Task? gate = null)
    {
        return builder.AddResource(new CustomResourceWithWaitSupport(name))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "CustomResource",
                State = KnownResourceStates.NotStarted,
                Properties = []
            })
            .OnInitializeResource(async (resource, @event, ct) =>
            {
                // This is where waiting happens.
                await @event.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, @event.Services), ct);

                if (gate is not null)
                {
                    await gate;
                }

                // ResourceHealthCheckService isn't running in this harness, so stand in for it and publish the
                // ready event a resource with no health checks would otherwise get. Without it, WaitFor - which
                // waits until healthy - could never observe the dependency as ready.
                await @event.Notifications.PublishUpdateAsync(resource, s => s with
                {
                    State = KnownResourceStates.Running,
                    ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
                });
            });
    }

    [Fact]
    public async Task OnResourceStarting_WithUnresolvedParameters_PublishesParameterNamesUntilResolved()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var firstParameter = builder.AddParameter("first", () => throw new MissingParameterValueException("Missing first parameter."));
        var secondParameter = builder.AddParameter("second", () => throw new MissingParameterValueException("Missing second parameter."));
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("FIRST", firstParameter)
            .WithEnvironment("SECOND", secondParameter);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Warn, unresolvedEvent.Snapshot.State?.Style);
        Assert.Equal(
            ["first", "second"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        var configurationTask = ExecutionConfigurationBuilder.Create(container.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance);
        Assert.False(configurationTask.IsCompleted);

        var partiallyResolvedTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                Assert.IsType<string[]>(Assert.Single(e.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value)
                    .SequenceEqual(["second"]));
        Assert.True(firstParameter.Resource.WaitForValueTcs!.TrySetResult("first-value"));
        _ = await partiallyResolvedTask.DefaultTimeout();
        Assert.False(configurationTask.IsCompleted);

        var startingTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Starting);
        Assert.True(secondParameter.Resource.WaitForValueTcs!.TrySetResult("second-value"));
        var startingEvent = await startingTask.DefaultTimeout();
        var configuration = await configurationTask.DefaultTimeout();

        Assert.DoesNotContain(startingEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters);
        Assert.Null(configuration.Exception);
        Assert.Equal("first-value", configuration.EnvironmentVariables.Single(e => e.Key == "FIRST").Value);
        Assert.Equal("second-value", configuration.EnvironmentVariables.Single(e => e.Key == "SECOND").Value);
    }

    [Fact]
    public async Task OnResourceStarting_WithDistinctSameNamedParameters_WaitsForEveryParameter()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var firstParameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var secondParameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("FIRST", firstParameter)
            .WithEnvironment("SECOND", secondParameter);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        Assert.NotNull(firstParameter.WaitForValueTcs);
        Assert.NotNull(secondParameter.WaitForValueTcs);

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        var configurationTask = ExecutionConfigurationBuilder.Create(container.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance);
        await firstParameter.SetValueAsync("first-value").DefaultTimeout();
        Assert.False(configurationTask.IsCompleted);
        Assert.False(secondParameter.WaitForValueTcs.Task.IsCompleted);

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var partiallyResolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, partiallyResolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(partiallyResolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        var startingTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Starting);
        Assert.True(secondParameter.WaitForValueTcs.TrySetResult("second-value"));
        _ = await startingTask.DefaultTimeout();
        var configuration = await configurationTask.DefaultTimeout();
        Assert.Null(configuration.Exception);
        Assert.Equal("first-value", configuration.EnvironmentVariables.Single(e => e.Key == "FIRST").Value);
        Assert.Equal("second-value", configuration.EnvironmentVariables.Single(e => e.Key == "SECOND").Value);
    }

    [Fact]
    public async Task OnResourceStarting_WithUnresolvedParameter_DoesNotInvokeLaunchCallbacks()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var environmentCallbackCount = 0;
        var argumentsCallbackCount = 0;
        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter)
            .WithEnvironment(context =>
            {
                Interlocked.Increment(ref environmentCallbackCount);
                context.EnvironmentVariables["OTHER"] = "other-value";
            })
            .WithArgs(context =>
            {
                Interlocked.Increment(ref argumentsCallbackCount);
                context.Args.Add("--other");
            });

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.Equal(0, environmentCallbackCount);
        Assert.Equal(0, argumentsCallbackCount);

        Assert.True(parameter.Resource.WaitForValueTcs!.TrySetResult("resolved-value"));
        var gathererContext = new ExecutionConfigurationGathererContext();
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);
        await new EnvironmentVariablesExecutionConfigurationGatherer().GatherAsync(gathererContext, container.Resource, NullLogger.Instance, executionContext);
        await new ArgumentsExecutionConfigurationGatherer().GatherAsync(gathererContext, container.Resource, NullLogger.Instance, executionContext);

        Assert.Equal(1, environmentCallbackCount);
        Assert.Equal(1, argumentsCallbackCount);
    }

    [Fact]
    public async Task OnResourceStarting_WithUnresolvedParameterInConnectionString_PublishesParameterName()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var resource = builder.AddResource(new ParameterConnectionStringResource("database", parameter.Resource));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Executable, resource.Resource, "database-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("database-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));
    }

    [Fact]
    public async Task OnResourceStarting_WithReferencedOnlyParameterInReferencedConnectionString_PublishesParameterName()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var source = builder.AddResource(new ParameterConnectionStringResource("database", parameter));
        var consumer = builder.AddContainer("api", "test-image")
            .WithReference(source);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.DoesNotContain(parameter, distributedAppModel.Resources);

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        Assert.NotNull(parameter.WaitForValueTcs);
        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, consumer.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));
    }

    [Fact]
    public async Task OnResourceStarting_WithReferencedOnlyParameterInConnectionProperty_PublishesParameterName()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var source = builder.AddResource(new TestResourceWithConnectionString("database", "Value=constant"))
            .WithConnectionProperty("property", ReferenceExpression.Create($"{parameter}"));
        var consumer = builder.AddContainer("api", "test-image")
            .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.ConnectionProperties)
            .WithReference(source);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.DoesNotContain(parameter, distributedAppModel.Resources);

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        Assert.NotNull(parameter.WaitForValueTcs);
        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, consumer.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnResourceStarting_WithUnresolvedParameterInOwnConnectionProperty_PublishesParameterName(bool parameterInModel)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = parameterInModel
            ? builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter.")).Resource
            : ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var resource = builder.AddResource(new TestResourceWithConnectionString(
            "database", "Value=constant", unresolvedProperty: ReferenceExpression.Create($"{parameter}")));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(TestContext.Current.CancellationToken));
        await events.PublishAsync(new OnResourceStartingContext(
            TestContext.Current.CancellationToken, KnownResourceTypes.Executable, resource.Resource, resource.Resource.Name));

        var connectionStringTask = events.PublishAsync(new OnConnectionStringAvailableContext(
            TestContext.Current.CancellationToken, resource.Resource));
        Assert.False(connectionStringTask.IsCompleted);
        Assert.True(resourceNotificationService.TryGetCurrentState(resource.Resource.Name, out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        var startingTask = resourceNotificationService.WaitForResourceAsync(
            resource.Resource.Name, KnownResourceStates.Starting, TestContext.Current.CancellationToken);
        await parameter.SetValueAsync("resolved-value", TestContext.Current.CancellationToken).DefaultTimeout();
        await connectionStringTask.DefaultTimeout();
        await startingTask.DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState(resource.Resource.Name, out var resolvedEvent));
        var connectionProperties = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string?>>(
            Assert.Single(resolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.ConnectionProperties).Value);
        Assert.Collection(connectionProperties, property =>
        {
            Assert.Equal("Unavailable", property.Key);
            Assert.Equal("resolved-value", property.Value);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnResourceStarting_WithOverriddenParameterAndWaitFor_DoesNotWaitForUnusedValue(bool dependencyAlreadyReady)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var dependency = builder.AddContainer("dependency", "test-image");
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter)
            .WithEnvironment("VALUE", "literal")
            .WaitFor(dependency);

        using var app = builder.Build();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var events = new DcpExecutorEvents();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        var orchestrator = CreateOrchestrator(model, notifications, dcpEvents: events);
        await orchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(cts.Token));

        if (dependencyAlreadyReady)
        {
            await SetDependencyReadyAsync();
        }

        await events.PublishAsync(new OnResourceStartingContext(
            cts.Token, KnownResourceTypes.Container, container.Resource, container.Resource.Name));
        var startingTask = StartResourceAsync();

        try
        {
            if (!dependencyAlreadyReady)
            {
                await notifications.WaitForResourceAsync(container.Resource.Name,
                    e => e.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.WaitingFor)?.Value is string[] waitingFor &&
                        waitingFor.SequenceEqual([dependency.Resource.Name]), cts.Token).DefaultTimeout();
                Assert.False(startingTask.IsCompleted);
                await SetDependencyReadyAsync();
            }

            var configuration = await startingTask.DefaultTimeout();
            Assert.Null(configuration.Exception);
            Assert.Collection(configuration.EnvironmentVariables, environmentVariable =>
            {
                Assert.Equal("VALUE", environmentVariable.Key);
                Assert.Equal("literal", environmentVariable.Value);
            });
            Assert.NotNull(parameter.Resource.WaitForValueTcs);
            Assert.False(parameter.Resource.WaitForValueTcs.Task.IsCompleted);
        }
        finally
        {
            cts.Cancel();
        }

        Task SetDependencyReadyAsync() => notifications.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        });

        async Task<IExecutionConfigurationResult> StartResourceAsync()
        {
            await events.PublishAsync(new OnResourceBeforeStartContext(cts.Token, container.Resource, container.Resource.Name));

            return await ExecutionConfigurationBuilder.Create(container.Resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance, cts.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnResourceStarting_WithUnresolvedParameterAndWaitFor_WaitsForBothBlockers(bool dependencyResolvesFirst)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var dependency = builder.AddContainer("dependency", "test-image");
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter)
            .WaitFor(dependency);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        var waitingForEventTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                e.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.WaitingFor)?.Value is string[] waitingFor &&
                waitingFor.SequenceEqual([dependency.Resource.Name]));
        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            container.Resource,
            container.Resource.GetResolvedResourceNames()[0]));
        var startingTask = BuildConfigurationAfterDependenciesAsync();

        var firstCompletedTask = await Task.WhenAny(startingTask, waitingForEventTask).DefaultTimeout();
        Assert.Same(waitingForEventTask, firstCompletedTask);
        _ = await waitingForEventTask.DefaultTimeout();

        var remainingBlockerEventTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => dependencyResolvesFirst
                ? e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                    e.Snapshot.Properties.All(p => p.Name != KnownProperties.Resource.WaitingFor)
                : e.Snapshot.State?.Text == KnownResourceStates.Waiting &&
                    e.Snapshot.Properties.All(p => p.Name != KnownProperties.Resource.UnresolvedParameters));

        if (dependencyResolvesFirst)
        {
            await resourceNotificationService.PublishUpdateAsync(dependency.Resource, s => s with
            {
                State = KnownResourceStates.Running,
                ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
            }).DefaultTimeout();
        }
        else
        {
            Assert.True(parameter.Resource.WaitForValueTcs!.TrySetResult("resolved-value"));
        }

        _ = await remainingBlockerEventTask.DefaultTimeout();
        Assert.False(startingTask.IsCompleted);

        if (dependencyResolvesFirst)
        {
            Assert.True(parameter.Resource.WaitForValueTcs!.TrySetResult("resolved-value"));
        }
        else
        {
            await resourceNotificationService.PublishUpdateAsync(dependency.Resource, s => s with
            {
                State = KnownResourceStates.Running,
                ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
            }).DefaultTimeout();
        }

        var configuration = await startingTask.DefaultTimeout();
        Assert.Null(configuration.Exception);
        Assert.Equal("resolved-value", configuration.EnvironmentVariables.Single(e => e.Key == "VALUE").Value);

        async Task<IExecutionConfigurationResult> BuildConfigurationAfterDependenciesAsync()
        {
            await events.PublishAsync(new OnResourceBeforeStartContext(CancellationToken.None, container.Resource, container.Resource.GetResolvedResourceNames()[0]));

            return await ExecutionConfigurationBuilder.Create(container.Resource)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnResourceStarting_ParameterDeletedWhileWaitingForDependency_TracksReplacementValue(bool overlapValueChanges)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var dependency = builder.AddContainer("dependency", "test-image");
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter)
            .WaitFor(dependency);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(
            distributedAppModel,
            resourceNotificationService,
            out var parameterProcessor,
            dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        var combinedBlockersTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                e.Snapshot.Properties.Any(p => p.Name == KnownProperties.Resource.WaitingFor));
        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            container.Resource,
            container.Resource.GetResolvedResourceNames()[0]));
        var beforeStartTask = events.PublishAsync(new OnResourceBeforeStartContext(CancellationToken.None, container.Resource, container.Resource.GetResolvedResourceNames()[0]));
        _ = await combinedBlockersTask.DefaultTimeout();

        var waitingTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Waiting);
        await parameterProcessor.SetParameterCoreAsync(parameter.Resource, CreateSetParameterArguments("first-value"), CancellationToken.None);
        _ = await waitingTask.DefaultTimeout();

        var replacementBlockerTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                e.Snapshot.Properties.Any(p => p.Name == KnownProperties.Resource.WaitingFor));
        if (overlapValueChanges)
        {
            var notificationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resumeNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var notificationCount = 0;
            parameterProcessor.ParameterBecameUnresolved += async _ =>
            {
                if (Interlocked.Increment(ref notificationCount) == 1)
                {
                    notificationStarted.SetResult();
                    await resumeNotification.Task;
                }
            };

            var firstDelete = parameterProcessor.DeleteParameterCoreAsync(parameter.Resource, new InteractionInputCollection([]), CancellationToken.None);
            Task valueChange;
            Task secondDelete;
            try
            {
                await notificationStarted.Task.DefaultTimeout();
                valueChange = parameter.Resource.SetValueAsync("superseded-value");
                secondDelete = parameterProcessor.DeleteParameterCoreAsync(parameter.Resource, new InteractionInputCollection([]), CancellationToken.None);
            }
            finally
            {
                resumeNotification.TrySetResult();
            }
            await Task.WhenAll(firstDelete, valueChange, secondDelete).DefaultTimeout();
            Assert.Equal(2, Volatile.Read(ref notificationCount));
        }
        else
        {
            await parameterProcessor.DeleteParameterCoreAsync(parameter.Resource, new InteractionInputCollection([]), CancellationToken.None);
        }
        _ = await replacementBlockerTask.DefaultTimeout();

        var configurationTask = ExecutionConfigurationBuilder.Create(container.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance);
        Assert.False(configurationTask.IsCompleted);

        var parameterOnlyBlockerTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.UnresolvedParameters &&
                e.Snapshot.Properties.All(p => p.Name != KnownProperties.Resource.WaitingFor));
        await resourceNotificationService.PublishUpdateAsync(dependency.Resource, s => s with
        {
            State = KnownResourceStates.Running,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        }).DefaultTimeout();
        var unresolvedEvent = await parameterOnlyBlockerTask.DefaultTimeout();
        await beforeStartTask.DefaultTimeout();
        Assert.False(configurationTask.IsCompleted);

        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.WaitingFor);

        var startingTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name,
            e => e.Snapshot.State?.Text == KnownResourceStates.Starting);
        await parameterProcessor.SetParameterCoreAsync(parameter.Resource, CreateSetParameterArguments("final-value"), CancellationToken.None);
        _ = await startingTask.DefaultTimeout();

        var configuration = await configurationTask.DefaultTimeout();
        Assert.Null(configuration.Exception);
        Assert.Equal("final-value", configuration.EnvironmentVariables.Single(e => e.Key == "VALUE").Value);

        static InteractionInputCollection CreateSetParameterArguments(string value)
        {
            return new([
                new InteractionInput
                {
                    Name = ParameterProcessor.SetParameterValueName,
                    InputType = InputType.Text,
                    Value = value
                },
                new InteractionInput
                {
                    Name = ParameterProcessor.SaveToUserSecretsName,
                    InputType = InputType.Boolean,
                    Value = "false"
                }
            ]);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ParameterDiscovery_WithThrowingConnectionMetadata_PreservesReferencesAndOtherResources(
        bool throwOnConnectionStringExpression,
        bool throwOnConnectionProperties)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var propertyParameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "property", secret: false);
        var resource = builder.AddResource(new TestResourceWithConnectionString(
            "resource", "connection-string", unresolvedProperty: ReferenceExpression.Create($"{propertyParameter}"))
        {
            ThrowOnConnectionStringExpression = throwOnConnectionStringExpression,
            ThrowOnConnectionProperties = throwOnConnectionProperties
        });
        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var container = builder.AddContainer("api", "test-image").WithEnvironment("VALUE", parameter);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var events = new DcpExecutorEvents();
        var notifications = ResourceNotificationServiceTestHelpers.Create();
        var orchestrator = CreateOrchestrator(model, notifications, out var parameterProcessor, dcpEvents: events);
        await orchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None)).DefaultTimeout();

        Assert.NotNull(propertyParameter.WaitForValueTcs);
        Assert.False(propertyParameter.WaitForValueTcs.Task.IsCompleted);
        Assert.True(notifications.TryGetCurrentState(resource.Resource.Name, out _));
        Assert.True(notifications.TryGetCurrentState(container.Resource.Name, out _));

        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Executable, resource.Resource, "resource-dcp")).DefaultTimeout();
        Assert.True(notifications.TryGetCurrentState("resource-dcp", out var resourceEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, resourceEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["property"],
            Assert.IsType<string[]>(Assert.Single(resourceEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        await events.PublishAsync(new OnResourceStartingContext(
            CancellationToken.None, KnownResourceTypes.Container, container.Resource, container.Resource.Name)).DefaultTimeout();
        await parameterProcessor.SetParameterCoreAsync(parameter.Resource, new InteractionInputCollection([
            new InteractionInput { Name = ParameterProcessor.SetParameterValueName, InputType = InputType.Text, Value = "resolved-value" },
            new InteractionInput { Name = ParameterProcessor.SaveToUserSecretsName, InputType = InputType.Boolean, Value = "false" }
        ]), CancellationToken.None).DefaultTimeout();
        await parameterProcessor.DeleteParameterCoreAsync(parameter.Resource, new InteractionInputCollection([]), CancellationToken.None).DefaultTimeout();

        Assert.True(notifications.TryGetCurrentState(container.Resource.Name, out var containerEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, containerEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(containerEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        await propertyParameter.SetValueAsync("resolved-property", CancellationToken.None);
        await parameter.Resource.SetValueAsync("resolved-value", CancellationToken.None);
    }

    [Fact]
    public async Task OnResourceStarting_WithReferencedOnlyUnresolvedParameter_PublishesParameterName()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = ParameterResourceBuilderExtensions.CreateParameter(builder, "value", secret: false);
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.DoesNotContain(parameter, distributedAppModel.Resources);
        Assert.Contains(
            container.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => relationship.Type == KnownRelationshipTypes.Reference && ReferenceEquals(relationship.Resource, parameter));

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));
        Assert.NotNull(parameter.WaitForValueTcs);

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));
    }

    [Fact]
    public async Task OnResourceStarting_AfterParameterValueDeleted_PublishesParameterName()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => "initial-value");
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(
            distributedAppModel,
            resourceNotificationService,
            out var parameterProcessor,
            dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await parameterProcessor.DeleteParameterCoreAsync(parameter.Resource, new InteractionInputCollection([]), CancellationToken.None);
        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["value"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));
    }

    [Fact]
    public async Task OnResourceStarting_WithParameterInTransitiveDependency_RemainsStarting()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var backend = builder.AddContainer("backend", "test-image")
            .WithHttpEndpoint(targetPort: 8080, name: "http")
            .WithEnvironment("VALUE", parameter);
        var frontend = builder.AddContainer("frontend", "test-image")
            .WithEnvironment("BACKEND_SCHEME", backend.GetEndpoint("http").Property(EndpointProperty.Scheme));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        var configuration = await ExecutionConfigurationBuilder.Create(frontend.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), NullLogger.Instance)
            .DefaultTimeout();
        Assert.Null(configuration.Exception);
        Assert.Equal("http", configuration.EnvironmentVariables.Single(e => e.Key == "BACKEND_SCHEME").Value);

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, frontend.Resource, "frontend-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("frontend-dcp", out var startingEvent));
        Assert.Equal(KnownResourceStates.Starting, startingEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(startingEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters);
    }

    [Fact]
    public async Task OnResourceStarting_WithUnrelatedUnresolvedParameter_RemainsStarting()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        _ = builder.AddParameter("unrelated", () => throw new MissingParameterValueException("Missing unrelated parameter."));
        var container = builder.AddContainer("api", "test-image");

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, container.Resource, "api-dcp"));

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var startingEvent));
        Assert.Equal(KnownResourceStates.Starting, startingEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(startingEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MonitorUnresolvedParameters_WhenValueTaskIsReplaced_DoesNotOverwriteNewerBlockers(
        bool replaceFirstParameter, bool keepThirdParameterUnresolved)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var first = builder.AddParameter("first", () => throw new MissingParameterValueException("Missing first parameter."));
        var second = builder.AddParameter("second", () => "second-value");
        var third = builder.AddParameter("third", () => keepThirdParameterUnresolved
            ? throw new MissingParameterValueException("Missing third parameter.")
            : "third-value");
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("FIRST", first)
            .WithEnvironment("SECOND", second)
            .WithEnvironment("THIRD", third);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(
            distributedAppModel, resourceNotificationService, out var parameterProcessor, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(TestContext.Current.CancellationToken));
        await events.PublishAsync(new OnResourceStartingContext(
            TestContext.Current.CancellationToken, KnownResourceTypes.Container, container.Resource, container.Resource.Name));

        var previousValueTasks = new Dictionary<ParameterResource, Task<string?>?>
        {
            [first.Resource] = first.Resource.WaitForValueTcs!.Task,
            [second.Resource] = second.Resource.WaitForValueTcs!.Task,
            [third.Resource] = third.Resource.WaitForValueTcs!.Task
        };
        await first.Resource.SetValueAsync("first-value", TestContext.Current.CancellationToken).DefaultTimeout();
        var replacementParameter = replaceFirstParameter ? first.Resource : second.Resource;
        var deleteResult = await parameterProcessor.DeleteParameterCoreAsync(
            replacementParameter, new InteractionInputCollection([]), TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.True(deleteResult.Success);
        Assert.False(replacementParameter.WaitForValueTcs!.Task.IsCompleted);

        Assert.True(resourceNotificationService.TryGetCurrentState(container.Resource.Name, out var replacementEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, replacementEvent.Snapshot.State?.Text);
        var expectedParameters = keepThirdParameterUnresolved
            ? new[] { replacementParameter.Name, third.Resource.Name }
            : [replacementParameter.Name];
        Assert.Equal(
            expectedParameters,
            Assert.IsType<string[]>(Assert.Single(replacementEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        // Execute the old value-task continuation only after the replacement generation has published.
        // The third parameter covers both an incomplete old list and an old list with no blockers left.
        var staleMonitorTask = appOrchestrator.MonitorUnresolvedParametersAsync(
            container.Resource, container.Resource.Name, previousValueTasks, TestContext.Current.CancellationToken);
        Assert.True(resourceNotificationService.TryGetCurrentState(container.Resource.Name, out var currentEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, currentEvent.Snapshot.State?.Text);
        Assert.Equal(replacementEvent.Snapshot.Version, currentEvent.Snapshot.Version);
        Assert.Equal(
            expectedParameters,
            Assert.IsType<string[]>(Assert.Single(currentEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        var startingTask = resourceNotificationService.WaitForResourceAsync(
            container.Resource.Name, KnownResourceStates.Starting, TestContext.Current.CancellationToken);
        await replacementParameter.SetValueAsync("replacement-value", TestContext.Current.CancellationToken).DefaultTimeout();
        if (keepThirdParameterUnresolved)
        {
            await third.Resource.SetValueAsync("third-value", TestContext.Current.CancellationToken).DefaultTimeout();
        }

        await staleMonitorTask.DefaultTimeout();
        await startingTask.DefaultTimeout();
    }

    [Fact]
    public async Task MonitorUnresolvedParameters_WhenResolvedValueIsReassigned_StillClearsOtherBlockers()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var first = builder.AddParameter("first", () => throw new MissingParameterValueException("Missing first parameter."));
        var second = builder.AddParameter("second", () => throw new MissingParameterValueException("Missing second parameter."));
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("FIRST", first)
            .WithEnvironment("SECOND", second);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var appOrchestrator = CreateOrchestrator(distributedAppModel, resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(TestContext.Current.CancellationToken));
        await events.PublishAsync(new OnResourceStartingContext(
            TestContext.Current.CancellationToken, KnownResourceTypes.Container, container.Resource, container.Resource.Name));

        var previousValueTasks = new Dictionary<ParameterResource, Task<string?>?>
        {
            [first.Resource] = first.Resource.WaitForValueTcs!.Task,
            [second.Resource] = second.Resource.WaitForValueTcs!.Task
        };
        await first.Resource.SetValueAsync("first-value", TestContext.Current.CancellationToken).DefaultTimeout();
        await first.Resource.SetValueAsync("updated-value", TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.NotSame(previousValueTasks[first.Resource], first.Resource.WaitForValueTcs!.Task);

        var monitorTask = appOrchestrator.MonitorUnresolvedParametersAsync(
            container.Resource, container.Resource.Name, previousValueTasks, TestContext.Current.CancellationToken);
        Assert.True(resourceNotificationService.TryGetCurrentState(container.Resource.Name, out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);
        Assert.Equal(
            ["second"],
            Assert.IsType<string[]>(Assert.Single(unresolvedEvent.Snapshot.Properties, p => p.Name == KnownProperties.Resource.UnresolvedParameters).Value));

        await second.Resource.SetValueAsync("second-value", TestContext.Current.CancellationToken).DefaultTimeout();
        await monitorTask.DefaultTimeout();
        Assert.True(resourceNotificationService.TryGetCurrentState(container.Resource.Name, out var startingEvent));
        Assert.Equal(KnownResourceStates.Starting, startingEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task MonitorUnresolvedParameters_WhenResourceLeavesUnresolvedParameters_DoesNotRegressState()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parameter = builder.AddParameter("value", () => throw new MissingParameterValueException("Missing parameter."));
        var container = builder.AddContainer("api", "test-image")
            .WithEnvironment("VALUE", parameter);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));
        await resourceNotificationService.PublishUpdateAsync(container.Resource, "api-dcp", s => s with
        {
            State = new ResourceStateSnapshot(KnownResourceStates.UnresolvedParameters, KnownResourceStateStyles.Warn),
            Properties = s.Properties.SetResourceProperty(KnownProperties.Resource.UnresolvedParameters, new[] { parameter.Resource.Name })
        }).DefaultTimeout();
        var monitorTask = appOrchestrator.MonitorUnresolvedParametersAsync(
            container.Resource, "api-dcp",
            new Dictionary<ParameterResource, Task<string?>?> { [parameter.Resource] = parameter.Resource.WaitForValueTcs!.Task },
            TestContext.Current.CancellationToken);

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var unresolvedEvent));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, unresolvedEvent.Snapshot.State?.Text);

        await resourceNotificationService.PublishUpdateAsync(container.Resource, "api-dcp", s => s with
        {
            State = KnownResourceStates.Running
        }).DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var runningEvent));
        Assert.True(parameter.Resource.WaitForValueTcs!.TrySetResult("resolved-value"));
        await monitorTask.DefaultTimeout();

        Assert.True(resourceNotificationService.TryGetCurrentState("api-dcp", out var monitorUpdate));
        Assert.Equal(KnownResourceStates.Running, monitorUpdate.Snapshot.State?.Text);
        Assert.Equal(runningEvent.Snapshot.Version, monitorUpdate.Snapshot.Version);
        Assert.All(monitorUpdate.Snapshot.Properties, p => Assert.NotEqual(KnownProperties.Resource.UnresolvedParameters, p.Name));
    }

    private ApplicationOrchestrator CreateOrchestrator(
        DistributedApplicationModel distributedAppModel,
        ResourceNotificationService notificationService,
        DcpExecutorEvents? dcpEvents = null,
        IDistributedApplicationEventing? applicationEventing = null,
        ResourceLoggerService? resourceLoggerService = null,
        DashboardOptions? dashboardOptions = null,
        TestDcpExecutor? dcpExecutor = null)
    {
        return CreateOrchestrator(
            distributedAppModel,
            notificationService,
            out _,
            dcpEvents,
            applicationEventing,
            resourceLoggerService,
            dashboardOptions,
            dcpExecutor);
    }

    private ApplicationOrchestrator CreateOrchestrator(
        DistributedApplicationModel distributedAppModel,
        ResourceNotificationService notificationService,
        out ParameterProcessor parameterProcessor,
        DcpExecutorEvents? dcpEvents = null,
        IDistributedApplicationEventing? applicationEventing = null,
        ResourceLoggerService? resourceLoggerService = null,
        DashboardOptions? dashboardOptions = null,
        TestDcpExecutor? dcpExecutor = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        services.AddTestAndResourceLogging(testOutputHelper, configuration);
        var serviceProvider = services.BuildServiceProvider();
        resourceLoggerService ??= new ResourceLoggerService();

        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { Services = serviceProvider });

        parameterProcessor = new ParameterProcessor(
            notificationService,
            resourceLoggerService,
            CreateInteractionService(),
            serviceProvider.GetRequiredService<ILogger<ParameterProcessor>>(),
            executionContext,
            deploymentStateManager: new MockDeploymentStateManager(),
            userSecretsManager: UserSecrets.NoopUserSecretsManager.Instance);

        return new ApplicationOrchestrator(
            distributedAppModel,
            dcpExecutor ?? new TestDcpExecutor(),
            dcpEvents ?? new DcpExecutorEvents(),
            [],
            notificationService,
            resourceLoggerService,
            applicationEventing ?? new DistributedApplicationEventing(),
            serviceProvider,
            executionContext,
            parameterProcessor,
            Options.Create(dashboardOptions ?? new()),
            serviceProvider.GetRequiredService<ILogger<ApplicationOrchestrator>>()
        );
    }

    private static InteractionService CreateInteractionService(DistributedApplicationOptions? options = null)
    {
        return new InteractionService(
            NullLogger<InteractionService>.Instance,
            options ?? new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new TestInteractionFileUploadStore());
    }

    private sealed class MockDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
        }

        public Task<DeploymentStateSection> AcquireCurrentSectionAsync(string sectionName, CancellationToken cancellationToken = default)
            => AcquireSectionAsync(sectionName, cancellationToken);

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAllStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CustomResource(string name) : Resource(name);

    private sealed class CustomResourceWithWaitSupport(string name) : Resource(name), IResourceWithWaitSupport, IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{Name}-connection-string");
    }

    private sealed class CustomChildResource(string name, IResource parent) : Resource(name), IResourceWithParent
    {
        public IResource Parent => parent;
    }

    private sealed class ProjectA : IProjectMetadata
    {
        public string ProjectPath => "projectA";

        public LaunchSettings LaunchSettings { get; } = new();
    }

    private sealed class ProjectB : IProjectMetadata
    {
        public string ProjectPath => "projectB";
        public LaunchSettings LaunchSettings { get; } = new();
    }

    private abstract class ResourceWithConnectionString(string name)
        : Resource(name), IResourceWithConnectionString
    {
        protected abstract ReferenceExpression ConnectionString { get; }

        public ReferenceExpression ConnectionStringExpression
        {
            get
            {
                if (this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var connectionStringAnnotation))
                {
                    return connectionStringAnnotation.Resource.ConnectionStringExpression;
                }

                return ConnectionString;
            }
        }
    }

    private sealed class ParentResourceWithConnectionString(string name) : ResourceWithConnectionString(name)
    {
        protected override ReferenceExpression ConnectionString =>
            ReferenceExpression.Create($"Server=localhost:8000");
    }

    private sealed class ChildResourceWithConnectionString(
        string name,
        Dictionary<string, string> kvConnectionString,
        IResourceWithConnectionString parent
    )
        : ResourceWithConnectionString(name), IResourceWithParent
    {
        private string SubConnectionString =>
            string.Join(';', kvConnectionString.Select(kv => $"{kv.Key}={kv.Value}"));

        protected override ReferenceExpression ConnectionString =>
            ReferenceExpression.Create($"{parent};{SubConnectionString}");

        public IResource Parent { get; } = parent;
    }

    private sealed class TestResourceWithConnectionString(string name, string connectionString, string? databaseName = null, string? host = null, ReferenceExpression? unresolvedProperty = null)
        : Resource(name), IResourceWithConnectionString
    {
        public bool ThrowOnConnectionStringExpression { get; init; }

        public bool ThrowOnConnectionProperties { get; init; }

        public ReferenceExpression ConnectionStringExpression => ThrowOnConnectionStringExpression
            ? throw new InvalidOperationException("Invalid connection string configuration.")
            : ReferenceExpression.Create($"{connectionString}");

        public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(connectionString);
        }

        public IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties()
        {
            if (databaseName is not null)
            {
                yield return new("DatabaseName", ReferenceExpression.Create($"{databaseName}"));
            }

            if (host is not null)
            {
                yield return new("Host", ReferenceExpression.Create($"{host}"));
            }

            if (unresolvedProperty is not null)
            {
                yield return new("Unavailable", unresolvedProperty);
            }

            if (ThrowOnConnectionProperties)
            {
                throw new InvalidOperationException("Invalid connection property configuration.");
            }
        }
    }

    private sealed class ParameterConnectionStringResource(string name, ParameterResource parameter)
        : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"Value={parameter}");
    }

    [Fact]
    public async Task ContainerChildResourcesWithOwnLifetimeDoNotReceiveParentStateChanges()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentContainer = builder.AddContainer("parent-container", "parent-image");
        var childContainer = builder.AddContainer("child-container", "child-image")
            .WithParentRelationship(parentContainer);
        var customChild = builder.AddResource(new CustomChildResource("custom-child", parentContainer.Resource));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        // Initialize resources
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        // Simulate parent container state change
        await events.PublishAsync(new OnResourceChangedContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            parentContainer.Resource,
            "parent-container-dcp",
            new ResourceStatus(KnownResourceStates.FailedToStart, null, null),
            PreviousState: null,
            snapshot => snapshot with { State = KnownResourceStates.FailedToStart }));

        // Check final states
        var parentState = resourceNotificationService.TryGetCurrentState("parent-container-dcp", out var parentEvent) ? parentEvent.Snapshot.State?.Text : null;
        var childContainerState = resourceNotificationService.TryGetCurrentState(childContainer.Resource.Name, out var childContainerEvent) ? childContainerEvent.Snapshot.State?.Text : null;
        var customChildState = resourceNotificationService.TryGetCurrentState(customChild.Resource.Name, out var customChildEvent) ? customChildEvent.Snapshot.State?.Text : null;

        // Parent should have the new state
        Assert.Equal(KnownResourceStates.FailedToStart, parentState);

        // Child container (has own lifetime) should NOT receive parent state
        Assert.NotEqual(KnownResourceStates.Running, childContainerState);

        // Custom child (does not have own lifetime) SHOULD receive parent state
        Assert.Equal(KnownResourceStates.FailedToStart, customChildState);
    }

    [Fact]
    public async Task ProjectChildResourcesWithOwnLifetimeDoNotReceiveParentStateChanges()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentContainer = builder.AddContainer("parent-container", "parent-image");
        var childProject = builder.AddProject<ProjectA>("child-project")
            .WithParentRelationship(parentContainer);
        var customChild = builder.AddResource(new CustomChildResource("custom-child", parentContainer.Resource));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        // Initialize resources
        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        // Simulate parent container state change
        await events.PublishAsync(new OnResourceChangedContext(
            CancellationToken.None,
            KnownResourceTypes.Container,
            parentContainer.Resource,
            "parent-container-dcp",
            new ResourceStatus(KnownResourceStates.FailedToStart, null, null),
            PreviousState: null,
            snapshot => snapshot with { State = KnownResourceStates.FailedToStart }));

        // Check final states
        var parentState = resourceNotificationService.TryGetCurrentState("parent-container-dcp", out var parentEvent) ? parentEvent.Snapshot.State?.Text : null;
        var childProjectState = resourceNotificationService.TryGetCurrentState(childProject.Resource.Name, out var childProjectEvent) ? childProjectEvent.Snapshot.State?.Text : null;
        var customChildState = resourceNotificationService.TryGetCurrentState(customChild.Resource.Name, out var customChildEvent) ? customChildEvent.Snapshot.State?.Text : null;

        // Parent should have the new state
        Assert.Equal(KnownResourceStates.FailedToStart, parentState);

        // Child project (has own lifetime) should NOT receive parent state
        Assert.NotEqual(KnownResourceStates.Running, childProjectState);

        // Custom child (does not have own lifetime) SHOULD receive parent state
        Assert.Equal(KnownResourceStates.FailedToStart, customChildState);
    }

    [Fact]
    public async Task WithChildRelationshipUsingResourceBuilderSetsParentPropertyCorrectly()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parent = builder.AddContainer("parent", "image");
        var child = builder.AddContainer("child", "image");
        var child2 = builder.AddContainer("child2", "image");

        parent.WithChildRelationship(child)
              .WithChildRelationship(child2);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentResourceId = null;
        string? childParentResourceId = null;
        string? child2ParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parent.Resource)
                {
                    parentResourceId = item.ResourceId;
                }
                else if (item.Resource == child.Resource)
                {
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }
                else if (item.Resource == child2.Resource)
                {
                    child2ParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentResourceId != null && childParentResourceId != null && child2ParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentResourceId, childParentResourceId);
        Assert.Equal(parentResourceId, child2ParentResourceId);
    }

    [Fact]
    public async Task WithChildRelationshipUsingResourceSetsParentPropertyCorrectly()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parent = builder.AddContainer("parent", "image");
        var child = builder.AddContainer("child", "image");
        var child2 = builder.AddContainer("child2", "image");

        parent.WithChildRelationship(child.Resource)
              .WithChildRelationship(child2.Resource);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentResourceId = null;
        string? childParentResourceId = null;
        string? child2ParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parent.Resource)
                {
                    parentResourceId = item.ResourceId;
                }
                else if (item.Resource == child.Resource)
                {
                    childParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }
                else if (item.Resource == child2.Resource)
                {
                    child2ParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentResourceId != null && childParentResourceId != null && child2ParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentResourceId, childParentResourceId);
        Assert.Equal(parentResourceId, child2ParentResourceId);
    }

    [Fact]
    public async Task WithChildRelationshipWorksWithProjects()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var parentProject = builder.AddProject<ProjectA>("parent-project");
        var childProject = builder.AddProject<ProjectB>("child-project");

        parentProject.WithChildRelationship(childProject);

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events);
        await appOrchestrator.RunApplicationAsync();

        string? parentProjectResourceId = null;
        string? childProjectParentResourceId = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == parentProject.Resource)
                {
                    parentProjectResourceId = item.ResourceId;
                }
                else if (item.Resource == childProject.Resource)
                {
                    childProjectParentResourceId = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                }

                if (parentProjectResourceId != null && childProjectParentResourceId != null)
                {
                    return;
                }
            }
        });

        await events.PublishAsync(new OnResourcesPreparedContext(CancellationToken.None));

        await watchResourceTask.DefaultTimeout();

        Assert.Equal(parentProjectResourceId, childProjectParentResourceId);
    }

    private sealed class ThrowingValueProvider : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => "{throwing.value}";

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The connection property isn't available.");
        }
    }
}
