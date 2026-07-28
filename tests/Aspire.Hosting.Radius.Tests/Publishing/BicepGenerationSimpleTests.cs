// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class BicepGenerationSimpleTests
{
    [Fact]
    public void SimpleSingleContainer_GeneratesValidBicep_WithExtensionDirective()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        // Act
        var bicep = context.GenerateBicep(model);

        // Assert
        Assert.StartsWith("extension radius", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_GeneratesEnvironmentBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Core/environments@2025-08-01-preview", bicep);
        Assert.Contains("name: 'myenv'", bicep);
        Assert.Contains("recipePacks:", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_GeneratesApplicationBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Core/applications@2025-08-01-preview", bicep);
        Assert.Contains("environment: myenv.id", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_GeneratesContainerBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Compute/containers@2025-08-01-preview", bicep);
        Assert.Contains("name: 'api'", bicep);
        Assert.Contains("image: 'myapp/api:latest'", bicep);
        Assert.Contains("application: app.id", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_GeneratesRecipePackBlock()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Core/recipePacks@2025-08-01-preview", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_EmitsEnvironmentReference()
    {
        // The Radius.Compute/containers v2 schema requires properties.environment so the
        // control plane can resolve the recipe pack that provisions the container. Without
        // it, shipped Radius cannot deploy the native container (this was the gap that
        // previously blocked the native path on older Radius installs).
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("Radius.Compute/containers@2025-08-01-preview", bicep);
        Assert.Contains("environment: myenv.id", bicep);
    }

    [Fact]
    public void SimpleSingleContainer_RegistersDefaultContainerRecipeInPack()
    {
        // Shipped Radius has no built-in recipe for Radius.Compute/containers, so the
        // publisher must register the published container recipe in the env's recipe pack;
        // otherwise `rad deploy` fails ("no recipe pack found for Radius.Compute/containers").
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        Assert.Contains("'Radius.Compute/containers': {", bicep);
        Assert.Contains("ghcr.io/radius-project/kube-recipes/containers:latest", bicep);
    }

    [Fact]
    public void GenerateBicep_ProjectResourceWithoutContainerImage_ThrowsActionableError()
    {
        // The Aspire.Hosting.Radius integration does not yet build or push project images.
        // Without a clear failure at publish time the user would only see ImagePullBackOff
        // inside the cluster after `aspire deploy` (surfaced by Radius/Kubernetes, not
        // Aspire) — opaque and hard to attribute. Fail fast with a remediation hint
        // naming the missing prereq.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");
        builder.AddProject<TestProjectMetadata>("webapp");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        RadiusTestHelper.AttachDeploymentTargets(env.Resource, model);
        var context = new RadiusBicepPublishingContext(env.Resource);

        var ex = Assert.Throws<InvalidOperationException>(() => context.GenerateBicep(model));
        Assert.Contains("webapp", ex.Message);
        Assert.Contains("WithContainerImage", ex.Message);
    }

    [Fact]
    public void EnvironmentResource_HasPipelineStepAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var env = builder.AddRadiusEnvironment("myenv");

        var annotations = env.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        // A single PipelineStepAnnotation with a multi-step factory is registered in
        // RadiusEnvironmentResource..ctor (mirroring KubernetesEnvironmentResource).
        // The factory expands to three pipeline steps at execution time:
        //   1. prepare-deployment-targets-{name} — attaches DeploymentTargetAnnotation (A1)
        //   2. publish-radius-{name}             — emits app.bicep + bicepconfig.json
        //   3. deploy-radius-{name}              — invokes `rad deploy`
        Assert.Single(annotations);
    }

    private sealed class TestProjectMetadata : IProjectMetadata
    {
        public string ProjectPath => "testproject";
        public LaunchSettings LaunchSettings { get; } = new();
    }
}
