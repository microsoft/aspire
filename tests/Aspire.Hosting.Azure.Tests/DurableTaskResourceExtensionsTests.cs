// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
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
    public void AddDurableTaskScheduler_IsExcludedFromPublishingManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");

        Assert.True(dts.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var manifestAnnotations));
        var annotation = Assert.Single(manifestAnnotations);
        Assert.Equal(ManifestPublishingCallbackAnnotation.Ignore, annotation);
    }

    [Fact]
    public void AddDurableTaskHub_IsExcludedFromPublishingManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts").RunAsExisting("Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure");
        var taskHub = dts.AddTaskHub("hub");

        Assert.True(taskHub.Resource.TryGetAnnotationsOfType<ManifestPublishingCallbackAnnotation>(out var manifestAnnotations));
        var annotation = Assert.Single(manifestAnnotations);
        Assert.Equal(ManifestPublishingCallbackAnnotation.Ignore, annotation);
    }

    [Fact]
    public void RunAsExisting_InPublishMode_DoesNotApplyConnectionStringAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsExisting("Endpoint=https://existing-scheduler.durabletask.io;Authentication=DefaultAzure");

        Assert.False(dts.ApplicationBuilder.ExecutionContext.IsRunMode);
        Assert.True(dts.ApplicationBuilder.ExecutionContext.IsPublishMode);

        var ex = Assert.Throws<InvalidOperationException>(() => _ = dts.Resource.ConnectionStringExpression);
        Assert.Contains("Unable to resolve the Durable Task Scheduler connection string", ex.Message);
    }

    [Fact]
    public void RunAsEmulator_InPublishMode_IsNoOp()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dts = builder.AddDurableTaskScheduler("dts")
            .RunAsEmulator();

        Assert.False(dts.Resource.IsEmulator);
        Assert.DoesNotContain(dts.Resource.Annotations, a => a is EmulatorResourceAnnotation);

        Assert.Throws<InvalidOperationException>(() => _ = dts.Resource.ConnectionStringExpression);
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
    public void DurableTaskSchedulerResource_WithoutEmulatorOrExistingConnectionString_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var dts = builder.AddDurableTaskScheduler("dts");

        var ex = Assert.Throws<InvalidOperationException>(() => _ = dts.Resource.ConnectionStringExpression);
        Assert.Contains("Unable to resolve the Durable Task Scheduler connection string", ex.Message);
    }
}
