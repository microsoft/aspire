// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.InnerLoop;

public class MultiEnvironmentTests
{
    [Fact]
    public void MultipleRadiusEnvironments_CanCoexist()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("env1").WithNamespace("ns1");
        builder.AddRadiusEnvironment("env2").WithNamespace("ns2");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environments = model.Resources.OfType<RadiusEnvironmentResource>().ToArray();
        Assert.Equal(2, environments.Length);
        Assert.Contains(environments, e => e.Namespace == "ns1");
        Assert.Contains(environments, e => e.Namespace == "ns2");
    }

    [Fact]
    public async Task UntargetedResources_WithMultipleEnvironments_FailFastWithClearError()
    {
        // A2: With multiple compute environments, the core ValidateComputeEnvironments pipeline
        // step rejects untargeted resources with a clear error directing the user to
        // WithComputeEnvironment. The Radius prepare step DependsOn that validation step,
        // so this error surfaces before any Radius-specific code runs (no silent first-env
        // claim, no duplicate annotations across environments).
        // See DistributedApplicationPipeline.ValidateComputeEnvironmentBindings.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("env1").WithNamespace("ns1");
        builder.AddRadiusEnvironment("env2").WithNamespace("ns2");
        builder.AddContainer("api", "myapp/api:latest");

        using var app = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteBeforeStartHooksAsync(app, default));

        Assert.Contains("'api'", ex.Message);
        Assert.Contains("WithComputeEnvironment", ex.Message);
    }

    [Fact]
    public async Task ExplicitlyTargetedResources_OnlyAnnotatedByOwningEnvironment()
    {
        // With multiple Radius environments and a resource explicitly targeted to one,
        // only that environment's prepare step claims the resource — no duplicate annotation
        // from the sibling environment.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env1 = builder.AddRadiusEnvironment("env1").WithNamespace("ns1");
        var env2 = builder.AddRadiusEnvironment("env2").WithNamespace("ns2");
        builder.AddContainer("api", "myapp/api:latest").WithComputeEnvironment(env1);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var apiResource = model.Resources.First(r => r.Name == "api");
        var annotations = apiResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();

        Assert.Single(annotations);
        Assert.Same(env1.Resource, annotations[0].ComputeEnvironment);
    }

    [Fact]
    public async Task DifferentNamespaces_DontCauseNamingCollisions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env1 = builder.AddRadiusEnvironment("env1").WithNamespace("ns1");
        builder.AddRadiusEnvironment("env2").WithNamespace("ns2");
        // Target explicitly to env1 — without explicit targeting, multi-env models fail fast
        // (see UntargetedResources_WithMultipleEnvironments_FailFastWithClearError).
        builder.AddContainer("api", "myapp/api:latest").WithComputeEnvironment(env1);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var environments = model.Resources.OfType<RadiusEnvironmentResource>().ToArray();
        Assert.Equal("ns1", environments[0].Namespace);
        Assert.Equal("ns2", environments[1].Namespace);

        var allResourceNames = model.Resources.Select(r => r.Name).ToArray();
        Assert.Equal(allResourceNames.Length, allResourceNames.Distinct().Count());

        var apiResource = model.Resources.First(r => r.Name == "api");
        var annotations = apiResource.Annotations.OfType<DeploymentTargetAnnotation>().ToArray();
        Assert.Single(annotations);
        Assert.Same(env1.Resource, annotations[0].ComputeEnvironment);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
