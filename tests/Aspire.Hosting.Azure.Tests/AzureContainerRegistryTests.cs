// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREAZURE003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.AppContainers;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure.Provisioning.ContainerRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureContainerRegistryTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task RunModeDoesNotGrantPullAccessForInertImageArtifacts()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run, testOutputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var registry = builder.AddAzureContainerRegistry("acr");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = registry.AddImage("published", source);
        var consumer = builder.AddProject<Project>("api", launchProfileName: null).WithReference(image);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        Assert.Empty(image.Resource.Annotations.OfType<ReferenceRoleAssignmentAnnotation>());
        Assert.Empty(consumer.Resource.Annotations.OfType<RoleAssignmentAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageConsumerReceivesScopedPullAccessAndWaitsForPublication(bool environmentOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var registry = builder.AddAzureContainerRegistry("acr");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = registry.AddImage("published", source);
        var second = registry.AddImage("second", source);
        var unrelatedRegistry = builder.AddAzureContainerRegistry("unrelated");
        unrelatedRegistry.AddImage("unused", source);
        var consumer = builder.AddProject<Project>("api", launchProfileName: null);
        if (environmentOnly)
        {
            consumer.WithEnvironment("IMAGE", image);
        }
        else
        {
            consumer.WithReference(image).WithReference(second);
        }
        var bystander = builder.AddProject<Project>("bystander", launchProfileName: null)
            .WithEnvironment("REGISTRY", registry.Resource.RegistryEndpoint);
        IReadOnlyList<PipelineStep> steps = [];
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            steps = context.Steps;
            return Task.CompletedTask;
        });
        builder.Services.Configure<PipelineOptions>(options => options.Step = WellKnownPipelineSteps.Diagnostics);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var roleAssignment = Assert.Single(model.Resources.OfType<AzureRoleAssignmentResource>(),
            resource => resource.Name == "api-roles-acr");
        Assert.Same(consumer.Resource, roleAssignment.OwnerResource);
        Assert.Same(registry.Resource, roleAssignment.TargetAzureResource);
        Assert.True(consumer.Resource.TryGetLastAnnotation<RoleAssignmentAnnotation>(out var roles));
        Assert.Equal([ContainerRegistryBuiltInRole.AcrPull.ToString()], roles.Roles.Select(role => role.Id));
        Assert.Empty(bystander.Resource.Annotations.OfType<RoleAssignmentAnnotation>().SelectMany(annotation => annotation.Roles));
        Assert.Equal(["api-roles-acr", "bystander-roles-acr"], model.Resources.OfType<AzureRoleAssignmentResource>()
            .Select(resource => resource.Name));

        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
            new PipelineContext(model, app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                app.Services, NullLogger.Instance, default));
        Assert.Single(image.Resource.Annotations.OfType<ReferenceRoleAssignmentAnnotation>());
        var target = Assert.IsType<AzureContainerAppResource>(consumer.Resource.GetDeploymentTargetAnnotation()!.DeploymentTarget);
        var provision = Assert.Single(steps, step => ReferenceEquals(step.Resource, target) &&
            step.Tags.Contains(WellKnownPipelineTags.ProvisionInfrastructure));
        Assert.Contains("push-published", provision.DependsOnSteps);
        if (!environmentOnly)
        {
            Assert.Contains("push-second", provision.DependsOnSteps);
        }
        var (manifest, bicep) = await GetManifestWithBicep(roleAssignment);
        var (consumerManifest, consumerBicep) = await GetManifestWithBicep(target);
        await Verify(manifest.ToString(), "json")
            .AppendContentAsFile(bicep, "bicep")
            .AppendContentAsFile(consumerManifest.ToString(), "json")
            .AppendContentAsFile(consumerBicep, "bicep");
    }

    [Fact]
    public async Task ImagePullAccessPreservesExplicitRegistryRoles()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var registry = builder.AddAzureContainerRegistry("acr");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = registry.AddImage("published", source);
        var consumer = builder.AddProject<Project>("api", launchProfileName: null)
            .WithRoleAssignments(registry, ContainerRegistryBuiltInRole.AcrPush)
            .WithReference(image);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);

        var roles = consumer.Resource.Annotations.OfType<RoleAssignmentAnnotation>()
            .Where(annotation => ReferenceEquals(annotation.Target, registry.Resource))
            .SelectMany(annotation => annotation.Roles)
            .Select(role => role.Id)
            .Distinct()
            .Order(StringComparer.Ordinal);
        Assert.Equal(new[] { ContainerRegistryBuiltInRole.AcrPull.ToString(), ContainerRegistryBuiltInRole.AcrPush.ToString() }
            .Order(StringComparer.Ordinal), roles);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("tag")]
    [InlineData("sha256")]
    [InlineData("registry")]
    [InlineData("repository")]
    public async Task IndividualImagePropertiesGrantSelectedRegistryPullAccess(string property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        builder.AddAzureContainerAppEnvironment("env");
        var registry = builder.AddAzureContainerRegistry("acr");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = registry.AddImage("published", source);
        var expression = property switch
        {
            "image" => image.Resource.ImageExpression,
            "tag" => image.Resource.TagExpression,
            "sha256" => image.Resource.Sha256Expression,
            "registry" => image.Resource.RegistryExpression,
            "repository" => image.Resource.RepositoryExpression,
            _ => throw new InvalidOperationException()
        };
        var consumer = builder.AddProject<Project>("api", launchProfileName: null).WithEnvironment("VALUE", expression);
        builder.AddAzureContainerRegistry("other").AddImage("unrelated", source);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var assignments = consumer.Resource.Annotations.OfType<RoleAssignmentAnnotation>().ToArray();
        Assert.All(assignments, assignment => Assert.Same(registry.Resource, assignment.Target));
        Assert.Equal([ContainerRegistryBuiltInRole.AcrPull.ToString()],
            assignments.SelectMany(assignment => assignment.Roles).Select(role => role.Id));
    }

    [Fact]
    public async Task ImageOnlyDeploymentIncludesRegistryProvisioningLoginAndPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddAzureContainerRegistry("acr");
        registry.AddImage("published", source);
        IReadOnlyList<PipelineStep> steps = [];
        builder.Pipeline.AddPipelineConfiguration(context =>
        {
            steps = context.Steps;
            return Task.CompletedTask;
        });
        builder.Services.Configure<PipelineOptions>(options => options.Step = WellKnownPipelineSteps.Diagnostics);
        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, default);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(
            new PipelineContext(model, app.Services.GetRequiredService<DistributedApplicationExecutionContext>(),
                app.Services, NullLogger.Instance, default));

        Assert.Empty(model.Resources.OfType<IComputeResource>());
        Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var provision = Assert.Single(steps, step => step.Resource == registry.Resource &&
            step.Tags.Contains(WellKnownPipelineTags.ProvisionInfrastructure));
        var login = Assert.Single(steps, step => step.Name == "login-to-acr-acr");
        var push = Assert.Single(steps, step => step.Name == "push-published");
        var prepare = Assert.Single(steps, step => step.Name == "prepare-image-tools");
        Assert.Contains(provision.Name, login.DependsOnSteps);
        Assert.Contains(login.Name, Assert.Single(steps, step => step.Name == WellKnownPipelineSteps.PushPrereq).DependsOnSteps);
        Assert.Contains(WellKnownPipelineSteps.PushPrereq, prepare.DependsOnSteps);
        Assert.Contains(prepare.Name, push.DependsOnSteps);
        Assert.Contains(provision.Name, push.DependsOnSteps);
        Assert.Contains(push.Name, Assert.Single(steps, step => step.Name == WellKnownPipelineSteps.Deploy).DependsOnSteps);
    }

    [Fact]
    public async Task AddAzureContainerRegistry_AddsResourceAndImplementsIContainerRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        var (manifest, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep");
    }

    [Fact]
    public async Task WithRoleAssignments_GeneratesCorrectRoleAssignmentBicep()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
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
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        var configAnnotations = acr.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();
        Assert.True(configAnnotations.Count >= 2);
    }

    [Fact]
    public async Task WithPurgeTask_GeneratesCorrectBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", ago: TimeSpan.FromDays(7), keep: 5);

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithPurgeTask_MultipleTasks_GeneratesUniqueNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", filter: "app1:.*", keep: 3)
            .WithPurgeTask("0 2 * * 0", filter: "app2:.*", ago: TimeSpan.FromDays(30), keep: 10);

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public async Task WithPurgeTask_CustomTaskName_UsedInBicep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

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
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.ThrowsAny<ArgumentException>(() => acr.WithPurgeTask(schedule));
    }

    [Fact]
    public void WithPurgeTask_NullSchedule_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.ThrowsAny<ArgumentException>(() => acr.WithPurgeTask(null!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithPurgeTask_InvalidKeep_Throws(int keep)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.Throws<ArgumentOutOfRangeException>(() => acr.WithPurgeTask("0 1 * * *", keep: keep));
    }

    [Theory]
    [InlineData("-00:00:01")]
    [InlineData("00:00:01")]
    [InlineData("00:00:59")]
    public void WithPurgeTask_InvalidAgo_Throws(string ago)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");

        Assert.Throws<ArgumentOutOfRangeException>(() => acr.WithPurgeTask("0 1 * * *", ago: TimeSpan.Parse(ago)));
    }

    [Fact]
    public async Task WithPurgeTask_DuplicateTaskName_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 1 * * *", taskName: "myPurge")
            .WithPurgeTask("0 2 * * *", taskName: "myPurge");

        await Assert.ThrowsAsync<ArgumentException>(async () => await GetManifestWithBicep(acr.Resource));
    }

    [Fact]
    public async Task WithPurgeTask_WithHoursAndMinutesAgo_FormatsCorrectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr")
            .WithPurgeTask("0 0 * * *", ago: new TimeSpan(2, 3, 6, 0));

        var (_, bicep) = await GetManifestWithBicep(acr.Resource);

        await Verify(bicep, "bicep");
    }

    [Fact]
    public void GetAzureContainerRegistry_ReturnsRegistryFromEnvironment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var acr = builder.AddAzureContainerRegistry("acr");
        var env = builder.AddAzureContainerAppEnvironment("env")
                         .WithAzureContainerRegistry(acr);

        var registryBuilder = env.GetAzureContainerRegistry();

        Assert.Same(acr.Resource, registryBuilder.Resource);
    }

    [Fact]
    public void GetAzureContainerRegistry_ReturnsDefaultRegistryWhenNoExplicitRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

        var env = builder.AddAzureContainerAppEnvironment("env");

        var registryBuilder = env.GetAzureContainerRegistry();

        Assert.NotNull(registryBuilder.Resource);
        Assert.IsType<AzureContainerRegistryResource>(registryBuilder.Resource);
    }

    [Fact]
    public void GetAzureContainerRegistry_ThrowWhenExplicitNoRegistry()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);

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
