// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureAppServiceEnvironmentExtensionsTests
{
    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureAppServiceEnvironmentResource()
    {
        // Arrange
        var appServiceEnvironmentResource = new AzureAppServiceEnvironmentResource("test-app-service-env", _ => { });
        var infrastructure = new AzureResourceInfrastructure(appServiceEnvironmentResource, "test-app-service-env");

        // Act - Call AddAsExistingResource twice
        var firstResult = appServiceEnvironmentResource.AddAsExistingResource(infrastructure);
        var secondResult = appServiceEnvironmentResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureAppServiceEnvironmentResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-appenv-name");
        var existingResourceGroup = builder.AddParameter("existing-appenv-rg");

        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("test-app-service-env")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = appServiceEnvironment.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public void ContainerRegistry_ReturnsDefaultContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env");

        // The environment should have a default container registry set up
        var registry = appServiceEnvironment.Resource.ContainerRegistry;
        Assert.NotNull(registry);
        Assert.IsType<AzureContainerRegistryResource>(registry);
    }

    [Fact]
    public void ContainerRegistry_PrefersExplicitContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("myacr");
        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
            .WithAzureContainerRegistry(acr);

        // Should return the explicitly set registry
        var registry = appServiceEnvironment.Resource.ContainerRegistry;
        Assert.Same(acr.Resource, registry);
    }

    [Fact]
    public void ContainerRegistry_ReturnsNullWhenNoRegistryConfigured()
    {
        // Create an environment resource without the builder to avoid automatic registry setup
        var environment = new AzureAppServiceEnvironmentResource("env", _ => { });

        Assert.Null(environment.ContainerRegistry);
    }

    [Fact]
    public void ContainerRegistry_ThrowsWhenNonAzureRegistryConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var dockerRegistry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var appServiceEnvironment = builder.AddAzureAppServiceEnvironment("env")
            .WithContainerRegistry(dockerRegistry);

        // Should throw because a non-Azure registry is configured
        var exception = Assert.Throws<InvalidOperationException>(() => appServiceEnvironment.Resource.ContainerRegistry);
        Assert.Contains("not an Azure Container Registry", exception.Message);
        Assert.Contains("env", exception.Message);
    }

    [Fact]
    public async Task PublishAsExisting_CrossResourceGroupAcr_EmitsScopedAcrPullRoleModule()
    {
        // Regression test for https://github.com/microsoft/aspire/issues/11256.
        // When the environment's container registry is an existing ACR in a DIFFERENT resource group,
        // the default ACR-pull role assignment must be emitted as a separately scoped module (not inlined
        // in the env Bicep), otherwise Bicep fails with BCP139.
        var tempDir = Directory.CreateTempSubdirectory(".acr-crossrg-appservice-test");
        try
        {
            var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, tempDir.FullName);

            var acr = builder.AddAzureContainerRegistry("acr")
                .PublishAsExisting("myexistingacr", "my-existing-resource-group");

            builder.AddAzureAppServiceEnvironment("env")
                .WithAzureContainerRegistry(acr);

            builder.AddProject<TestProject>("apiservice", launchProfileName: null);

            using var app = builder.Build();
            app.Run();

            var envBicep = await File.ReadAllTextAsync(Path.Combine(tempDir.FullName, "env", "env.bicep"));
            // The env module must not contain an inline AcrPull role assignment scoped to the cross-RG
            // registry (AcrPull built-in role id 7f951dda-4ed3-4680-a7ca-43fe172d538d) - that is what
            // triggers BCP139. The role assignment must instead live in a separately scoped module.
            Assert.DoesNotContain("7f951dda-4ed3-4680-a7ca-43fe172d538d", envBicep);

            var mainBicep = await File.ReadAllTextAsync(Path.Combine(tempDir.FullName, "main.bicep"));
            // A role-assignment module for the generated identity must be scoped to the registry's resource group.
            Assert.Contains("env_mi_roles_acr", mainBicep);
            Assert.Contains("resourceGroup('my-existing-resource-group')", mainBicep);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private sealed class TestProject : IProjectMetadata
    {
        public string ProjectPath => "another-path";

        public LaunchSettings? LaunchSettings { get; set; }
    }
}
