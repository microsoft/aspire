// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002

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

        await events.PublishAsync(new OnResourceStartingContext(CancellationToken.None, KnownResourceTypes.Container, parentResource.Resource, parentResource.Resource.Name));

        Assert.True(parentConnectionStringAvailable);
        Assert.True(childConnectionStringAvailable);
        Assert.True(grandChildConnectionStringAvailable);
    }

    [Fact]
    public async Task ConnectionStringAvailableEventPublishesUpdateWithConnectionStringValue()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.WithTestAndResourceLogging(testOutputHelper);

        var resource = builder.AddResource(new TestResourceWithConnectionString("test-resource", "Server=localhost:5432;Database=testdb"));

        using var app = builder.Build();
        var distributedAppModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var events = new DcpExecutorEvents();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var applicationEventing = new DistributedApplicationEventing();

        var appOrchestrator = CreateOrchestrator(distributedAppModel, notificationService: resourceNotificationService, dcpEvents: events, applicationEventing: applicationEventing);
        await appOrchestrator.RunApplicationAsync();

        string? connectionStringProperty = null;
        bool? isSensitive = null;
        var watchResourceTask = Task.Run(async () =>
        {
            await foreach (var item in resourceNotificationService.WatchAsync())
            {
                if (item.Resource == resource.Resource)
                {
                    var connectionStringProp = item.Snapshot.Properties.SingleOrDefault(p => p.Name == KnownProperties.Resource.ConnectionString);
                    if (connectionStringProp is not null)
                    {
                        connectionStringProperty = connectionStringProp.Value?.ToString();
                        isSensitive = connectionStringProp.IsSensitive;
                        return;
                    }
                }
            }
        });

        // Publish the ConnectionStringAvailableEvent to trigger the update
        await applicationEventing.PublishAsync(new ConnectionStringAvailableEvent(resource.Resource, app.Services), CancellationToken.None);

        await watchResourceTask.DefaultTimeout();

        Assert.Equal("Server=localhost:5432;Database=testdb", connectionStringProperty);
        Assert.True(isSensitive);
    }

    private ApplicationOrchestrator CreateOrchestrator(
        DistributedApplicationModel distributedAppModel,
        ResourceNotificationService notificationService,
        DcpExecutorEvents? dcpEvents = null,
        IDistributedApplicationEventing? applicationEventing = null,
        ResourceLoggerService? resourceLoggerService = null,
        DashboardOptions? dashboardOptions = null)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationManager();
        services.AddTestAndResourceLogging(testOutputHelper, configuration);
        var serviceProvider = services.BuildServiceProvider();
        resourceLoggerService ??= new ResourceLoggerService();

        var executionContext = new DistributedApplicationExecutionContext(
            new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run) { ServiceProvider = serviceProvider });

        return new ApplicationOrchestrator(
            distributedAppModel,
            new TestDcpExecutor(),
            dcpEvents ?? new DcpExecutorEvents(),
            [],
            notificationService,
            resourceLoggerService,
            applicationEventing ?? new DistributedApplicationEventing(),
            serviceProvider,
            executionContext,
            new ParameterProcessor(
                notificationService,
                resourceLoggerService,
                CreateInteractionService(),
                serviceProvider.GetRequiredService<ILogger<ParameterProcessor>>(),
                executionContext,
                deploymentStateManager: new MockDeploymentStateManager(),
                userSecretsManager: UserSecrets.NoopUserSecretsManager.Instance),
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
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
    }

    private sealed class MockDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class CustomResource(string name) : Resource(name);

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

    private sealed class TestResourceWithConnectionString(string name, string connectionString)
        : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{connectionString}");

        public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<string?>(connectionString);
        }
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
}
