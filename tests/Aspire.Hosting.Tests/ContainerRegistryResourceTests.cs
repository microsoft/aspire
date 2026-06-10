// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ContainerRegistryResourceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddContainerRegistryWithStrings()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "captainsafia");

        Assert.Equal("docker-hub", registry.Resource.Name);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Name);
        Assert.NotNull(containerRegistry.Endpoint);
        Assert.NotNull(containerRegistry.Repository);
    }

    [Fact]
    public void AddContainerRegistryWithStringsWithoutRepository()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io");

        Assert.Equal("acr", registry.Resource.Name);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Name);
        Assert.NotNull(containerRegistry.Endpoint);
        Assert.Null(containerRegistry.Repository);
    }

    [Fact]
    public void AddContainerRegistryWithParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var endpointParam = builder.AddParameter("registry-endpoint");
        var repositoryParam = builder.AddParameter("registry-repo");
        var registry = builder.AddContainerRegistry("my-registry", endpointParam, repositoryParam);

        Assert.Equal("my-registry", registry.Resource.Name);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Name);
        Assert.NotNull(containerRegistry.Endpoint);
        Assert.NotNull(containerRegistry.Repository);
    }

    [Fact]
    public void AddContainerRegistryWithParametersWithoutRepository()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var endpointParam = builder.AddParameter("registry-endpoint");
        var registry = builder.AddContainerRegistry("my-registry", endpointParam);

        Assert.Equal("my-registry", registry.Resource.Name);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Name);
        Assert.NotNull(containerRegistry.Endpoint);
        Assert.Null(containerRegistry.Repository);
    }

    [Fact]
    public void AddContainerRegistryWithNullBuilderThrows()
    {
        IDistributedApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() => builder.AddContainerRegistry("registry", "docker.io"));
    }

    [Fact]
    public void AddContainerRegistryWithNullNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Assert.Throws<ArgumentNullException>(() => builder.AddContainerRegistry(null!, "docker.io"));
    }

    [Fact]
    public void AddContainerRegistryWithEmptyNameThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Assert.Throws<ArgumentException>(() => builder.AddContainerRegistry("", "docker.io"));
    }

    [Fact]
    public void AddContainerRegistryWithNullEndpointStringThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Assert.Throws<ArgumentNullException>(() => builder.AddContainerRegistry("registry", (string)null!));
    }

    [Fact]
    public void AddContainerRegistryWithEmptyEndpointStringThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Assert.Throws<ArgumentException>(() => builder.AddContainerRegistry("registry", ""));
    }

    [Fact]
    public void AddContainerRegistryWithNullEndpointParameterThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        Assert.Throws<ArgumentNullException>(() => builder.AddContainerRegistry("registry", (IResourceBuilder<ParameterResource>)null!));
    }

    [Fact]
    public void ContainerRegistryResourceNotAddedToModelInRunMode()
    {
        // In run mode, the resource should not be added to the application model
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Resource should NOT be in the model in run mode
        Assert.Empty(appModel.Resources.OfType<ContainerRegistryResource>());
    }

    [Fact]
    public void ContainerRegistryResourceIsAddedToModelInPublishMode()
    {
        // In publish mode, the resource should be added to the application model
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var registry = Assert.Single(appModel.Resources.OfType<ContainerRegistryResource>());
        Assert.Equal("docker-hub", registry.Name);
    }

    [Fact]
    public void ContainerRegistryWithParametersNotAddedToModelInRunMode()
    {
        // In run mode, the resource should not be added to the application model
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var endpointParam = builder.AddParameter("registry-endpoint");
        builder.AddContainerRegistry("my-registry", endpointParam);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Resource should NOT be in the model in run mode
        Assert.Empty(appModel.Resources.OfType<ContainerRegistryResource>());
    }

    [Fact]
    public void ContainerRegistryWithParametersIsAddedToModelInPublishMode()
    {
        // In publish mode, the resource should be added to the application model
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var endpointParam = builder.AddParameter("registry-endpoint");
        builder.AddContainerRegistry("my-registry", endpointParam);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var registry = Assert.Single(appModel.Resources.OfType<ContainerRegistryResource>());
        Assert.Equal("my-registry", registry.Name);
    }

    [Fact]
    public async Task ContainerRegistryEndpointExpressionResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "captainsafia");

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);

        var endpointValue = await containerRegistry.Endpoint.GetValueAsync(default);
        Assert.Equal("docker.io", endpointValue);
    }

    [Fact]
    public async Task ContainerRegistryRepositoryExpressionResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "captainsafia");

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Repository);

        var repositoryValue = await containerRegistry.Repository.GetValueAsync(default);
        Assert.Equal("captainsafia", repositoryValue);
    }

    [Fact]
    public async Task ContainerRegistryNameExpressionResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "captainsafia");

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);

        var nameValue = await containerRegistry.Name.GetValueAsync(default);
        Assert.Equal("docker-hub", nameValue);
    }

    [Fact]
    public async Task ContainerRegistryWithParameterEndpointResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["Parameters:registry-endpoint"] = "ghcr.io";

        var endpointParam = builder.AddParameter("registry-endpoint");
        var registry = builder.AddContainerRegistry("ghcr", endpointParam);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);

        var endpointValue = await containerRegistry.Endpoint.GetValueAsync(default);
        Assert.Equal("ghcr.io", endpointValue);
    }

    [Fact]
    public async Task ContainerRegistryWithParameterRepositoryResolves()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["Parameters:registry-endpoint"] = "ghcr.io";
        builder.Configuration["Parameters:registry-repo"] = "captainsafia/my-repo";

        var endpointParam = builder.AddParameter("registry-endpoint");
        var repositoryParam = builder.AddParameter("registry-repo");
        var registry = builder.AddContainerRegistry("ghcr", endpointParam, repositoryParam);

        var containerRegistry = registry.Resource as IContainerRegistry;
        Assert.NotNull(containerRegistry);
        Assert.NotNull(containerRegistry.Repository);

        var repositoryValue = await containerRegistry.Repository.GetValueAsync(default);
        Assert.Equal("captainsafia/my-repo", repositoryValue);
    }

    [Fact]
    public void ContainerRegistryResourceImplementsIContainerRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");

        Assert.IsAssignableFrom<IContainerRegistry>(registry.Resource);
    }

    [Fact]
    public void MultipleContainerRegistriesCanBeAddedInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        builder.AddContainerRegistry("ghcr", "ghcr.io", "user2/repo");
        builder.AddContainerRegistry("acr", "myregistry.azurecr.io");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var registries = appModel.Resources.OfType<ContainerRegistryResource>().ToList();
        Assert.Equal(3, registries.Count);
        Assert.Contains(registries, r => r.Name == "docker-hub");
        Assert.Contains(registries, r => r.Name == "ghcr");
        Assert.Contains(registries, r => r.Name == "acr");
    }

    [Fact]
    public void WithContainerRegistryAddsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var container = builder.AddContainer("mycontainer", "myimage")
            .WithContainerRegistry(registry);

        var annotation = container.Resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Same(registry.Resource, annotation.Registry);
    }

    [Fact]
    public void WithContainerRegistryWithNullBuilderThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        IResourceBuilder<ContainerResource> containerBuilder = null!;

        Assert.Throws<ArgumentNullException>(() => containerBuilder.WithContainerRegistry(registry));
    }

    [Fact]
    public void WithContainerRegistryWithNullRegistryThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container = builder.AddContainer("mycontainer", "myimage");
        IResourceBuilder<ContainerRegistryResource> registry = null!;

        Assert.Throws<ArgumentNullException>(() => container.WithContainerRegistry(registry));
    }

    [Fact]
    public void ContainerWithDockerfileHasPushStepAndConfigurationAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var container = builder.AddDockerfile("mycontainer", "../myapp");

        var pipelineStepAnnotations = container.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        var pipelineConfigAnnotations = container.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();

        Assert.NotEmpty(pipelineStepAnnotations);
        Assert.NotEmpty(pipelineConfigAnnotations);
    }

    [Fact]
    public void ProjectResourceHasPushStepAndConfigurationAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        var pipelineStepAnnotations = project.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();
        var pipelineConfigAnnotations = project.Resource.Annotations.OfType<PipelineConfigurationAnnotation>().ToList();

        Assert.NotEmpty(pipelineStepAnnotations);
        Assert.NotEmpty(pipelineConfigAnnotations);
    }

    [Fact]
    public async Task ProjectResourcePushStepHasPushContainerImageTag()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        var pipelineStepAnnotation = Assert.Single(project.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = project.Resource
        };

        var steps = (await pipelineStepAnnotation.CreateStepsAsync(factoryContext)).ToList();

        var pushStep = steps.FirstOrDefault(s => s.Name == "push-api");
        Assert.NotNull(pushStep);
        Assert.Contains(WellKnownPipelineTags.PushContainerImage, pushStep.Tags);
        Assert.Contains(WellKnownPipelineSteps.Push, pushStep.RequiredBySteps);
    }

    [Fact]
    public async Task ContainerWithDockerfilePushStepHasPushContainerImageTag()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var container = builder.AddDockerfile("mycontainer", "../myapp");

        var pipelineStepAnnotation = Assert.Single(container.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = container.Resource
        };

        var steps = (await pipelineStepAnnotation.CreateStepsAsync(factoryContext)).ToList();

        var pushStep = steps.FirstOrDefault(s => s.Name == "push-mycontainer");
        Assert.NotNull(pushStep);
        Assert.Contains(WellKnownPipelineTags.PushContainerImage, pushStep.Tags);
        Assert.Contains(WellKnownPipelineSteps.Push, pushStep.RequiredBySteps);
    }

    [Fact]
    public async Task ContainerWithoutDockerfileHasNoPushStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var container = builder.AddContainer("mycontainer", "myimage");

        var pipelineStepAnnotations = container.Resource.Annotations.OfType<PipelineStepAnnotation>().ToList();

        foreach (var annotation in pipelineStepAnnotations)
        {
            var factoryContext = new PipelineStepFactoryContext
            {
                PipelineContext = null!,
                Resource = container.Resource
            };

            var steps = (await annotation.CreateStepsAsync(factoryContext)).ToList();

            Assert.DoesNotContain(steps, s => s.Tags.Contains(WellKnownPipelineTags.PushContainerImage));
        }
    }

    [Fact]
    public async Task ProjectResourcePushStepDependsOnBuildStep()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        var pipelineStepAnnotation = Assert.Single(project.Resource.Annotations.OfType<PipelineStepAnnotation>());

        var factoryContext = new PipelineStepFactoryContext
        {
            PipelineContext = null!,
            Resource = project.Resource
        };

        var steps = (await pipelineStepAnnotation.CreateStepsAsync(factoryContext)).ToList();

        var buildStep = steps.FirstOrDefault(s => s.Name == "build-api");
        var pushStep = steps.FirstOrDefault(s => s.Name == "push-api");

        Assert.NotNull(buildStep);
        Assert.NotNull(pushStep);

        Assert.Contains(WellKnownPipelineTags.BuildCompute, buildStep.Tags);
    }

    [Fact]
    public void WithContainerRegistryAddsAnnotationToProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithContainerRegistry(registry);

        var annotation = project.Resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().FirstOrDefault();
        Assert.NotNull(annotation);
        Assert.Same(registry.Resource, annotation.Registry);
    }

    [Fact]
    public void MultipleRegistriesCanBeAddedWithExplicitSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry1 = builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        var registry2 = builder.AddContainerRegistry("ghcr", "ghcr.io", "user2");

        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithContainerRegistry(registry1);

        var annotations = project.Resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>().ToList();
        Assert.Single(annotations);
        Assert.Same(registry1.Resource, annotations[0].Registry);
    }

    [Fact]
    public void ContainerRegistryReferenceAnnotationHoldsCorrectRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io");

        var annotation = new ContainerRegistryReferenceAnnotation(registry.Resource);

        Assert.Same(registry.Resource, annotation.Registry);
    }

    [Fact]
    public async Task RegistryTargetAnnotationIsAddedToResourcesOnBeforeStartEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Before BeforeStartEvent, the project should not have RegistryTargetAnnotation
        Assert.Empty(project.Resource.Annotations.OfType<RegistryTargetAnnotation>());

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // After BeforeStartEvent, the project should have RegistryTargetAnnotation
        var registryTargetAnnotation = Assert.Single(project.Resource.Annotations.OfType<RegistryTargetAnnotation>());
        Assert.Same(registry.Resource, registryTargetAnnotation.Registry);
    }

    [Fact]
    public async Task MultipleRegistriesAddMultipleRegistryTargetAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry1 = builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        var registry2 = builder.AddContainerRegistry("ghcr", "ghcr.io", "user2");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // Project should have two RegistryTargetAnnotations
        var registryTargetAnnotations = project.Resource.Annotations.OfType<RegistryTargetAnnotation>().ToList();
        Assert.Equal(2, registryTargetAnnotations.Count);

        var registryResources = registryTargetAnnotations.Select(a => a.Registry).ToList();
        Assert.Contains(registry1.Resource, registryResources);
        Assert.Contains(registry2.Resource, registryResources);
    }

    [Fact]
    public async Task GetContainerRegistryReturnsRegistryFromRegistryTargetAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent to add RegistryTargetAnnotation
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // GetContainerRegistry should return the registry from RegistryTargetAnnotation
        var containerRegistry = project.Resource.GetContainerRegistry();
        Assert.Same(registry.Resource, containerRegistry);
    }

    [Fact]
    public async Task GetContainerRegistryPrefersContainerRegistryReferenceAnnotationOverRegistryTargetAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry1 = builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        var registry2 = builder.AddContainerRegistry("ghcr", "ghcr.io", "user2");
        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithContainerRegistry(registry2); // Explicit preference for registry2

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent to add RegistryTargetAnnotations
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // GetContainerRegistry should return registry2 (from ContainerRegistryReferenceAnnotation)
        // even though both registries added RegistryTargetAnnotations
        var containerRegistry = project.Resource.GetContainerRegistry();
        Assert.Same(registry2.Resource, containerRegistry);
    }

    [Fact]
    public async Task GetContainerRegistryThrowsWhenMultipleRegistryTargetAnnotationsAndNoExplicitSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry1 = builder.AddContainerRegistry("docker-hub", "docker.io", "user1");
        var registry2 = builder.AddContainerRegistry("ghcr", "ghcr.io", "user2");
        var project = builder.AddProject<Projects.ServiceA>("api");
        // No explicit WithContainerRegistry call

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent to add RegistryTargetAnnotations
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // GetContainerRegistry should throw because there are multiple registries and no explicit selection
        var exception = Assert.Throws<InvalidOperationException>(project.Resource.GetContainerRegistry);
        Assert.Contains("multiple container registries", exception.Message);
        Assert.Contains("WithContainerRegistry", exception.Message);
    }

    [Fact]
    public void GetContainerRegistryThrowsWhenNoRegistryAvailable()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var project = builder.AddProject<Projects.ServiceA>("api");
        // No container registry added

        // GetContainerRegistry should throw because there's no registry available
        var exception = Assert.Throws<InvalidOperationException>(project.Resource.GetContainerRegistry);
        Assert.Contains("does not have a container registry reference", exception.Message);
    }

    [Fact]
    public async Task RegistryTargetAnnotationIsAddedToAllResourcesInModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var registry = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project1 = builder.AddProject<Projects.ServiceA>("api1");
        var project2 = builder.AddProject<Projects.ServiceB>("api2");
        var container = builder.AddContainer("redis", "redis:latest");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // All resources should have RegistryTargetAnnotation
        Assert.Single(project1.Resource.Annotations.OfType<RegistryTargetAnnotation>());
        Assert.Single(project2.Resource.Annotations.OfType<RegistryTargetAnnotation>());
        Assert.Single(container.Resource.Annotations.OfType<RegistryTargetAnnotation>());
    }

    [Fact]
    public async Task WithContainerRegistryOverridesDefaultRegistryTargetAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var defaultRegistry = builder.AddContainerRegistry("docker-hub", "docker.io", "default");
        var specificRegistry = builder.AddContainerRegistry("acr", "myregistry.azurecr.io", "specific");

        var project = builder.AddProject<Projects.ServiceA>("api")
            .WithContainerRegistry(specificRegistry);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // The project has both RegistryTargetAnnotations (from BeforeStartEvent) and ContainerRegistryReferenceAnnotation
        var registryTargetAnnotations = project.Resource.Annotations.OfType<RegistryTargetAnnotation>().ToList();
        Assert.Equal(2, registryTargetAnnotations.Count);

        var containerRegistryRefAnnotation = Assert.Single(project.Resource.Annotations.OfType<ContainerRegistryReferenceAnnotation>());
        Assert.Same(specificRegistry.Resource, containerRegistryRefAnnotation.Registry);

        // GetContainerRegistry should return specificRegistry because ContainerRegistryReferenceAnnotation takes precedence
        var containerRegistry = project.Resource.GetContainerRegistry();
        Assert.Same(specificRegistry.Resource, containerRegistry);
    }

    [Fact]
    public async Task AzureContainerRegistry_RegistryTargetAnnotationIsAddedToResourcesOnBeforeStartEvent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Before BeforeStartEvent, the project should not have RegistryTargetAnnotation
        Assert.Empty(project.Resource.Annotations.OfType<RegistryTargetAnnotation>());

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // After BeforeStartEvent, the project should have RegistryTargetAnnotation
        var registryTargetAnnotation = Assert.Single(project.Resource.Annotations.OfType<RegistryTargetAnnotation>());
        Assert.Same(acr.Resource, registryTargetAnnotation.Registry);
    }

    [Fact]
    public async Task AzureContainerRegistry_RegistryTargetAnnotationIsAddedToAllResourcesInModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var project = builder.AddProject<Projects.ServiceA>("api");
        var container = builder.AddContainer("redis", "redis:latest");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // All resources should have RegistryTargetAnnotation
        Assert.Single(project.Resource.Annotations.OfType<RegistryTargetAnnotation>());
        Assert.Single(container.Resource.Annotations.OfType<RegistryTargetAnnotation>());
    }

    [Fact]
    public async Task AzureContainerRegistry_GetContainerRegistryReturnsRegistryFromRegistryTargetAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent to add RegistryTargetAnnotation
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // GetContainerRegistry should return the registry from RegistryTargetAnnotation
        var containerRegistry = project.Resource.GetContainerRegistry();
        Assert.Same(acr.Resource, containerRegistry);
    }

    [Fact]
    public async Task AzureContainerRegistry_GetContainerRegistryPrefersContainerRegistryReferenceAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var env = builder.AddAzureContainerAppEnvironment("env")
            .WithAzureContainerRegistry(acr);

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent to add RegistryTargetAnnotations
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // GetContainerRegistry should return acr (from ContainerRegistryReferenceAnnotation)
        var containerRegistry = env.Resource.GetContainerRegistry();
        Assert.Same(acr.Resource, containerRegistry);
    }

    [Fact]
    public async Task AzureContainerRegistryAndContainerRegistry_MultipleRegistriesAddMultipleRegistryTargetAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var acr = builder.AddAzureContainerRegistry("acr");
        var dockerHub = builder.AddContainerRegistry("docker-hub", "docker.io", "myuser");
        var project = builder.AddProject<Projects.ServiceA>("api");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Simulate BeforeStartEvent
        var beforeStartEvent = new BeforeStartEvent(app.Services, appModel);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        // Project should have two RegistryTargetAnnotations (one from each registry)
        var registryTargetAnnotations = project.Resource.Annotations.OfType<RegistryTargetAnnotation>().ToList();
        Assert.Equal(2, registryTargetAnnotations.Count);

        var registryResources = registryTargetAnnotations.Select(a => a.Registry).ToList();
        Assert.Contains(acr.Resource, registryResources);
        Assert.Contains(dockerHub.Resource, registryResources);
    }
}
