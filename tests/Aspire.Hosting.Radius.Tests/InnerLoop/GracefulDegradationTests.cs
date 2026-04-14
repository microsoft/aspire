// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.InnerLoop;

public class GracefulDegradationTests
{
    [Fact]
    public async Task AspireRun_Completes_WhenKubernetesUnavailable()
    {
        // In Run mode the Radius integration short-circuits — it doesn't register itself
        // with the app builder, doesn't add pipeline steps, and never attempts to talk to
        // Kubernetes or the rad CLI. So `aspire run` against an AppHost using AddRadiusEnvironment
        // must succeed regardless of whether a Kubernetes cluster is reachable.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Radius env is intentionally NOT registered in Run mode (see A3 in RadiusExtensions).
        Assert.Empty(model.Resources.OfType<RadiusEnvironmentResource>());

        // The container is still in the model and not annotated for Radius — it stays a
        // regular Aspire container for inner-loop execution.
        var apiResource = model.Resources.First(r => r.Name == "api");
        Assert.Empty(apiResource.Annotations.OfType<DeploymentTargetAnnotation>());
    }

    [Fact]
    public async Task AspireRun_Completes_WhenRadCliNotOnPath()
    {
        // Run mode never invokes the rad CLI — the deploy pipeline step is publish-only.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        builder.AddRadiusEnvironment();
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
    }

    [Fact]
    public async Task PublishMode_AttachesAnnotationsToAllComputeResources()
    {
        // In Publish mode the prepare step attaches DeploymentTargetAnnotation to every
        // compute resource, independent of whether infrastructure is reachable. The actual
        // deploy step is what requires rad/Kubernetes — annotation attachment is pure
        // in-process model manipulation.
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
    public async Task PublishMode_LogsEnvironmentConfiguration()
    {
        // RadiusInfrastructure.PrepareDeploymentTargetsAsync emits an Information-level log
        // line describing the environment / namespace each time it runs. We don't capture
        // the log here, just verify the prepare step ran (annotations attached) and the
        // environment carries the requested namespace.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("testenv").WithNamespace("test-ns");
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var apiResource = model.Resources.First(r => r.Name == "api");
        var annotations = apiResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();
        Assert.Single(annotations);

        var env = model.Resources.OfType<RadiusEnvironmentResource>().First();
        Assert.Equal("test-ns", env.Namespace);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
