// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREACADOMAINS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable CS0618 // Type or member is obsolete

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class ContainerRegistryTests
{
    [Fact]
    public async Task AzureContainerAppEnvironmentResourceImplementsContainerRegistry()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        _ = builder.AddAzureContainerAppEnvironment("env");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        // Assert
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        // Get IContainerRegistry interface
        var registry = environment as IContainerRegistry;
        Assert.NotNull(registry);

        // Verify registry properties are available
        Assert.NotNull(registry.Name);
        Assert.NotNull(registry.Endpoint);
        var azureRegistry = Assert.IsType<IAzureContainerRegistry>(registry, exactMatch: false);
        Assert.NotNull(azureRegistry.ManagedIdentityId);
    }

    [Fact]
    public async Task ContainerRegistryInfoFlowsToDeploymentTargetForProjects()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");

        builder.AddProject<TestProject>("api", launchProfileName: null)
               .WithHttpEndpoint();

        using var app = builder.Build();

        // Act
        await ExecuteBeforeStartHooksAsync(app, default);

        // Assert
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var project = Assert.Single(model.GetProjectResources());

        project.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var target);
        Assert.NotNull(target);

        // Verify that ContainerRegistryInfo property is not null for project resources
        Assert.NotNull(target.ContainerRegistry);

        // Verify that ContainerRegistryInfo is of type IContainerRegistry
        var registry = Assert.IsType<IContainerRegistry>(target.ContainerRegistry, exactMatch: false);

        // Verify registry properties are available
        Assert.NotNull(registry.Name);
        Assert.NotNull(registry.Endpoint);
        var azureRegistry = Assert.IsType<IAzureContainerRegistry>(registry, exactMatch: false);
        Assert.NotNull(azureRegistry.ManagedIdentityId);

    }

    [Fact]
    public async Task ContainerRegistryInfoIsAccessibleFromPublisher()
    {
        // Arrange
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddAzureContainerAppEnvironment("env");
        builder.AddContainer("api", "myimage");

        // Add a custom publisher that will validate the container registry info
        var publisherValidator = new ContainerRegistryValidatingPublisher();
        builder.Services.AddKeyedSingleton<IDistributedApplicationPublisher>("test-publisher", publisherValidator);

        using var app = builder.Build();

        // Act
        await ExecuteBeforeStartHooksAsync(app, default);

        // Get our publisher and manually invoke it
        var publisher = app.Services.GetRequiredKeyedService<IDistributedApplicationPublisher>("test-publisher") as ContainerRegistryValidatingPublisher;
        Assert.NotNull(publisher);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await publisher.PublishAsync(model, default);

        // Assert
        Assert.True(publisher.EnvironmentRegistryFound);
        Assert.NotNull(publisher.EnvironmentRegistry);

        // Verify the container registry properties on the environment
        Assert.NotNull(publisher.EnvironmentRegistry.Name);
        Assert.NotNull(publisher.EnvironmentRegistry.Endpoint);

        // Verify container registry info was found in child compute resources
        Assert.True(publisher.ComputeResourceRegistryFound);
        Assert.NotNull(publisher.ComputeResourceRegistry);

        // Verify the container registry properties on the compute resource
        Assert.NotNull(publisher.ComputeResourceRegistry.Name);
        Assert.NotNull(publisher.ComputeResourceRegistry.Endpoint);

        // Verify both registries have the same values
        Assert.Equal(publisher.EnvironmentRegistry.Name.ToString(),
                     publisher.ComputeResourceRegistry.Name.ToString());
        Assert.Equal(publisher.EnvironmentRegistry.Endpoint.ToString(),
                     publisher.ComputeResourceRegistry.Endpoint.ToString());

        // If the registry is an Azure container registry with managed identity, verify it
        if (publisher.EnvironmentRegistry is IAzureContainerRegistry azureRegistry)
        {
            Assert.NotNull(azureRegistry.ManagedIdentityId);
        }
    }

    /// <summary>
    /// A special publisher that checks for container registry information in both
    /// the environment resource and compute resources
    /// </summary>
    private sealed class ContainerRegistryValidatingPublisher : IDistributedApplicationPublisher
    {
        public bool EnvironmentRegistryFound { get; private set; }
        public IContainerRegistry? EnvironmentRegistry { get; private set; }

        public bool ComputeResourceRegistryFound { get; private set; }
        public IContainerRegistry? ComputeResourceRegistry { get; private set; }
        public IAzureContainerRegistry? AzureContainerRegistry { get; private set; }

        public Task PublishAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
        {
            // Look for container registry in Container App Environment resource
            foreach (var resource in model.Resources.OfType<IContainerRegistry>())
            {
                EnvironmentRegistryFound = true;
                EnvironmentRegistry = resource;
                break;
            }

            // Look for container registry in deployment target annotations
            foreach (var resource in model.Resources)
            {
                if (resource.TryGetLastAnnotation<DeploymentTargetAnnotation>(out var annotation) &&
                    annotation.ContainerRegistry != null)
                {
                    ComputeResourceRegistryFound = true;
                    ComputeResourceRegistry = annotation.ContainerRegistry;
                    if (ComputeResourceRegistry is IAzureContainerRegistry azureRegistry)
                    {
                        AzureContainerRegistry = azureRegistry;
                    }
                    break;
                }
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "testproject";
    }
}
