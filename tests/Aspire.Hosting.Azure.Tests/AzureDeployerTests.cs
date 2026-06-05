// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIRECONTAINERRUNTIME001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Tests;

public class AzureDeployerTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task DeployAsync_PromptsViaInteractionService()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner(), setDefaultProvisioningOptions: false);

        // Add an Azure environment resource which will trigger the deployment prompting
        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();

        var runTask = Task.Run(app.Run);

        // Wait for the first interaction (subscription selection)
        var tenantInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure tenant", tenantInteraction.Title);
        Assert.False(tenantInteraction.Options!.EnableMessageMarkdown);

        Assert.Collection(tenantInteraction.Inputs,
            input =>
            {
                Assert.Equal("Tenant ID", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            });

        tenantInteraction.Inputs[0].Value = "87654321-4321-4321-4321-210987654321";
        tenantInteraction.CompletionTcs.SetResult(InteractionResult.Ok(tenantInteraction.Inputs));

        // Wait for the next interaction (subscription selection)
        var subscriptionInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure subscription", subscriptionInteraction.Title);
        Assert.False(subscriptionInteraction.Options!.EnableMessageMarkdown);

        // Verify the expected input for subscription selection (fallback to manual entry)
        Assert.Collection(subscriptionInteraction.Inputs,
            input =>
            {
                Assert.Equal("Subscription ID", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            });

        // Complete the subscription interaction
        subscriptionInteraction.Inputs[0].Value = "12345678-1234-1234-1234-123456789012";
        subscriptionInteraction.CompletionTcs.SetResult(InteractionResult.Ok(subscriptionInteraction.Inputs));

        // Wait for the resource group selection interaction
        var resourceGroupInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure resource group", resourceGroupInteraction.Title);
        Assert.False(resourceGroupInteraction.Options!.EnableMessageMarkdown);

        // Verify the expected input for resource group selection
        Assert.Collection(resourceGroupInteraction.Inputs,
            input =>
            {
                Assert.Equal("Resource group", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.False(input.Required);
            });

        // Complete the resource group interaction with a new resource group name
        resourceGroupInteraction.Inputs[0].Value = "test-rg";
        resourceGroupInteraction.CompletionTcs.SetResult(InteractionResult.Ok(resourceGroupInteraction.Inputs));

        // Wait for the location selection interaction (only shown for new resource groups)
        var locationInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure location and resource group", locationInteraction.Title);
        Assert.False(locationInteraction.Options!.EnableMessageMarkdown);

        // Verify the expected input for location selection
        Assert.Collection(locationInteraction.Inputs,
            input =>
            {
                Assert.Equal("Location", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            });

        // Complete the location interaction
        locationInteraction.Inputs[0].Value = "westus2";
        locationInteraction.CompletionTcs.SetResult(InteractionResult.Ok(locationInteraction.Inputs));

        // Wait for the run task to complete (or timeout)
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Verifies that deploying an application with resources that are build-only containers only builds
    /// the containers and does not attempt to push them.
    /// </summary>
    [Fact]
    public async Task DeployAsync_WithBuildOnlyContainers()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, activityReporter: mockActivityReporter);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");

        // Add a build-only container resource
        builder.AddExecutable("exe", "exe", ".")
            .PublishAsDockerFile(c =>
            {
                c.WithDockerfileBuilder(".", dockerfileContext =>
                {
                    var dockerBuilder = dockerfileContext.Builder
                        .From("scratch");
                });

                var dockerFileAnnotation = c.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Single();
                dockerFileAnnotation.HasEntrypoint = false;
            });

        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert - Verify MockImageBuilder was only called to build an image and not push it
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.True(mockImageBuilder.BuildImageCalled);
        var builtImage = Assert.Single(mockImageBuilder.BuildImageResources);
        Assert.Equal("exe", builtImage.Name);
        Assert.False(mockImageBuilder.PushImageCalled);
    }

    [Fact]
    public async Task DeployAsync_WithAzureStorageResourcesWorks()
    {
        // Arrange
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, activityReporter: mockActivityReporter, containerRuntime: fakeContainerRuntime);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();

        // Add Azure Storage with blob containers and queues
        var storage = builder.AddAzureStorage("teststorage");
        storage.AddBlobContainer("container1", blobContainerName: "test-container-1");
        storage.AddBlobContainer("container2", blobContainerName: "test-container-2");
        storage.AddQueue("testqueue", queueName: "test-queue");

        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that ACR login was not called given no compute resources
        Assert.False(fakeContainerRuntime.WasLoginToRegistryCalled);

        // Assert - Verify MockImageBuilder was NOT called when there are no compute resources
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.False(mockImageBuilder.BuildImageCalled);
        Assert.False(mockImageBuilder.BuildImagesCalled);
        Assert.False(mockImageBuilder.PushImageCalled);
        Assert.Empty(mockImageBuilder.BuildImageResources);
        Assert.Empty(mockImageBuilder.PushImageCalls);
    }

    [Fact]
    public async Task DeployAsync_WithContainer_Works()
    {
        // Arrange
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, activityReporter: mockActivityReporter, processRunner: mockProcessRunner, containerRuntime: fakeContainerRuntime);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();
        builder.AddContainer("api", "my-api-image:latest");

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated to outputs because they are
        // hoisted up for the container resource
        Assert.Equal("testregistry", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("testregistry.azurecr.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert - Verify ACR login was not called since no image was pushed
        Assert.False(fakeContainerRuntime.WasLoginToRegistryCalled);

        // Assert - Verify MockImageBuilder push method was NOT called for existing container image
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.False(mockImageBuilder.PushImageCalled);
        Assert.Empty(mockImageBuilder.PushImageCalls);
    }

    [Fact]
    public async Task DeployAsync_WithDockerfile_Works()
    {
        // Arrange
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, activityReporter: mockActivityReporter, processRunner: mockProcessRunner, containerRuntime: fakeContainerRuntime);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();
        var api = builder.AddDockerfile("api", "api.Dockerfile");

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated to outputs because they are
        // hoisted up for the container resource
        Assert.Equal("testregistry", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("testregistry.azurecr.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert - Verify ACR login was called using IContainerRuntime
        Assert.True(fakeContainerRuntime.WasLoginToRegistryCalled);
        Assert.Contains(fakeContainerRuntime.LoginToRegistryCalls, call =>
            call.registryServer == "testregistry.azurecr.io" &&
            call.username == "00000000-0000-0000-0000-000000000000");

        // Assert - Verify MockImageBuilder push method was called
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.True(mockImageBuilder.PushImageCalled);

        // Verify specific push call was made for the api resource
        Assert.Contains(mockImageBuilder.PushImageCalls, resource =>
            resource.Name == "api");
    }

    [Fact]
    public async Task DeployAsync_WithProjectResource_Works()
    {
        // Arrange
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter, containerRuntime: fakeContainerRuntime);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();
        var api = builder.AddProject<Project>("api", launchProfileName: null);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated to outputs because they are
        // hoisted up for the container resource
        Assert.Equal("testregistry", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("testregistry.azurecr.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert - Verify ACR login was called using IContainerRuntime
        Assert.True(fakeContainerRuntime.WasLoginToRegistryCalled);
        Assert.Contains(fakeContainerRuntime.LoginToRegistryCalls, call =>
            call.registryServer == "testregistry.azurecr.io" &&
            call.username == "00000000-0000-0000-0000-000000000000");

        // Assert - Verify MockImageBuilder push method was called
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.True(mockImageBuilder.PushImageCalled);

        // Verify specific push call was made for the api resource
        Assert.Contains(mockImageBuilder.PushImageCalls, resource =>
            resource.Name == "api");
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("diagnostics")]
    public async Task DeployAsync_WithMultipleComputeEnvironments_Works(string step)
    {
        // Arrange
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("aca-env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "acaregistry" },
                    ["loginServer"] = new { type = "String", value = "acaregistry.azurecr.io" }
                },
                string name when name.StartsWith("aca-env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "acaregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "acaregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aca-identity" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID"] = new { type = "String", value = "aca-client-id" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "aca.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/acaenv" }
                },
                string name when name.StartsWith("aas-env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "aasregistry" },
                    ["loginServer"] = new { type = "String", value = "aasregistry.azurecr.io" }
                },
                string name when name.StartsWith("aas-env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "aasregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "aasregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aas-identity" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID"] = new { type = "String", value = "aas-client-id" },
                    ["AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aas-website-identity" },
                    ["AZURE_WEBSITE_CONTRIBUTOR_MANAGED_IDENTITY_PRINCIPAL_ID"] = new { type = "String", value = "aas-website-principal-id" },
                    ["webSiteSuffix"] = new { type = "String", value = ".azurewebsites.net" },
                    ["planId"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/serverfarms/aasplan" },
                    ["AZURE_APP_SERVICE_DASHBOARD_URI"] = new { type = "String", value = "https://infra-aspiredashboard-test.azurewebsites.net" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter, containerRuntime: fakeContainerRuntime);

        var acaEnv = builder.AddAzureContainerAppEnvironment("aca-env");
        var aasEnv = builder.AddAzureAppServiceEnvironment("aas-env");
        var azureEnv = builder.AddAzureEnvironment();

        var storage = builder.AddAzureStorage("storage");
        storage.AddBlobContainer("mycontainer1", blobContainerName: "test-container-1");
        storage.AddBlobContainer("mycontainer2", blobContainerName: "test-container-2");
        storage.AddQueue("myqueue", queueName: "my-queue");

        builder.AddRedis("cache").WithComputeEnvironment(acaEnv);
        var apiService = builder.AddProject<Project>("api-service", launchProfileName: null).WithComputeEnvironment(aasEnv);
        var pythonApp = builder.AddDockerfile("python-app", "python-app.Dockerfile").WithComputeEnvironment(acaEnv);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        if (step == "diagnostics")
        {
            // In diagnostics mode, just verify logs match snapshot
            var logs = mockActivityReporter.LoggedMessages
                            .Where(s => s.StepTitle == "diagnostics")
                            .Select(s => s.Message)
                            .ToList();

            await Verify(logs);
            return;
        }

        // Assert ACA environment outputs are properly set
        Assert.Equal("acaregistry", acaEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("acaregistry.azurecr.io", acaEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aca-identity", acaEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("aca.westus.azurecontainerapps.io", acaEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/acaenv", acaEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert AAS environment outputs are properly set
        Assert.Equal("aasregistry", aasEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("aasregistry.azurecr.io", aasEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/aas-identity", aasEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/serverfarms/aasplan", aasEnv.Resource.Outputs["planId"]);
        Assert.Equal("aas-client-id", aasEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID"]);

        // Assert - Verify ACR login was called using IContainerRuntime for both registries
        Assert.True(fakeContainerRuntime.WasLoginToRegistryCalled);
        Assert.Contains(fakeContainerRuntime.LoginToRegistryCalls, call =>
            call.registryServer == "acaregistry.azurecr.io" &&
            call.username == "00000000-0000-0000-0000-000000000000");
        Assert.Contains(fakeContainerRuntime.LoginToRegistryCalls, call =>
            call.registryServer == "aasregistry.azurecr.io" &&
            call.username == "00000000-0000-0000-0000-000000000000");

        // Assert - Verify MockImageBuilder push method was called for multiple registries
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.True(mockImageBuilder.PushImageCalled);

        // Verify push calls were made for both resources
        Assert.Contains(mockImageBuilder.PushImageCalls, resource =>
            resource.Name == "api-service");
        Assert.Contains(mockImageBuilder.PushImageCalls, resource =>
            resource.Name == "python-app");

        // Verify that redis (existing container) was not pushed
        Assert.DoesNotContain(mockImageBuilder.PushImageCalls, resource => resource.Name == "cache");
    }

    [Fact]
    public async Task DeployAsync_WithUnresolvedParameters_PromptsForParameterValues()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());

        // Add a parameter that will be unresolved
        var param = builder.AddParameter("test-param");
        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        var runTask = Task.Run(app.Run);

        // Wait for the parameter inputs interaction (no notification in publish mode)
        var parameterInputs = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Set unresolved parameters", parameterInputs.Title);

        // Verify the parameter input (should not include save to secrets option in publish mode)
        Assert.Collection(parameterInputs.Inputs,
            input =>
            {
                Assert.Equal("test-param", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.Equal("Enter value for test-param", input.Placeholder);
            });

        // Complete the parameter inputs interaction
        parameterInputs.Inputs[0].Value = "test-value";
        parameterInputs.CompletionTcs.SetResult(InteractionResult.Ok(parameterInputs.Inputs));

        // Wait for the run task to complete (or timeout)
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        var setValue = await param.Resource.GetValueAsync(default);
        Assert.Equal("test-value", setValue);
    }

    [Fact]
    public async Task DeployAsync_WithResolvedParameters_SkipsPrompting()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());
        builder.Configuration["Parameters:test-param-2"] = "resolved-value-2";

        // Add a parameter with a resolved value
        var param = builder.AddParameter("test-param", () => "resolved-value");
        var secondParam = builder.AddParameter("test-param-2");
        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        Assert.Equal(0, testInteractionService.Interactions.Reader.Count);
    }

    [Fact]
    public async Task DeployAsync_WithCustomInputGeneratorParameter_RespectsInputGenerator()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());

        // Add a parameter with a custom input generator
        var param = builder.AddParameter("custom-param")
            .WithCustomInput(p => new InteractionInput
            {
                Name = p.Name,
                InputType = InputType.Number,
                Label = "Custom Port Number",
                Description = "Enter a custom port number for the service",
                EnableDescriptionMarkdown = false,
                Placeholder = "8080"
            });
        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        var runTask = Task.Run(app.Run);

        // Wait for the parameter inputs interaction (no notification in publish mode)
        var parameterInputs = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Set unresolved parameters", parameterInputs.Title);

        // Verify the custom input generator is respected (should not include save to secrets option in publish mode)
        Assert.Collection(parameterInputs.Inputs,
            input =>
            {
                Assert.Equal("custom-param", input.Name);
                Assert.Equal("Custom Port Number", input.Label);
                Assert.Equal("Enter a custom port number for the service", input.Description);
                Assert.Equal(InputType.Number, input.InputType);
                Assert.Equal("8080", input.Placeholder);
                Assert.False(input.EnableDescriptionMarkdown);
            });

        // Complete the parameter inputs interaction
        parameterInputs.Inputs[0].Value = "9090";
        parameterInputs.CompletionTcs.SetResult(InteractionResult.Ok(parameterInputs.Inputs));

        // Wait for the run task to complete (or timeout)
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        var setValue = await param.Resource.GetValueAsync(default);
        Assert.Equal("9090", setValue);
    }

    [Fact]
    public async Task DeployAsync_WithSingleRedisCache_CallsDeployingComputeResources()
    {
        // Arrange
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();

        // Add a single Redis cache resource which is a compute resource
        builder.AddRedis("cache").WithComputeEnvironment(containerAppEnv);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated
        Assert.Equal("testregistry", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("testregistry.azurecr.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert that compute resources deployment logic was triggered (Redis doesn't require image build/push)
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.False(mockImageBuilder.BuildImageCalled);
        Assert.False(mockImageBuilder.PushImageCalled);

        // Assert that ACR login was not called since Redis uses existing container image
        Assert.False(fakeContainerRuntime.WasLoginToRegistryCalled);

        // Assert that deploying steps executed
        Assert.Contains("provision-cache-containerapp", mockActivityReporter.CreatedSteps);
    }

    [Fact]
    public async Task DeployAsync_WithOnlyAzureResources_PrintsDashboardUrl()
    {
        // Arrange
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env-acr") => new Dictionary<string, object>
                {
                    ["name"] = new { type = "String", value = "testregistry" },
                    ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
                },
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                    ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();

        // Add only Azure resources (no compute resources)
        var storage = builder.AddAzureStorage("teststorage");
        storage.AddBlobContainer("container1", blobContainerName: "test-container-1");

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert that no compute resources were deployed (no image build/push)
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.False(mockImageBuilder.BuildImageCalled);
        Assert.False(mockImageBuilder.PushImageCalled);

        // Assert that ACR login was not called since only Azure resources exist
        Assert.False(fakeContainerRuntime.WasLoginToRegistryCalled);

        // Assert that the completion request was called
        Assert.True(mockActivityReporter.CompletePublishCalled);
    }

    [Fact]
    public async Task DeployAsync_WithGeneratedParameters_DoesNotPromptsForParameterValues()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());

        // Add a parameter with GenerateParameterDefault (like Redis password)
        var redis = builder.AddRedis("cache");
        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        Assert.Equal(0, testInteractionService.Interactions.Reader.Count);
    }

    [Fact]
    public async Task DeployAsync_WithParametersInEnvironmentVariables_DiscoversAndPromptsForParameters()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());

        // Create a parameter that will be referenced in environment variables but not added to the model
        var dependentParam = new ParameterResource("dependent-param", p => throw new MissingParameterValueException("Should be prompted"), secret: false);

        // Create a container that references the parameter in its environment variables
        var container = builder.AddContainer("test-container", "test-image")
            .WithEnvironment("DEPENDENT_VALUE", dependentParam);

        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        var runTask = Task.Run(app.Run);

        // Wait for the parameter inputs interaction (no notification in publish mode)
        var parameterInputs = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Set unresolved parameters", parameterInputs.Title);

        // Verify the dependent parameter is discovered and prompted for (should not include save to secrets option in publish mode)
        Assert.Collection(parameterInputs.Inputs,
            input =>
            {
                Assert.Equal("dependent-param", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.Equal("Enter value for dependent-param", input.Placeholder);
            });

        // Complete the parameter inputs interaction
        parameterInputs.Inputs[0].Value = "discovered-param-value";
        parameterInputs.CompletionTcs.SetResult(InteractionResult.Ok(parameterInputs.Inputs));

        // Wait for the run task to complete (or timeout)
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        var setValue = await dependentParam.GetValueAsync(default);
        Assert.Equal("discovered-param-value", setValue);
    }

    [Fact]
    public async Task DeployAsync_WithParametersInArguments_DiscoversAndPromptsForParameters()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var testInteractionService = new TestInteractionService();
        ConfigureTestServices(builder, interactionService: testInteractionService, bicepProvisioner: new NoOpBicepProvisioner());

        // Create a parameter that will be referenced in command line arguments but not added to the model
        var portParam = new ParameterResource("app-port", p => throw new MissingParameterValueException("Should be prompted"), secret: false);

        // Create a container that references the parameter in its command line arguments
        var container = builder.AddContainer("test-container", "test-image")
            .WithArgs("--port", portParam, "--verbose");

        builder.AddAzureEnvironment();

        // Act
        using var app = builder.Build();
        var runTask = Task.Run(app.Run);

        // Wait for the parameter inputs interaction (no notification in publish mode)
        var parameterInputs = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Set unresolved parameters", parameterInputs.Title);

        // Verify the dependent parameter is discovered and prompted for (should not include save to secrets option in publish mode)
        Assert.Collection(parameterInputs.Inputs,
            input =>
            {
                Assert.Equal("app-port", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.Equal("Enter value for app-port", input.Placeholder);
            });

        // Complete the parameter inputs interaction
        parameterInputs.Inputs[0].Value = "8080";
        parameterInputs.CompletionTcs.SetResult(InteractionResult.Ok(parameterInputs.Inputs));

        // Wait for the run task to complete (or timeout)
        await runTask.WaitAsync(TimeSpan.FromSeconds(10));

        var setValue = await portParam.GetValueAsync(default);
        Assert.Equal("8080", setValue);
    }

    [Fact]
    public async Task DeployAsync_WithAzureFunctionsProject_Works()
    {
        // Arrange
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var deploymentOutputsProvider = (string deploymentName) => deploymentName switch
        {
            string name when name.StartsWith("env-acr") => new Dictionary<string, object>
            {
                ["name"] = new { type = "String", value = "testregistry" },
                ["loginServer"] = new { type = "String", value = "testregistry.azurecr.io" }
            },
            string name when name.StartsWith("env") => new Dictionary<string, object>
            {
                ["name"] = new { type = "String", value = "env" },
                ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                ["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"] = new { type = "String", value = "test.westus.azurecontainerapps.io" },
                ["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv" }
            },
            string name when name.StartsWith("funcstorage") => new Dictionary<string, object>
            {
                ["name"] = new { type = "String", value = "testfuncstorage" },
                ["blobEndpoint"] = new { type = "String", value = "https://testfuncstorage.blob.core.windows.net/" },
                ["queueEndpoint"] = new { type = "String", value = "https://testfuncstorage.queue.core.windows.net/" },
                ["tableEndpoint"] = new { type = "String", value = "https://testfuncstorage.table.core.windows.net/" }
            },
            string name when name.StartsWith("hoststorage") => new Dictionary<string, object>
            {
                ["name"] = new { type = "String", value = "testhoststorage" },
                ["blobEndpoint"] = new { type = "String", value = "https://testhoststorage.blob.core.windows.net/" },
                ["queueEndpoint"] = new { type = "String", value = "https://testhoststorage.queue.core.windows.net/" },
                ["tableEndpoint"] = new { type = "String", value = "https://testhoststorage.table.core.windows.net/" }
            },
            string name when name.StartsWith("funcapp-identity") => new Dictionary<string, object>
            {
                ["id"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/funcapp-identity" },
                ["clientId"] = new { type = "String", value = "funcapp-client-id" },
                ["principalId"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
            },
            string name when name.StartsWith("funcapp-containerapp") => new Dictionary<string, object>
            {
                ["id"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/containerApps/funcapp" },
                ["name"] = new { type = "String", value = "funcapp" },
                ["fqdn"] = new { type = "String", value = "funcapp.azurecontainerapps.io" },
                ["environmentId"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/env" }
            },
            string name when name.StartsWith("funcapp") => new Dictionary<string, object>
            {
                ["identity_id"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                ["identity_clientId"] = new { type = "String", value = "test-client-id" }
            },
            _ => []
        };

        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider(deploymentOutputsProvider);
        ConfigureTestServices(builder, armClientProvider: armClientProvider, activityReporter: mockActivityReporter, processRunner: mockProcessRunner, containerRuntime: fakeContainerRuntime);

        var containerAppEnv = builder.AddAzureContainerAppEnvironment("env");
        var azureEnv = builder.AddAzureEnvironment();

        // Add Azure Storage for the Functions project
        var storage = builder.AddAzureStorage("funcstorage");
        var hostStorage = builder.AddAzureStorage("hoststorage");
        var blobs = storage.AddBlobs("blobs");
        var funcApp = builder.AddAzureFunctionsProject<TestFunctionsProject>("funcapp")
            .WithReference(blobs)
            .WithHostStorage(hostStorage);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Assert that publish completed without errors
        Assert.NotEqual(CompletionState.CompletedWithError, mockActivityReporter.ResultCompletionState);

        // Assert that container environment outputs are propagated
        Assert.Equal("testregistry", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_NAME"]);
        Assert.Equal("testregistry.azurecr.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_ENDPOINT"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"]);
        Assert.Equal("test.westus.azurecontainerapps.io", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal("/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.App/managedEnvironments/testenv", containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);

        // Assert that funcapp outputs are propagated
        var funcAppDeployment = Assert.IsAssignableFrom<AzureProvisioningResource>(funcApp.Resource.GetDeploymentTargetAnnotation()?.DeploymentTarget);
        Assert.NotNull(funcAppDeployment);
        Assert.Equal(await ((BicepOutputReference)funcAppDeployment.Parameters["env_outputs_azure_container_apps_environment_default_domain"]!).GetValueAsync(), containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_DEFAULT_DOMAIN"]);
        Assert.Equal(await ((BicepOutputReference)funcAppDeployment.Parameters["env_outputs_azure_container_apps_environment_id"]!).GetValueAsync(), containerAppEnv.Resource.Outputs["AZURE_CONTAINER_APPS_ENVIRONMENT_ID"]);
        Assert.Equal("https://testfuncstorage.blob.core.windows.net/", await ((BicepOutputReference)funcAppDeployment.Parameters["funcstorage_outputs_blobendpoint"]!).GetValueAsync());
        Assert.Equal("https://testhoststorage.blob.core.windows.net/", await ((BicepOutputReference)funcAppDeployment.Parameters["hoststorage_outputs_blobendpoint"]!).GetValueAsync());

        // Assert - Verify ACR login was called using IContainerRuntime since Functions image needs to be built and pushed
        Assert.True(fakeContainerRuntime.WasLoginToRegistryCalled);
        Assert.Contains(fakeContainerRuntime.LoginToRegistryCalls, call =>
            call.registryServer == "testregistry.azurecr.io" &&
            call.username == "00000000-0000-0000-0000-000000000000");

        // Assert - Verify MockImageBuilder push method was called
        var mockImageBuilder = app.Services.GetRequiredService<IResourceContainerImageManager>() as MockImageBuilder;
        Assert.NotNull(mockImageBuilder);
        Assert.True(mockImageBuilder.PushImageCalled);

        // Verify specific push call was made for the funcapp resource
        Assert.Contains(mockImageBuilder.PushImageCalls, resource =>
            resource.Name == "funcapp");
    }

    [Theory]
    [InlineData("deploy-api")]
    [InlineData("diagnostics")]
    public async Task DeployAsync_WithAzureResourceDependencies_DoesNotHang(string step)
    {
        // Arrange - Recreate scenario similar to the issue where a compute resource references a KeyVault secret
        // This tests that Bicep resources properly depend on referenced Azure resources to avoid hangs during deployment
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["planId"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/serverfarms/testplan" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID"] = new { type = "String", value = "test-client-id" }
                },
                string name when name.StartsWith("kv") => new Dictionary<string, object>
                {
                    ["vaultUri"] = new { type = "String", value = "https://testkv.vault.azure.net/" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter, containerRuntime: fakeContainerRuntime);

        // Set up the scenario from the issue: AppService environment with a compute resource that references a KeyVault secret
        builder.AddAzureAppServiceEnvironment("env");

        var keyVault = builder.AddAzureKeyVault("kv");
        var secret = keyVault.GetSecret("test-secret");

        // Add a compute resource that references the KeyVault secret
        // This creates a dependency: api -> secret -> keyVault
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithEnvironment("SECRET_VALUE", secret);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();

        if (step == "diagnostics")
        {
            // In diagnostics mode, verify the deployment graph shows correct dependencies
            var logs = mockActivityReporter.LoggedMessages
                            .Where(s => s.StepTitle == "diagnostics")
                            .Select(s => s.Message)
                            .ToList();

            // Verify that diagnostics complete without hanging (test will timeout if there's a hang)
            Assert.NotEmpty(logs);

            // Use Verify to snapshot the diagnostic output showing the dependency graph
            await Verify(logs);
            return;
        }

        // In deploy mode, verify that deployment completes without hanging
        // The key verification is that the provision-api-website step depends on provision-kv
        // which is shown in the diagnostics output above (line 101 in the snapshot)
        // Just verify the test completed without timing out, which proves no hang occurred
    }

    [Fact]
    public async Task DeployAsync_WithRedisAccessKeyAuthentication_CreatesCorrectDependencies()
    {
        // Arrange - Test that Redis with AccessKeyAuthentication creates proper dependencies
        // This recreates the scenario from issue #12801 where Redis writes a secret to KeyVault
        // and a website references that secret
        var mockProcessRunner = new MockProcessRunner();
        var fakeContainerRuntime = new FakeContainerRuntime();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "diagnostics");
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);
        var armClientProvider = new TestArmClientProvider(deploymentName =>
        {
            return deploymentName switch
            {
                string name when name.StartsWith("env") => new Dictionary<string, object>
                {
                    ["AZURE_CONTAINER_REGISTRY_NAME"] = new { type = "String", value = "testregistry" },
                    ["AZURE_CONTAINER_REGISTRY_ENDPOINT"] = new { type = "String", value = "testregistry.azurecr.io" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_ID"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.ManagedIdentity/userAssignedIdentities/test-identity" },
                    ["planId"] = new { type = "String", value = "/subscriptions/test/resourceGroups/test-rg/providers/Microsoft.Web/serverfarms/testplan" },
                    ["AZURE_CONTAINER_REGISTRY_MANAGED_IDENTITY_CLIENT_ID"] = new { type = "String", value = "test-client-id" }
                },
                string name when name.StartsWith("kv") => new Dictionary<string, object>
                {
                    ["vaultUri"] = new { type = "String", value = "https://testkv.vault.azure.net/" }
                },
                string name when name.StartsWith("cache") => new Dictionary<string, object>
                {
                    ["hostName"] = new { type = "String", value = "testcache.redis.cache.windows.net" }
                },
                _ => []
            };
        });
        ConfigureTestServices(builder, armClientProvider: armClientProvider, processRunner: mockProcessRunner, activityReporter: mockActivityReporter, containerRuntime: fakeContainerRuntime);

        // Set up the scenario: AppService environment with Redis using access key authentication
        // and a compute resource that references the Redis cache
        builder.AddAzureAppServiceEnvironment("env");

        var cache = builder.AddAzureManagedRedis("cache")
            .WithAccessKeyAuthentication();

        var azpg = builder.AddAzurePostgresFlexibleServer("pg")
                          .WithPasswordAuthentication()
                          .AddDatabase("db");

        var cosmos = builder.AddAzureCosmosDB("cosmos")
                            .WithAccessKeyAuthentication()
                            .AddCosmosDatabase("cdb");

        // Add a compute resource that references the Redis cache
        // This creates dependencies: api -> cache secret -> keyVault
        // The cache secret is owned by cache, so api should depend on cache being fully provisioned
        builder.AddProject<Project>("api", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithReference(cache)
            .WithReference(azpg)
            .WithReference(cosmos);

        // Act
        using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();

        // In diagnostics mode, verify the deployment graph shows correct dependencies
        var logs = mockActivityReporter.LoggedMessages
                        .Where(s => s.StepTitle == "diagnostics")
                        .Select(s => s.Message)
                        .ToList();

        // Verify that diagnostics complete without hanging (test will timeout if there's a hang)
        Assert.NotEmpty(logs);

        // Use Verify to snapshot the diagnostic output showing the dependency graph
        // The key assertion is that provision-api-website depends on provision-cache
        // because the Redis resource writes the secret that the API consumes
        await Verify(logs);
    }

    [Fact]
    public async Task DeployAsync_WithAzureResourcesAndNoEnvironment_Fails()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: WellKnownPipelineSteps.Deploy);
        var mockActivityReporter = new TestPipelineActivityReporter(testOutputHelper);

        ConfigureTestServices(builder, bicepProvisioner: new NoOpBicepProvisioner(), activityReporter: mockActivityReporter);
        builder.Services.AddSingleton<IBicepProvisioner, BicepProvisioner>();
        builder.Services.AddSingleton(_ => ProvisioningTestHelpers.CreateBicepCompiler());

        builder.AddAzureSqlServer("sql").AddDatabase("db");

        using var app = builder.Build();
        await app.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var logs = mockActivityReporter.LoggedMessages
                .Where(m => m.LogLevel >= LogLevel.Error)
                .Select(s => s.Message)
                .ToList();

        await Verify(logs);
    }

    private void ConfigureTestServices(IDistributedApplicationTestingBuilder builder,
        IInteractionService? interactionService = null,
        IBicepProvisioner? bicepProvisioner = null,
        IArmClientProvider? armClientProvider = null,
        MockProcessRunner? processRunner = null,
        IPipelineActivityReporter? activityReporter = null,
        IContainerRuntime? containerRuntime = null,
        bool setDefaultProvisioningOptions = true)
    {
        var options = setDefaultProvisioningOptions ? ProvisioningTestHelpers.CreateOptions() : ProvisioningTestHelpers.CreateOptions(null, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();

        builder.WithTestAndResourceLogging(testOutputHelper);

        armClientProvider ??= ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        builder.Services.AddSingleton(armClientProvider);
        builder.Services.AddSingleton(userPrincipalProvider);
        builder.Services.AddSingleton(tokenCredentialProvider);
        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton(options);
        if (interactionService is not null)
        {
            builder.Services.AddSingleton(interactionService);
        }
        if (activityReporter is not null)
        {
            builder.Services.AddSingleton(activityReporter);
        }
        builder.Services.AddSingleton<IProvisioningContextProvider, PublishModeProvisioningContextProvider>();
        builder.Services.AddSingleton<IDeploymentStateManager, NoOpDeploymentStateManager>();
        if (bicepProvisioner is not null)
        {
            builder.Services.AddSingleton(bicepProvisioner);
        }
        builder.Services.AddSingleton<IProcessRunner>(processRunner ?? new MockProcessRunner());
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IContainerRuntime>(containerRuntime ?? new FakeContainerRuntime());
        builder.Services.AddSingleton<IAcrLoginService>(sp => new FakeAcrLoginService(sp.GetRequiredService<IContainerRuntime>()));
    }

    private sealed class NoOpDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploymentStateSection(sectionName, [], 0));

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpBicepProvisioner : IBicepProvisioner
    {
        public Task<bool> ConfigureResourceAsync(IConfiguration configuration, AzureBicepResource resource, CancellationToken cancellationToken)
        {
            return Task.FromResult(true);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }

    private sealed class TestFunctionsProject : IProjectMetadata
    {
        public string ProjectPath => "functions-project";

        public LaunchSettings LaunchSettings => new()
        {
            Profiles = new Dictionary<string, LaunchProfile>
            {
                ["funcapp"] = new()
                {
                    CommandLineArgs = "--port 7071",
                    LaunchBrowser = false,
                }
            }
        };
    }

    [Fact]
    public async Task DeployAsync_FirstDeployment_SavesStateToFile()
    {
        var appHostSha = "testsha1first";

        using var builder = TestDistributedApplicationBuilder.Create(
            $"AppHost:Operation=publish",
            $"Pipeline:OutputPath=./",
            $"Pipeline:Step=deploy",
            $"AppHostSha={appHostSha}");

        ConfigureTestServicesWithFileDeploymentStateManager(builder, bicepProvisioner: new NoOpBicepProvisioner());

        builder.AddAzureEnvironment();

        using var app = builder.Build();

        var deploymentStateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var deploymentStatePath = deploymentStateManager.StateFilePath;
        Assert.NotNull(deploymentStatePath);

        if (File.Exists(deploymentStatePath))
        {
            File.Delete(deploymentStatePath);
        }

        await app.StartAsync();
        await app.WaitForShutdownAsync();

        Assert.True(File.Exists(deploymentStatePath));
        var stateContent = await File.ReadAllTextAsync(deploymentStatePath);
        var stateJson = JsonNode.Parse(stateContent);
        Assert.NotNull(stateJson);
        Assert.True(stateJson.AsObject().ContainsKey("Azure:SubscriptionId"));

        if (File.Exists(deploymentStatePath))
        {
            File.Delete(deploymentStatePath);
        }
    }

    [Fact]
    public async Task DeployAsync_WithCachedDeploymentState_LoadsFromCache()
    {
        var appHostSha = "testsha2cache";
        var deploymentStatePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspire",
            "deployments",
            appHostSha,
            $"production.json"
        );
        Directory.CreateDirectory(Path.GetDirectoryName(deploymentStatePath)!);
        var cachedState = new JsonObject
        {
            ["Azure:SubscriptionId"] = "cached-sub-12345678-1234-1234-1234-123456789012",
            ["Azure:Location"] = "westus2",
            ["Azure:ResourceGroup"] = "cached-rg-test"
        };
        await File.WriteAllTextAsync(deploymentStatePath, cachedState.ToJsonString());

        using var builder = TestDistributedApplicationBuilder.Create(
            $"AppHost:Operation=publish",
            $"Pipeline:OutputPath=./",
            $"Pipeline:Step=deploy",
            $"AppHostSha={appHostSha}");

        ConfigureTestServicesWithFileDeploymentStateManager(builder, bicepProvisioner: new NoOpBicepProvisioner());
        using var app = builder.Build();

        // Verify that the cached state was loaded into configuration
        Assert.Equal("cached-sub-12345678-1234-1234-1234-123456789012", builder.Configuration["Azure:SubscriptionId"]);
        Assert.Equal("westus2", builder.Configuration["Azure:Location"]);
        Assert.Equal("cached-rg-test", builder.Configuration["Azure:ResourceGroup"]);

        builder.AddAzureEnvironment();

        await app.StartAsync();
        await app.WaitForShutdownAsync();

        // Verify that the state file still exists after deployment
        Assert.True(File.Exists(deploymentStatePath));

        if (File.Exists(deploymentStatePath))
        {
            File.Delete(deploymentStatePath);
        }
    }

    [Fact]
    public async Task DeployAsync_WithClearCacheFlag_DoesNotSaveState()
    {
        var appHostSha = "testsha3clear";

        using var builder = TestDistributedApplicationBuilder.Create(
            $"AppHost:Operation=publish",
            $"Pipeline:OutputPath=./",
            $"Pipeline:ClearCache=true",
            $"AppHostSha={appHostSha}");

        ConfigureTestServicesWithFileDeploymentStateManager(builder, bicepProvisioner: new NoOpBicepProvisioner());

        builder.AddAzureEnvironment();

        using var app = builder.Build();

        var deploymentStateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var deploymentStatePath = deploymentStateManager.StateFilePath;
        Assert.NotNull(deploymentStatePath);

        if (File.Exists(deploymentStatePath))
        {
            File.Delete(deploymentStatePath);
        }

        await app.StartAsync();
        await app.WaitForShutdownAsync();

        Assert.False(File.Exists(deploymentStatePath));
    }

    [Fact]
    public async Task DeployAsync_WithStagingEnvironment_UsesStagingStateFile()
    {
        var appHostSha = "testsha4stage";

        using var builder = TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish",
            $"Pipeline:OutputPath=./",
            $"Pipeline:Step=deploy",
            $"AppHostSha={appHostSha}");

        ConfigureTestServicesWithFileDeploymentStateManager(builder, bicepProvisioner: new NoOpBicepProvisioner(), environmentName: "Staging");

        builder.AddAzureEnvironment();

        using var app = builder.Build();

        var deploymentStateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
        var stagingStatePath = deploymentStateManager.StateFilePath;
        Assert.NotNull(stagingStatePath);
        Assert.EndsWith("staging.json", stagingStatePath);

        if (File.Exists(stagingStatePath))
        {
            File.Delete(stagingStatePath);
        }

        await app.StartAsync();
        await app.WaitForShutdownAsync();

        Assert.True(File.Exists(stagingStatePath));
        var stateContent = await File.ReadAllTextAsync(stagingStatePath);
        var stateJson = JsonNode.Parse(stateContent);
        Assert.NotNull(stateJson);

        if (File.Exists(stagingStatePath))
        {
            File.Delete(stagingStatePath);
        }
    }

    [Fact]
    public async Task DeployAsync_WithSavedParameters_ReloadsAllParameterTypesFromDeploymentState()
    {
        // This test verifies the end-to-end flow for all parameter types:
        // 1. Regular parameters (Parameters:name)
        // 2. Connection strings (ConnectionStrings:name)
        // 3. Custom configuration key parameters (CustomSection:Key)
        // All should be saved to deployment state and reloaded on next deployment

        var appHostSha = Guid.NewGuid().ToString();

        string? firstDeploymentState = null;
        string? secondDeploymentState = null;
        string? deploymentStatePath = null;

        // ===== First deployment - prompt and save =====
        using (var builder = TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish",
            "Pipeline:OutputPath=./",
            "Pipeline:Step=deploy",
            $"AppHostSha={appHostSha}"))
        {
            var testInteractionService = new TestInteractionService();
            ConfigureTestServicesWithFileDeploymentStateManager(
                builder,
                bicepProvisioner: new NoOpBicepProvisioner(),
                environmentName: "Production");
            builder.Services.AddSingleton<IInteractionService>(testInteractionService);

            // Add all three types of parameters
            var regularParam = builder.AddParameter("api-key");
            var connectionStringParam = builder.AddConnectionString("mydb");
            var customKeyParam = builder.AddParameterFromConfiguration("custom-setting", "MyApp:Setting");

            builder.AddAzureEnvironment();

            using var app = builder.Build();

            // Get the actual deployment state file path
            var deploymentStateManager = app.Services.GetRequiredService<IDeploymentStateManager>();
            deploymentStatePath = deploymentStateManager.StateFilePath;
            Assert.NotNull(deploymentStatePath);
            Assert.False(File.Exists(deploymentStatePath));

            var runTask = Task.Run(app.Run);

            // Wait for parameter prompting
            var parameterInputs = await testInteractionService.Interactions.Reader.ReadAsync();
            Assert.Equal("Set unresolved parameters", parameterInputs.Title);

            // Verify all three parameters are prompted
            Assert.Equal(3, parameterInputs.Inputs.Count);

            // Provide values for all parameters
            var apiKeyInput = parameterInputs.Inputs["api-key"];
            var dbInput = parameterInputs.Inputs["mydb"];
            var customInput = parameterInputs.Inputs["custom-setting"];

            apiKeyInput.Value = "secret-key-12345";
            dbInput.Value = "Server=localhost;Database=mydb";
            customInput.Value = "custom-value-xyz";

            parameterInputs.CompletionTcs.SetResult(InteractionResult.Ok(parameterInputs.Inputs));

            await runTask.WaitAsync(TimeSpan.FromSeconds(10));

            // Verify parameter values were set correctly
            Assert.Equal("secret-key-12345", await regularParam.Resource.GetValueAsync(default));
            Assert.Equal("Server=localhost;Database=mydb", await connectionStringParam.Resource.GetValueAsync(default));
            Assert.Equal("custom-value-xyz", await customKeyParam.Resource.GetValueAsync(default));

            // Capture the state file contents after first deployment
            Assert.True(File.Exists(deploymentStatePath));
            firstDeploymentState = await File.ReadAllTextAsync(deploymentStatePath);

            // Verify the state after first deployment to ensure parameters are saved with correct keys
            await Verify(firstDeploymentState)
                .UseMethodName($"{nameof(DeployAsync_WithSavedParameters_ReloadsAllParameterTypesFromDeploymentState)}_FirstDeployment");
        }

        // ===== Second deployment - should load from state without prompting =====
        using (var builder = TestDistributedApplicationBuilder.Create(
            "AppHost:Operation=publish",
            "Pipeline:OutputPath=./",
            "Pipeline:Step=deploy",
            $"AppHostSha={appHostSha}"))
        {
            var testInteractionService = new TestInteractionService();
            ConfigureTestServicesWithFileDeploymentStateManager(
                builder,
                bicepProvisioner: new NoOpBicepProvisioner(),
                environmentName: "Production");
            builder.Services.AddSingleton<IInteractionService>(testInteractionService);

            // Add the same parameters
            var regularParam = builder.AddParameter("api-key");
            var connectionStringParam = builder.AddConnectionString("mydb");
            var customKeyParam = builder.AddParameterFromConfiguration("custom-setting", "MyApp:Setting");

            builder.AddAzureEnvironment();

            using var app = builder.Build();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var runTask = Task.Run(() => app.RunAsync(cts.Token));

            // Give the deployment time to complete
            await Task.Delay(TimeSpan.FromSeconds(1));

            // CRITICAL: Verify NO prompting occurred because all values were loaded from state
            // This is the key assertion - if the JsonFlattener was broken, keys would be saved as "Parameters:api-key:"
            // and the configuration system wouldn't find them, causing prompts on the second deployment
            if (testInteractionService.Interactions.Reader.Count > 0)
            {
                // Debug: See what's being prompted
                var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
                var parameterNames = string.Join(", ", interaction.Inputs.Select(i => i.Label));
                Assert.Fail($"Expected no prompting but got {interaction.Inputs.Count} parameter(s): {parameterNames}");
            }
            Assert.Equal(0, testInteractionService.Interactions.Reader.Count);

            // Verify all parameter values are accessible at runtime (loaded from state)
            Assert.Equal("secret-key-12345", await regularParam.Resource.GetValueAsync(default));
            Assert.Equal("Server=localhost;Database=mydb", await connectionStringParam.Resource.GetValueAsync(default));
            Assert.Equal("custom-value-xyz", await customKeyParam.Resource.GetValueAsync(default));

            // Capture the state file contents after second deployment
            Assert.True(File.Exists(deploymentStatePath));
            secondDeploymentState = await File.ReadAllTextAsync(deploymentStatePath);
        }

        // Verify both deployment states using snapshots
        await Verify(new
        {
            FirstDeploymentState = firstDeploymentState,
            SecondDeploymentState = secondDeploymentState
        });

        // Clean up
        if (deploymentStatePath is not null && File.Exists(deploymentStatePath))
        {
            File.Delete(deploymentStatePath);
        }
    }

    private static void ConfigureTestServicesWithFileDeploymentStateManager(
        IDistributedApplicationTestingBuilder builder,
        IBicepProvisioner? bicepProvisioner = null,
        string? environmentName = null)
    {
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = new TestHostEnvironment
        {
            ApplicationName = "TestApp",
            EnvironmentName = environmentName ?? "Test"
        };
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();

        builder.Services.AddSingleton<IHostEnvironment>(environment);
        builder.Services.AddSingleton(armClientProvider);
        builder.Services.AddSingleton(userPrincipalProvider);
        builder.Services.AddSingleton(tokenCredentialProvider);
        builder.Services.AddSingleton(logger);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IProvisioningContextProvider, PublishModeProvisioningContextProvider>();

        if (bicepProvisioner is not null)
        {
            builder.Services.AddSingleton(bicepProvisioner);
        }

        builder.Services.AddSingleton<IProcessRunner>(new MockProcessRunner());
        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();
        builder.Services.AddSingleton<IContainerRuntime>(new FakeContainerRuntime());
        builder.Services.AddSingleton<IAcrLoginService>(sp => new FakeAcrLoginService(sp.GetRequiredService<IContainerRuntime>()));
    }
}
