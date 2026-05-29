// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Foundry.Tests;

public class HostedAgentExtensionTests
{
    [Fact]
    public void AsHostedAgent_InRunMode_AddsHttpEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        // In run mode, the resource should have an HTTP endpoint annotation
        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        Assert.Contains(endpoints, e => e.Name == "http");
    }

    [Fact]
    public void AsHostedAgent_InRunMode_PreservesExistingHttpEndpointTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .WithHttpEndpoint(targetPort: 5000)
            .AsHostedAgent();

        builder.Build();

        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        var httpEndpoints = endpoints.Where(e => e.Name == "http").ToList();
        Assert.Single(httpEndpoints);
        Assert.Equal(5000, httpEndpoints[0].TargetPort);
        Assert.True(httpEndpoints[0].IsProxied);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_DoesNotHardCodePort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        Assert.True(app.Resource.TryGetEndpoints(out var endpoints));
        var httpEndpoint = endpoints.Single(e => e.Name == "http");
        Assert.Null(httpEndpoint.Port);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_ConfiguresSendMessageCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        var resource = builder.Resources.Single(r => r.Name == "agent");
        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.Equal("Send Message", command.DisplayName);
        Assert.Equal("ChatSparkle", command.IconName);
        Assert.Equal(IconVariant.Regular, command.IconVariant);
        Assert.True(command.IsHighlighted);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_DoesNotValidateRegion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        builder.Configuration["Azure:Location"] = "invalidregion";

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_AcceptsValidRegion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish);

        builder.Configuration["Azure:Location"] = "eastus";

        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_NoRegionConfig_DoesNotThrow()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        Assert.NotNull(app);
    }

    [Fact]
    public void AsHostedAgent_InPublishMode_CreatesHostedAgentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        var hostedAgent = builder.Resources.OfType<AzureHostedAgentResource>().SingleOrDefault();
        Assert.NotNull(hostedAgent);
        Assert.Equal("agent-ha", hostedAgent.Name);
    }

    [Fact]
    public void AsHostedAgent_WithoutProject_CreatesDefaultProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent();

        builder.Build();

        var project = builder.Resources.OfType<AzureCognitiveServicesProjectResource>().SingleOrDefault();
        Assert.NotNull(project);
    }

    [Fact]
    public void AsHostedAgent_InRunMode_WithProject_AddsProjectDependency()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        var app = builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        Assert.Contains(app.Resource.Annotations.OfType<WaitAnnotation>(), w => ReferenceEquals(w.Resource, project.Resource));
    }

    [Fact]
    public void AsHostedAgent_InRunMode_WithProject_RemovesDefaultContainerRegistryResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        builder.Build();

        Assert.Null(project.Resource.DefaultContainerRegistry);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "my-project-acr");
    }

    [Fact]
    public async Task AsHostedAgent_InRunMode_WithProject_ExecutesBeforeStartHooksWithoutContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var project = builder.AddFoundry("account")
            .AddProject("my-project");

        builder.AddPythonApp("agent", "./app.py", "main:app")
            .AsHostedAgent(project);

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public async Task FoundryProject_DefaultRegistryDoesNotAddGlobalRegistryTargets()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddAzureContainerRegistry("global");
        builder.AddFoundry("account")
            .AddProject("my-project");
        var container = builder.AddContainer("redis", "redis:latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));

        var registryTargets = container.Resource.Annotations.OfType<RegistryTargetAnnotation>().ToList();
        var registryTarget = Assert.Single(registryTargets);
        Assert.Same(registry.Resource, registryTarget.Registry);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
