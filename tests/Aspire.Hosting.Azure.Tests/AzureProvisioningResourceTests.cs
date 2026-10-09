// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureProvisioningResourceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task PublishAsAzureContainerApp_CreatesAzureContainerAppResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        builder.AddAzureContainerAppEnvironment("env");

        var apiProject = builder.AddProject<Project>("api", launchProfileName: null);
        apiProject.PublishAsAzureContainerApp((infrastructure, containerApp) =>
        {
            // This callback should have access to the original resource
            // via the AzureContainerAppResource.TargetResource property
            Assert.IsType<AzureContainerAppResource>(infrastructure.AspireResource);
            var containerAppResource = (AzureContainerAppResource)infrastructure.AspireResource;

            Assert.Same(apiProject.Resource, containerAppResource.TargetResource);
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(model.GetProjectResources());

        // Verify the deployment target was created
        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var provisioningResource = target?.DeploymentTarget as AzureContainerAppResource;
        Assert.NotNull(provisioningResource);

        // Verify the target resource is accessible
        Assert.Same(apiProject.Resource, provisioningResource.TargetResource);
    }

    [Fact]
    public async Task PublishAsAzureAppServiceWebsite_CreatesAzureWebSiteResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        builder.AddAzureAppServiceEnvironment("env");

        var apiProject = builder.AddProject<Project>("api", launchProfileName: null);
        apiProject.PublishAsAzureAppServiceWebsite((infrastructure, website) =>
        {
            // This callback should have access to the original resource
            // via the AzureAppServiceWebSiteResource.TargetResource property
            Assert.IsType<AzureAppServiceWebSiteResource>(infrastructure.AspireResource);
            var webSiteResource = (AzureAppServiceWebSiteResource)infrastructure.AspireResource;

            Assert.Same(apiProject.Resource, webSiteResource.TargetResource);
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(model.GetProjectResources());

        // Verify the deployment target was created
        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var provisioningResource = target?.DeploymentTarget as AzureAppServiceWebSiteResource;
        Assert.NotNull(provisioningResource);

        // Verify the target resource is accessible
        Assert.Same(apiProject.Resource, provisioningResource.TargetResource);
    }

    [Fact]
    public async Task ContainerResource_WithPublishAsContainerApp_CreatesAzureContainerAppResource()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        builder.AddAzureContainerAppEnvironment("env");

        var container = builder.AddContainer("api", "myimage");
        container.PublishAsAzureContainerApp((infrastructure, containerApp) =>
        {
            // Verify we can access the original container resource
            Assert.IsType<AzureContainerAppResource>(infrastructure.AspireResource);
            var containerAppResource = (AzureContainerAppResource)infrastructure.AspireResource;

            Assert.Same(container.Resource, containerAppResource.TargetResource);
        });

        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var containerResource = Assert.Single(model.GetContainerResources());

        // Verify the deployment target was created with the correct type
        containerResource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        var provisioningResource = target?.DeploymentTarget as AzureContainerAppResource;
        Assert.NotNull(provisioningResource);

        Assert.Same(container.Resource, provisioningResource.TargetResource);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
