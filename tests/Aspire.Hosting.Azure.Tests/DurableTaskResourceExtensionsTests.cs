// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.DurableTask;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class DurableTaskResourceExtensionsTests
{
    [Fact]
    public async Task AddDurableTaskScheduler_RunAsEmulator_ResolvedConnectionString()
    {
        string expectedConnectionString = "Endpoint=http://localhost:8080;Authentication=None";

        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder
            .AddDurableTaskScheduler("dts")
            .RunAsEmulator(e =>
            {
                e.WithEndpoint("grpc", e => e.AllocatedEndpoint = new(e, "localhost", 8080));
                e.WithEndpoint("http", e => e.AllocatedEndpoint = new(e, "localhost", 8081));
                e.WithEndpoint("dashboard", e => e.AllocatedEndpoint = new(e, "localhost", 8082));
            });

        var connectionString = await dts.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(expectedConnectionString, connectionString);
    }

    [Fact]
    public async Task AddDurableTaskScheduler_RunAsExisting_ResolvedConnectionString()
    {
        string expectedConnectionString = "Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure";

        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder
            .AddDurableTaskScheduler("dts")
            .RunAsExisting(expectedConnectionString);

        var connectionString = await dts.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(expectedConnectionString, connectionString);
    }

    [Fact]
    public async Task AddDurableTaskScheduler_RunAsExisting_ResolvedConnectionStringParameter()
    {
        string expectedConnectionString = "Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure";

        using var builder = TestDistributedApplicationBuilder.Create();

        var connectionStringParameter = builder.AddParameter("dts-connection-string", expectedConnectionString);

        var dts = builder
            .AddDurableTaskScheduler("dts")
            .RunAsExisting(connectionStringParameter);

        var connectionString = await dts.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(expectedConnectionString, connectionString);
    }

    [Theory]
    [InlineData(null, "mytaskhub")]
    [InlineData("myrealtaskhub", "myrealtaskhub")]
    public async Task AddDurableTaskHub_RunAsExisting_ResolvedConnectionStringParameter(string? taskHubName, string expectedTaskHubName)
    {
        string dtsConnectionString = "Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure";
        string expectedConnectionString = $"{dtsConnectionString};TaskHub={expectedTaskHubName}";
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder
            .AddDurableTaskScheduler("dts")
            .RunAsExisting(dtsConnectionString);

        var taskHub = dts.AddTaskHub("mytaskhub");

        if (taskHubName is not null)
        {
            taskHub = taskHub.WithTaskHubName(taskHubName);
        }

        var connectionString = await taskHub.Resource.ConnectionStringExpression.GetValueAsync(default);

        Assert.Equal(expectedConnectionString, connectionString);
    }

    [Fact]
    public void AddDurableTaskScheduler_HasManifestPublishingCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");

        Assert.True(dts.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var manifestAnnotations));
        var annotation = Assert.Single(manifestAnnotations);
        Assert.NotNull(annotation.Callback);
    }

    [Fact]
    public void AddDurableTaskHub_HasManifestPublishingCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts").RunAsExisting("Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure");
        var taskHub = dts.AddTaskHub("hub");

        Assert.True(taskHub.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var manifestAnnotations));
        var annotation = Assert.Single(manifestAnnotations);
        Assert.NotNull(annotation.Callback);
    }

    [Fact]
    public void RunAsExisting_InPublishMode_DoesNotApplyConnectionStringAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsExisting("Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure");

        Assert.False(dts.ApplicationBuilder.ExecutionContext.IsRunMode);
        Assert.True(dts.ApplicationBuilder.ExecutionContext.IsPublishMode);

        // In publish mode, RunAsExisting does not apply the connection string annotation;
        // instead, the connection string falls through to the provisioned-mode Bicep output reference.
        var connectionString = dts.Resource.ConnectionStringExpression;
        Assert.Contains("Endpoint=", connectionString.Format);
        Assert.Contains("Authentication=DefaultAzure", connectionString.Format);
    }

    [Fact]
    public void RunAsEmulator_InPublishMode_IsNoOp()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsEmulator();

        Assert.False(dts.Resource.IsEmulator);
        Assert.DoesNotContain(dts.Resource.Annotations, a => a is EmulatorResourceAnnotation);

        // In publish mode, the emulator is a no-op, but the connection string resolves
        // to the provisioned-mode Bicep output reference instead of throwing.
        var connectionString = dts.Resource.ConnectionStringExpression;
        Assert.Contains("Endpoint=", connectionString.Format);
        Assert.Contains("Authentication=DefaultAzure", connectionString.Format);
    }

    [Fact]
    public void RunAsEmulator_AddsEmulatorAnnotationContainerImageAndEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsEmulator();

        Assert.True(dts.Resource.IsEmulator);

        var emulatorAnnotation = dts.Resource.Annotations.OfType<EmulatorResourceAnnotation>().SingleOrDefault();
        Assert.NotNull(emulatorAnnotation);

        var containerImageAnnotation = dts.Resource.Annotations.OfType<ContainerImageAnnotation>().SingleOrDefault();
        Assert.NotNull(containerImageAnnotation);
        Assert.Equal("mcr.microsoft.com", containerImageAnnotation.Registry);
        Assert.Equal("dts/dts-emulator", containerImageAnnotation.Image);
        Assert.Equal("latest", containerImageAnnotation.Tag);

        var endpointAnnotations = dts.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

        var grpc = endpointAnnotations.SingleOrDefault(e => e.Name == "grpc");
        Assert.NotNull(grpc);
        Assert.Equal(8080, grpc.TargetPort);

        var http = endpointAnnotations.SingleOrDefault(e => e.Name == "http");
        Assert.NotNull(http);
        Assert.Equal(8081, http.TargetPort);
        Assert.Equal("http", http.UriScheme);

        var dashboard = endpointAnnotations.SingleOrDefault(e => e.Name == "dashboard");
        Assert.NotNull(dashboard);
        Assert.Equal(8082, dashboard.TargetPort);
        Assert.Equal("http", dashboard.UriScheme);
    }

    [Fact]
    public async Task RunAsEmulator_SetsSingleDtsTaskHubNamesEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts").RunAsEmulator();

        _ = dts.AddTaskHub("hub1").WithTaskHubName("realhub1");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dts.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("realhub1", env["DTS_TASK_HUB_NAMES"]);
    }

    [Fact]
    public async Task RunAsEmulator_SetsMultipleDtsTaskHubNamesEnvironmentVariable()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts").RunAsEmulator();

        _ = dts.AddTaskHub("hub1");
        _ = dts.AddTaskHub("hub2").WithTaskHubName("realhub2");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dts.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("hub1, realhub2", env["DTS_TASK_HUB_NAMES"]);
    }

    [Fact]
    public async Task RunAsEmulator_DtsTaskHubNamesOnlyIncludesHubsForSameScheduler()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts1 = builder.AddDurableTaskScheduler("dts1").RunAsEmulator();
        var dts2 = builder.AddDurableTaskScheduler("dts2").RunAsEmulator();

        _ = dts1.AddTaskHub("hub1");
        _ = dts2.AddTaskHub("hub2");

        var env1 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dts1.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var env2 = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(dts2.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("hub1", env1["DTS_TASK_HUB_NAMES"]);
        Assert.Equal("hub2", env2["DTS_TASK_HUB_NAMES"]);
    }

    [Fact]
    public async Task WithTaskHubName_Parameter_ResolvedConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        const string dtsConnectionString = "Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure";
        var hubNameParameter = builder.AddParameter("hub-name", "parameterHub");

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsExisting(dtsConnectionString);

        var hub = dts.AddTaskHub("ignored").WithTaskHubName(hubNameParameter);

        var connectionString = await hub.Resource.ConnectionStringExpression.GetValueAsync(default);
        Assert.Equal($"{dtsConnectionString};TaskHub=parameterHub", connectionString);
    }

    [Fact]
    public void DurableTaskSchedulerResource_WithoutEmulatorOrExistingConnectionString_ResolvesToBicepOutput()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");

        // Without emulator or RunAsExisting, connection string resolves to the
        // provisioned-mode Bicep output reference.
        var connectionString = dts.Resource.ConnectionStringExpression;
        Assert.Contains("Endpoint=", connectionString.Format);
        Assert.Contains("Authentication=DefaultAzure", connectionString.Format);
    }

    [Fact]
    public void AddDurableTaskScheduler_HasDefaultRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");

        Assert.True(dts.Resource.TryGetAnnotationsOfType<DefaultRoleAssignmentsAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(DurableTaskSchedulerBuiltInRole.DurableTaskDataContributor.ToString(), role.Id);
    }

    [Fact]
    public void AddTaskHub_HasDefaultRoleAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var hub = dts.AddTaskHub("myhub");

        Assert.True(hub.Resource.TryGetAnnotationsOfType<DefaultRoleAssignmentsAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(DurableTaskSchedulerBuiltInRole.DurableTaskDataContributor.ToString(), role.Id);
    }

    [Fact]
    public void WithRoleAssignments_Scheduler_AppliesRoleAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var project = builder.AddResource(new TestResource("myapp"))
            .WithRoleAssignments(dts, DurableTaskSchedulerBuiltInRole.DurableTaskWorker);

        Assert.True(project.Resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        Assert.Equal(dts.Resource, annotation.Target);
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(DurableTaskSchedulerBuiltInRole.DurableTaskWorker.ToString(), role.Id);
    }

    [Fact]
    public void WithRoleAssignments_TaskHub_AppliesRoleAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var hub = dts.AddTaskHub("myhub");
        var project = builder.AddResource(new TestResource("myapp"))
            .WithRoleAssignments(hub, DurableTaskSchedulerBuiltInRole.DurableTaskDataReader);

        Assert.True(project.Resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        Assert.Equal(hub.Resource, annotation.Target);
        var role = Assert.Single(annotation.Roles);
        Assert.Equal(DurableTaskSchedulerBuiltInRole.DurableTaskDataReader.ToString(), role.Id);
    }

    [Fact]
    public void WithRoleAssignments_Scheduler_MultipleRoles()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var project = builder.AddResource(new TestResource("myapp"))
            .WithRoleAssignments(dts,
                DurableTaskSchedulerBuiltInRole.DurableTaskDataReader,
                DurableTaskSchedulerBuiltInRole.DurableTaskWorker);

        Assert.True(project.Resource.TryGetAnnotationsOfType<RoleAssignmentAnnotation>(out var annotations));
        var annotation = Assert.Single(annotations);
        Assert.Equal(2, annotation.Roles.Count);
        Assert.Contains(annotation.Roles, r => r.Id == DurableTaskSchedulerBuiltInRole.DurableTaskDataReader.ToString());
        Assert.Contains(annotation.Roles, r => r.Id == DurableTaskSchedulerBuiltInRole.DurableTaskWorker.ToString());
    }

    [Fact]
    public async Task AddDurableTaskScheduler_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddDurableTaskScheduler_WithTaskHub_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts");
        _ = dts.AddTaskHub("myhub");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddDurableTaskScheduler_WithNamedTaskHub_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts");
        _ = dts.AddTaskHub("myhub").WithTaskHubName("MyCustomTaskHub");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddDurableTaskScheduler_WithMultipleTaskHubs_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts");
        _ = dts.AddTaskHub("hub1");
        _ = dts.AddTaskHub("hub2").WithTaskHubName("CustomHub2");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddDurableTaskScheduler_PublishAsExisting_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingName = builder.AddParameter("existingSchedulerName");
        var dts = builder.AddDurableTaskScheduler("dts")
            .PublishAsExisting(existingName, resourceGroupParameter: default);
        _ = dts.AddTaskHub("myhub");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddDurableTaskScheduler_PublishAsExisting_WithResourceGroup_GeneratesBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var existingName = builder.AddParameter("existingSchedulerName");
        var existingRg = builder.AddParameter("existingResourceGroup");
        var dts = builder.AddDurableTaskScheduler("dts")
            .PublishAsExisting(existingName, existingRg);
        _ = dts.AddTaskHub("myhub");

        var manifest = await AzureManifestUtils.GetManifestWithBicep(dts.Resource);

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task DashboardUrlService_AzureMode_AddsDashboardUrlAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var hub = dts.AddTaskHub("myhub");

        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var appModel = new DistributedApplicationModel([dts.Resource, hub.Resource]);
        var service = new DurableTaskDashboardUrlService(appModel, notificationService);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Set Azure outputs on the scheduler
        dts.Resource.Outputs["subscriptionId"] = "sub-123";
        dts.Resource.Outputs["schedulerEndpoint"] = "https://myscheduler.durabletask.io";
        dts.Resource.Outputs["name"] = "myscheduler";
        dts.Resource.Outputs["tenantId"] = "tenant-456";

        // Publish update before starting so WatchAsync replays the snapshot
        await notificationService.PublishUpdateAsync(hub.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        var urlAnnotation = hub.Resource.Annotations.OfType<ResourceUrlAnnotation>().SingleOrDefault();
        Assert.NotNull(urlAnnotation);
        Assert.Contains("https://dashboard.durabletask.io/subscriptions/sub-123/schedulers/myscheduler/taskhubs/myhub", urlAnnotation.Url);
        Assert.Contains("endpoint=" + Uri.EscapeDataString("https://myscheduler.durabletask.io"), urlAnnotation.Url);
        Assert.Contains("tenantId=tenant-456", urlAnnotation.Url);
    }

    [Fact]
    public async Task DashboardUrlService_AzureMode_WithoutTenantId_OmitsTenantIdFromUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var hub = dts.AddTaskHub("myhub");

        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var appModel = new DistributedApplicationModel([dts.Resource, hub.Resource]);
        var service = new DurableTaskDashboardUrlService(appModel, notificationService);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Set Azure outputs on the scheduler without tenantId
        dts.Resource.Outputs["subscriptionId"] = "sub-123";
        dts.Resource.Outputs["schedulerEndpoint"] = "https://myscheduler.durabletask.io";
        dts.Resource.Outputs["name"] = "myscheduler";

        await notificationService.PublishUpdateAsync(hub.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        var urlAnnotation = hub.Resource.Annotations.OfType<ResourceUrlAnnotation>().SingleOrDefault();
        Assert.NotNull(urlAnnotation);
        Assert.Contains("https://dashboard.durabletask.io/subscriptions/sub-123/schedulers/myscheduler/taskhubs/myhub", urlAnnotation.Url);
        Assert.DoesNotContain("tenantId", urlAnnotation.Url);
    }

    [Fact]
    public async Task DashboardUrlService_EmulatorMode_AddsDashboardUrlAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsEmulator(e =>
            {
                e.WithEndpoint("grpc", e => e.AllocatedEndpoint = new(e, "localhost", 8080));
                e.WithEndpoint("http", e => e.AllocatedEndpoint = new(e, "localhost", 8081));
                e.WithEndpoint("dashboard", e => e.AllocatedEndpoint = new(e, "localhost", 8082));
            });
        var hub = dts.AddTaskHub("myhub");

        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var appModel = new DistributedApplicationModel([dts.Resource, hub.Resource]);
        var service = new DurableTaskDashboardUrlService(appModel, notificationService);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await notificationService.PublishUpdateAsync(hub.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(cts.Token);

        var urlAnnotation = hub.Resource.Annotations.OfType<ResourceUrlAnnotation>().SingleOrDefault();
        Assert.NotNull(urlAnnotation);
        Assert.Equal("http://localhost:8082/subscriptions/default/schedulers/default/taskhubs/myhub", urlAnnotation.Url);
    }

    [Fact]
    public async Task DashboardUrlService_AzureMode_MissingOutputs_DoesNotAddUrl()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");
        var hub = dts.AddTaskHub("myhub");

        var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var appModel = new DistributedApplicationModel([dts.Resource, hub.Resource]);
        var service = new DurableTaskDashboardUrlService(appModel, notificationService);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Only set partial outputs - missing schedulerEndpoint and name
        dts.Resource.Outputs["subscriptionId"] = "sub-123";

        await notificationService.PublishUpdateAsync(hub.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });

        await service.StartAsync(cts.Token);

        // The service should keep watching since it can't build a URL. Cancel to unblock.
        cts.Cancel();

        try
        {
            await service.ExecuteTask!;
        }
        catch (OperationCanceledException)
        {
        }

        var urlAnnotation = hub.Resource.Annotations.OfType<ResourceUrlAnnotation>().SingleOrDefault();
        Assert.Null(urlAnnotation);
    }

    private sealed class TestResource(string name) : Resource(name);
}
