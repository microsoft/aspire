// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning.ContainerRegistry;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerRegistryTests
{
    [Fact]
    public async Task AddAzureContainerRegistry_AddsResourceAndImplementsIContainerRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        _ = builder.AddAzureContainerRegistry("acr");

        // Build & execute hooks
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var registryResource = Assert.Single(model.Resources.OfType<AzureContainerRegistryResource>());
        var registryInterface = Assert.IsType<IContainerRegistry>(registryResource, exactMatch: false);

        Assert.NotNull(registryInterface);
        Assert.NotNull(registryInterface.Name);
        Assert.NotNull(registryInterface.Endpoint);
    }

    [Fact]
    public async Task WithRegistry_AttachesContainerRegistryReferenceAnnotation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registryBuilder = builder.AddAzureContainerRegistry("acr");
        _ = builder.AddAzureContainerAppEnvironment("env")
                   .WithAzureContainerRegistry(registryBuilder); // Extension method under test

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environment = Assert.Single(model.Resources.OfType<AzureContainerAppEnvironmentResource>());

        Assert.True(environment.TryGetLastAnnotation<ContainerRegistryReferenceAnnotation>(out var annotation));
        Assert.Same(registryBuilder.Resource, annotation!.Registry);
    }

    [Fact]
    public async Task AddAzureContainerRegistry_GeneratesCorrectManifestAndBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr");

        var (manifest, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task WithRoleAssignments_GeneratesCorrectRoleAssignmentBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Add container app environment since it's required for role assignments
        builder.AddAzureContainerAppEnvironment("env");

        // Create a container registry and assign roles to a project
        var acr = builder.AddAzureContainerRegistry("acr");
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(acr, ContainerRegistryBuiltInRole.AcrPull, ContainerRegistryBuiltInRole.AcrPush);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var rolesResource = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "api-roles-acr");

        var (rolesManifest, rolesBicep) = await GetManifestWithBicep(rolesResource);

        await Verify(rolesManifest.ToString(), "json")
              .AppendContentAsFile(rolesBicep, "bicep");
              
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureContainerRegistryResource()
    {
        // Arrange
        var containerRegistryResource = new AzureContainerRegistryResource("test-acr", _ => { });
        var infrastructure = new AzureResourceInfrastructure(containerRegistryResource, "test-acr");

        // Act - Call AddAsExistingResource twice
        var firstResult = containerRegistryResource.AddAsExistingResource(infrastructure);
        var secondResult = containerRegistryResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureContainerRegistryResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-acr-name");
        var existingResourceGroup = builder.AddParameter("existing-acr-rg");

        var acr = builder.AddAzureContainerRegistry("test-acr")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = acr.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task AzureContainerRegistryHasLoginStep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var pipelineStepAnnotations = acr.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        Assert.True(pipelineStepAnnotations.Count >= 2);

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = acr.Resource
        };

        var allSteps = new List<PipelineStep>();
        foreach (var annotation in pipelineStepAnnotations)
        {
            allSteps.AddRange(await annotation.CreateStepsAsync(factoryContext));
        }

        var loginStep = allSteps.FirstOrDefault(s => s.Name == "login-to-acr-acr");
        Assert.NotNull(loginStep);
        Assert.Contains("acr-login", loginStep.Tags);
    }

    [Fact]
    public async Task LoginStepRequiredByPushPrereq()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var pipelineStepAnnotations = acr.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = acr.Resource
        };

        var allSteps = new List<PipelineStep>();
        foreach (var annotation in pipelineStepAnnotations)
        {
            allSteps.AddRange(await annotation.CreateStepsAsync(factoryContext));
        }

        var loginStep = allSteps.FirstOrDefault(s => s.Name == "login-to-acr-acr");
        Assert.NotNull(loginStep);
        Assert.Contains(WellKnownPipelineSteps.PushPrereq, loginStep.RequiredBySteps);
    }

    [Fact]
    public async Task AzureContainerRegistryHasProvisionStep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var pipelineStepAnnotations = acr.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = acr.Resource
        };

        var allSteps = new List<PipelineStep>();
        foreach (var annotation in pipelineStepAnnotations)
        {
            allSteps.AddRange(await annotation.CreateStepsAsync(factoryContext));
        }

        var provisionStep = allSteps.FirstOrDefault(s => s.Name == "provision-acr");
        Assert.NotNull(provisionStep);
        Assert.Contains(WellKnownPipelineTags.ProvisionInfrastructure, provisionStep.Tags);
    }

    [Fact]
    public void LoginStepDependsOnProvisionStep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");

        var configAnnotations = acr.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();
        Assert.True(configAnnotations.Count >= 2);
    }

    [Fact]
    public async Task WithPurgeTask_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", ago: TimeSpan.FromDays(7), keep: 5);

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithPurgeTask_MultipleTasks_GeneratesUniqueNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", filter: "app1:.*", keep: 3)
            .WithPurgeTask("0 2 * * 0", filter: "app2:.*", ago: TimeSpan.FromDays(30), keep: 10);

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithPurgeTask_CustomTaskName_UsedInBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 3 * * *", taskName: "myCustomPurge");

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData("bad cron")]
    [InlineData("")]
    [InlineData("  ")]
    public void WithPurgeTask_InvalidSchedule_Throws(string schedule)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.ThrowsAny<ArgumentException>(() => acr.WithPurgeTask(schedule));
    }

    [Fact]
    public void WithPurgeTask_NullSchedule_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.ThrowsAny<ArgumentException>(() => acr.WithPurgeTask(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithPurgeTask_InvalidKeep_Throws(int keep)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.Throws<ArgumentOutOfRangeException>(() => acr.WithPurgeTask("0 1 * * *", keep: keep));
    }

    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:01")]
    [InlineData("00:00:59")]
    public void WithPurgeTask_InvalidAgo_Throws(string ago)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.Throws<ArgumentOutOfRangeException>(() => acr.WithPurgeTask("0 1 * * *", ago: TimeSpan.Parse(ago)));
    }

    [Fact]
    public async Task WithPurgeTask_DuplicateTaskName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", taskName: "myPurge")
            .WithPurgeTask("0 2 * * *", taskName: "myPurge");

        await Assert.ThrowsAsync<ArgumentException>(async () => await GetManifestWithBicep(acr.Resource));
    }

    [Fact]
    public async Task WithPurgeTask_WithHoursAndMinutesAgo_FormatsCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 0 * * *", ago: new TimeSpan(2, 3, 6, 0));

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public void GetAzureContainerRegistry_ReturnsRegistryFromEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var env = builder.AddAzureContainerAppEnvironment("env")
                         .WithAzureContainerRegistry(acr);

        var registryBuilder = env.GetAzureContainerRegistry();

        Assert.Same(acr.Resource, registryBuilder.Resource);
    }

    [Fact]
    public void GetAzureContainerRegistry_ReturnsDefaultRegistryWhenNoExplicitRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var env = builder.AddAzureContainerAppEnvironment("env");

        var registryBuilder = env.GetAzureContainerRegistry();

        Assert.NotNull(registryBuilder.Resource);
        Assert.IsType<AzureContainerRegistryResource>(registryBuilder.Resource);
    }

    [Fact]
    public void GetAzureContainerRegistry_ThrowWhenExplicitNoRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        var registry = builder.AddContainerRegistry("ghcr", "ghcr.io", "owner/repo");

        var env = builder.AddAzureContainerAppEnvironment("env")
            .WithContainerRegistry(registry);
#pragma warning restore ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

        Assert.Throws<InvalidOperationException>(() => env.GetAzureContainerRegistry());
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
