// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.InnerLoop;

public class RadiusInfrastructureInitializationTests
{
    // These tests drive the pipeline through DistributedApplication.ExecuteBeforeStartHooksAsync,
    // which is internal and only reachable from inside Aspire.Hosting. UnsafeAccessor lets us
    // invoke it from outside without making it public. This mirrors the established pattern in
    // the Kubernetes/Docker integration tests.

    [Fact]
    public async Task PrepareDeploymentTargets_AttachesDeploymentTargetAnnotation_ToContainerResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var apiResource = model.Resources.First(r => r.Name == "api");
        var annotations = apiResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();

        Assert.Single(annotations);
        Assert.IsType<RadiusEnvironmentResource>(annotations[0].ComputeEnvironment);
    }

    [Fact]
    public async Task PrepareDeploymentTargets_AttachesAnnotation_ToAllComputeResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment();
        builder.AddContainer("frontend", "myapp/frontend:latest");
        builder.AddContainer("backend", "myapp/backend:latest");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        foreach (var resource in model.GetComputeResources())
        {
            var annotations = resource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();
            Assert.NotEmpty(annotations);
        }
    }

    [Fact]
    public async Task PrepareDeploymentTargets_DoesNotAnnotate_NonComputeResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");
        builder.AddParameter("my-param");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var paramResource = model.Resources.First(r => r.Name == "my-param");

        // Parameters are not compute resources, so should not have a deployment target annotation
        var annotations = paramResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();
        Assert.Empty(annotations);
    }

    [Fact]
    public async Task PrepareDeploymentTargets_AnnotationPointsToCorrectEnvironment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var apiResource = model.Resources.First(r => r.Name == "api");
        var annotation = apiResource.Annotations.OfType<DeploymentTargetAnnotation>().First();

        Assert.Same(env.Resource, annotation.ComputeEnvironment);
    }

    [Fact]
    public async Task PrepareDeploymentTargets_AttachesAnnotation_ToProjectResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment();
        builder.AddProject<TestProjectMetadata>("webapp");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var projectResource = model.Resources.First(r => r.Name == "webapp");
        var annotations = projectResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();

        Assert.Single(annotations);
        Assert.IsType<RadiusEnvironmentResource>(annotations[0].ComputeEnvironment);
    }

    [Fact]
    public async Task AddRadiusEnvironment_InRunMode_DoesNotRegisterEnvironmentResource()
    {
        // A3: In Run mode the integration short-circuits and the environment is NOT added to the
        // application builder. This avoids surfacing a publish-only resource in the dashboard
        // and prevents pipeline steps from being wired up for inner-loop scenarios. Matches the
        // Kubernetes / Docker Compose integration behaviour.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Empty(model.Resources.OfType<RadiusEnvironmentResource>());
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "testproject";
        public LaunchSettings LaunchSettings { get; } = new();
    }
}
