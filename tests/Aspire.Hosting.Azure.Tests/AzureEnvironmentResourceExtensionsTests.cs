#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests;
using Azure;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEnvironmentResourceExtensionsTests
{
    [Fact]
    public void AddAzureEnvironment_ShouldAddResourceToBuilder_InPublishMode()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var resourceBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        // Assert that default Location and ResourceGroup parameters are set
        Assert.NotNull(environmentResource.Location);
        Assert.NotNull(environmentResource.ResourceGroupName);
        // Assert that the parameters are not added to the resource model
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public void AddAzureEnvironment_CalledMultipleTimes_ReturnsSameResource()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var firstBuilder = builder.AddAzureEnvironment();
        var secondBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.Same(firstBuilder.Resource, secondBuilder.Resource);
        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsControlResourceWithResetCommand()
    {
        var builder = CreateBuilder(isRunMode: true);

        var resourceBuilder = builder.AddAzureEnvironment();

        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);
        Assert.Equal("Reset provisioning state", resetCommand.DisplayName);
        Assert.Contains("not delete live Azure resources", resetCommand.DisplayDescription);
        Assert.Contains("may be left orphaned", resetCommand.ConfirmationMessage);
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsCommandsInDefinitionOrder()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.AddAzureEnvironment();

        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var commands = environmentResource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();

        Assert.Collection(commands,
            command =>
            {
                Assert.Equal(AzureProvisioningController.ResetProvisioningStateCommandName, command.Name);
                Assert.True(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.ChangeAzureContextCommandName, command.Name);
                Assert.True(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.ReprovisionAllCommandName, command.Name);
                Assert.False(command.IsHighlighted);
            },
            command =>
            {
                Assert.Equal(AzureProvisioningController.DeleteAzureResourcesCommandName, command.Name);
                Assert.False(command.IsHighlighted);
            });
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsSelectableArgumentsToChangeAzureContextCommand()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.AddAzureEnvironment();

        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        Assert.NotNull(changeContextCommand.ValidateArguments);
        Assert.Collection(changeContextCommand.Arguments,
            input =>
            {
                Assert.Equal("tenantId", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.True(input.Disabled);
                Assert.NotNull(input.DynamicLoading);
                Assert.True(input.DynamicLoading.AlwaysLoadOnStart);
            },
            input =>
            {
                Assert.Equal("subscriptionId", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.True(input.Disabled);
                Assert.NotNull(input.DynamicLoading);
                Assert.True(input.DynamicLoading.AlwaysLoadOnStart);
                Assert.Equal(["tenantId"], input.DynamicLoading.DependsOnInputs);
            },
            input =>
            {
                Assert.Equal("resourceGroup", input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.True(input.Disabled);
                Assert.NotNull(input.DynamicLoading);
                Assert.True(input.DynamicLoading.AlwaysLoadOnStart);
                Assert.Equal(["subscriptionId"], input.DynamicLoading.DependsOnInputs);
            },
            input =>
            {
                Assert.Equal(AzureBicepResource.KnownParameters.Location, input.Name);
                Assert.True(input.Required);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.AllowCustomChoice);
                Assert.True(input.Disabled);
                Assert.NotNull(input.DynamicLoading);
                Assert.True(input.DynamicLoading.AlwaysLoadOnStart);
                Assert.Equal(["subscriptionId", "resourceGroup"], input.DynamicLoading.DependsOnInputs);
            });
    }

    [Fact]
    public async Task ChangeAzureContextCommand_DynamicArgumentsLoadAzureContextOptions()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.Configuration["Azure:TenantId"] = "87654321-4321-4321-4321-210987654321";
        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:ResourceGroup"] = "rg-test-2";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);
        var inputs = CloneInputs(changeContextCommand.Arguments);

        var tenantInput = inputs["tenantId"];
        Assert.True(tenantInput.Disabled);

        await LoadInputAsync(app.Services, inputs, tenantInput);

        Assert.False(tenantInput.Disabled);
        Assert.Contains(tenantInput.Options!, option => option.Key == "87654321-4321-4321-4321-210987654321");
        Assert.Equal("87654321-4321-4321-4321-210987654321", tenantInput.Value);

        var subscriptionInput = inputs["subscriptionId"];
        Assert.True(subscriptionInput.Disabled);
        Assert.True(subscriptionInput.DynamicLoading!.AlwaysLoadOnStart);

        await LoadInputAsync(app.Services, inputs, subscriptionInput);

        Assert.False(subscriptionInput.Disabled);
        Assert.Contains(subscriptionInput.Options!, option => option.Key == "12345678-1234-1234-1234-123456789012");
        Assert.Equal("12345678-1234-1234-1234-123456789012", subscriptionInput.Value);

        var resourceGroupInput = inputs["resourceGroup"];
        Assert.True(resourceGroupInput.Disabled);

        await LoadInputAsync(app.Services, inputs, resourceGroupInput);

        Assert.False(resourceGroupInput.Disabled);
        Assert.Contains(resourceGroupInput.Options!, option => option.Key == "rg-test-2");
        Assert.Equal("rg-test-2", resourceGroupInput.Value);

        var locationInput = inputs[AzureBicepResource.KnownParameters.Location];
        Assert.True(locationInput.Disabled);

        await LoadInputAsync(app.Services, inputs, locationInput);

        Assert.True(locationInput.Disabled);
        Assert.Equal("westus", locationInput.Value);
        Assert.Equal([KeyValuePair.Create("westus", "westus")], locationInput.Options);
    }

    [Fact]
    public async Task ChangeAzureContextCommand_TenantChangeReloadsSubscriptionOptionsForSelectedTenant()
    {
        var builder = CreateBuilder(isRunMode: true);
        var armClient = new AzureContextOptionsArmClient();

        builder.Configuration["Azure:TenantId"] = AzureContextOptionsArmClient.FirstTenantId;
        builder.Configuration["Azure:SubscriptionId"] = AzureContextOptionsArmClient.FirstSubscriptionId;
        builder.Configuration["Azure:ResourceGroup"] = "rg-first";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder, armClientProvider: new AzureContextOptionsArmClientProvider(armClient));

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);
        var inputs = CloneInputs(changeContextCommand.Arguments);

        var tenantInput = inputs["tenantId"];
        await LoadInputAsync(app.Services, inputs, tenantInput);

        Assert.Contains(tenantInput.Options!, option => option.Key == AzureContextOptionsArmClient.SecondTenantId);
        Assert.Equal(AzureContextOptionsArmClient.FirstTenantId, tenantInput.Value);

        tenantInput.Value = AzureContextOptionsArmClient.SecondTenantId;
        var subscriptionInput = inputs["subscriptionId"];
        subscriptionInput.Disabled = true;

        await LoadInputAsync(app.Services, inputs, subscriptionInput);

        Assert.False(subscriptionInput.Disabled);
        Assert.Equal(AzureContextOptionsArmClient.SecondTenantId, armClient.LastSubscriptionTenantId);
        Assert.DoesNotContain(subscriptionInput.Options!, option => option.Key == AzureContextOptionsArmClient.FirstSubscriptionId);
        Assert.Contains(subscriptionInput.Options!, option => option.Key == AzureContextOptionsArmClient.SecondSubscriptionId);

        subscriptionInput.Value = AzureContextOptionsArmClient.SecondSubscriptionId;
        var resourceGroupInput = inputs["resourceGroup"];

        await LoadInputAsync(app.Services, inputs, resourceGroupInput);

        Assert.Equal(AzureContextOptionsArmClient.SecondSubscriptionId, armClient.LastResourceGroupSubscriptionId);
        Assert.Contains(resourceGroupInput.Options!, option => option.Key == "rg-second");
    }

    [Fact]
    public async Task ChangeAzureContextCommand_CustomResourceGroupEnablesLocationChoices()
    {
        var builder = CreateBuilder(isRunMode: true);

        builder.Configuration["Azure:TenantId"] = "87654321-4321-4321-4321-210987654321";
        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:ResourceGroup"] = "rg-new";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);
        var inputs = CloneInputs(changeContextCommand.Arguments);
        var subscriptionInput = inputs["subscriptionId"];
        subscriptionInput.Value = "12345678-1234-1234-1234-123456789012";
        var resourceGroupInput = inputs["resourceGroup"];
        resourceGroupInput.Value = "rg-new";
        var locationInput = inputs[AzureBicepResource.KnownParameters.Location];
        locationInput.Disabled = true;

        await LoadInputAsync(app.Services, inputs, locationInput);

        Assert.False(locationInput.Disabled);
        Assert.Contains(locationInput.Options!, option => option.Key == "eastus");
        Assert.Contains(locationInput.Options!, option => option.Key == "westus2");
        Assert.Equal("eastus", locationInput.Value);
    }

    [Fact]
    public async Task ResetProvisioningStateCommand_ClearsCachedStateAndResetsSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);
        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "sub";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Resources/deployments/storage";
        storageSection.Data["CheckSum"] = "checksum";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(environmentResource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties =
            [
                new("azure.subscription.id", "sub")
            ]
        });

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls =
            [
                new("deployment", "https://portal.azure.com", false)
            ],
            Properties =
            [
                new("azure.subscription.id", "sub"),
                new(CustomResourceKnownProperties.Source, "deployment-id"),
                new("custom.property", "keep")
            ]
        });

        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);

        var result = await resetCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure provisioning state reset.", result.Message);
        var resultData = AssertCommandJsonData(result);
        Assert.Contains("orphaned", resultData["warning"]?.GetValue<string>(), StringComparison.Ordinal);
        var recommendedActions = Assert.IsType<JsonArray>(resultData["recommendedActions"]);
        Assert.Contains(recommendedActions, action => action?["code"]?.GetValue<string>() == "delete-live-resources");

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Empty(azureSection.Data);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        Assert.Empty(storage.Resource.Outputs);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.NotStarted, environmentEvent.Snapshot.State?.Text);
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Enabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);
        Assert.Empty(storageEvent.Snapshot.Urls);
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == "azure.subscription.id");
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
        Assert.Contains(storageEvent.Snapshot.Properties, p => p.Name == "custom.property");
    }

    [Fact]
    public async Task ResetProvisioningStateCommand_ReentersProvisioningAndPromptsWhenAzureConfigMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);

        var commandTask = resetCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var result = await commandTask.WaitAsync(s_testSynchronizationTimeout);

        Assert.False(result.Success);
        Assert.True(result.Canceled);

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(s_testSynchronizationTimeout);

        Assert.Equal(AzureProvisioningStrings.NotificationTitle, interaction.Title);
        Assert.Equal(AzureProvisioningStrings.NotificationMessage, interaction.Message);
        var options = Assert.IsType<NotificationInteractionOptions>(interaction.Options);
        Assert.Equal(MessageIntent.Warning, options.Intent);
        Assert.Equal(AzureProvisioningStrings.NotificationPrimaryButtonText, options.PrimaryButtonText);
    }

    [Fact]
    public async Task MissingAzureContextNotification_ReappearsWhenConfigureDialogIsCanceled()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        var notification = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(s_testSynchronizationTimeout);

        Assert.Equal(AzureProvisioningStrings.NotificationTitle, notification.Title);
        Assert.Equal(AzureProvisioningStrings.NotificationMessage, notification.Message);

        var notificationOptions = Assert.IsType<NotificationInteractionOptions>(notification.Options);
        Assert.Equal(MessageIntent.Warning, notificationOptions.Intent);
        Assert.Equal(AzureProvisioningStrings.NotificationPrimaryButtonText, notificationOptions.PrimaryButtonText);

        notification.CompletionTcs.SetResult(InteractionResult.Ok(true));

        var inputs = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(s_testSynchronizationTimeout);

        Assert.Equal(AzureProvisioningStrings.InputsTitle, inputs.Title);
        Assert.Equal(AzureProvisioningStrings.InputsMessage, inputs.Message);
        var inputOptions = Assert.IsType<InputsDialogInteractionOptions>(inputs.Options);
        Assert.True(inputOptions.EnableMessageMarkdown);
        Assert.Equal(AzureProvisioningStrings.InputsPrimaryButtonText, inputOptions.PrimaryButtonText);
        Assert.Equal(AzureProvisioningStrings.InputsSecondaryButtonText, inputOptions.SecondaryButtonText);

        inputs.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());

        var repeatedNotification = await testInteractionService.Interactions.Reader.ReadAsync().AsTask().WaitAsync(s_testSynchronizationTimeout);

        Assert.Equal(AzureProvisioningStrings.NotificationTitle, repeatedNotification.Title);
        Assert.Equal(AzureProvisioningStrings.NotificationMessage, repeatedNotification.Message);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_UsesControllerProvisioningFlow()
    {
        var builder = CreateBuilder(isRunMode: true);
        AddTestAzureProvisioning(builder);

        var storage = builder.AddAzureStorage("storage");

        using var app = builder.Build();

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        await controller.EnsureProvisionedAsync(model);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Running", storageEvent.Snapshot.State?.Text);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task EnsureProvisionedAsync_CompletesExistingPendingProvisioningWaiters()
    {
        var builder = CreateBuilder(isRunMode: true);
        var cachedStateProvisioner = new CachedStateTestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: cachedStateProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var existingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Resource.ProvisioningTaskCompletionSource = existingTcs;
        var outputTask = storage.GetOutput("blobEndpoint").GetValueAsync(CancellationToken.None).AsTask();

        Assert.False(outputTask.IsCompleted);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await controller.EnsureProvisionedAsync(model);

        Assert.Same(existingTcs, storage.Resource.ProvisioningTaskCompletionSource);
        Assert.True(existingTcs.Task.IsCompletedSuccessfully);
        Assert.Equal("https://storage.blob.core.windows.net/", await outputTask.WaitAsync(s_testSynchronizationTimeout));
    }

    [Fact]
    public async Task RunModeInitializeResource_ProvisionsAzureResourcesAfterPrepareStep()
    {
        var builder = CreateBuilder(isRunMode: true);
        AddTestAzureProvisioning(builder);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var (steps, pipelineContext) = await CreateAzureEnvironmentPipelineStepsAsync(environmentResource, model, app.Services);
        var prepareResourcesStep = Assert.Single(steps, step => step.Name == AzureEnvironmentResource.PrepareResourcesStepName);
        var beforeStartSteps = steps.Where(step => step.RequiredBySteps.Contains(WellKnownPipelineSteps.BeforeStart)).ToArray();

        Assert.Collection(beforeStartSteps, step => Assert.Equal(AzureEnvironmentResource.PrepareResourcesStepName, step.Name));
        Assert.DoesNotContain(WellKnownPipelineSteps.BeforeStart, steps.Single(step => step.Name == AzureEnvironmentResource.ProvisionInfrastructureStepName).RequiredBySteps);

        await using var reportingStep = await new NullPublishingActivityReporter().CreateStepAsync("test");
        var stepContext = new PipelineStepContext
        {
            PipelineContext = pipelineContext,
            ReportingStep = reportingStep
        };

        await prepareResourcesStep.Action(stepContext);

        Assert.Empty(storage.Resource.Outputs);

        var eventing = app.Services.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new InitializeResourceEvent(
            environmentResource,
            eventing,
            app.Services.GetRequiredService<ResourceLoggerService>(),
            notifications,
            app.Services));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.Running, storageEvent.Snapshot.State?.Text);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task EnsureProvisioned_UsesCachedStateWhenMissingResourceProbeCannotAuthenticate()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var cachedStateProvisioner = new CachedStateTestBicepProvisioner();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder, armClientProvider: new CredentialUnavailableArmClientProvider(), bicepProvisioner: cachedStateProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """
            {
              // Cached output IDs are used to decide whether provisioning can be skipped.
              "id": {
                "value": "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage",
              },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        Assert.Equal(1, cachedStateProvisioner.ConfigureResourceCallCount);
        Assert.Equal(0, cachedStateProvisioner.GetOrCreateResourceCallCount);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task EnsureProvisioned_UsesCachedStateWhenMissingResourceProbeFailsTransiently()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var cachedStateProvisioner = new CachedStateTestBicepProvisioner();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder, armClientProvider: new ThrowingResourceProbeArmClientProvider(new RequestFailedException(503, "Service unavailable.")), bicepProvisioner: cachedStateProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/storage"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        Assert.Equal(1, cachedStateProvisioner.ConfigureResourceCallCount);
        Assert.Equal(0, cachedStateProvisioner.GetOrCreateResourceCallCount);
        Assert.Equal("https://storage.blob.core.windows.net/", storage.Resource.Outputs["blobEndpoint"]);
    }

    [Fact]
    public async Task OnBeforeStartAsync_AddsPerResourceCommandsToDeployableAzureResourcesOnly()
    {
        var builder = CreateBuilder(isRunMode: true);
        AddTestAzureProvisioning(builder);

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ForgetStateCommandName);
        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);
        var locationArgument = Assert.Single(changeLocationCommand.Arguments);
        Assert.Equal(AzureBicepResource.KnownParameters.Location, locationArgument.Name);
        Assert.True(locationArgument.Required);
        Assert.True(locationArgument.Disabled);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourceCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        Assert.DoesNotContain(blobs.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c =>
            c.Name == AzureProvisioningController.ForgetStateCommandName ||
            c.Name == AzureProvisioningController.CancelDeploymentCommandName ||
            c.Name == AzureProvisioningController.DeleteAzureResourceCommandName ||
            c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsCachedDeploymentStateAndLiveStatus()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string tenantId = "87654321-4321-4321-4321-210987654321";
        const string resourceGroup = "test-rg";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/storage";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([resourceId]), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = subscriptionId;
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = resourceGroup;
        azureSection.Data["TenantId"] = tenantId;
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data["Parameters"] = """
            {
              // Cached deployment state can be hand-edited while recovering local state.
              "location": { "value": "westus2", },
            }
            """;
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "type": "String",
                "value": "{{resourceId}}",
              },
              "blobEndpoint": {
                "type": "String",
                "value": "https://storage.blob.core.windows.net/",
              },
            }
            """;
        storageSection.Data["Scope"] = $$"""
            {
              "resourceGroup": "{{resourceGroup}}",
            }
            """;
        storageSection.Data["CheckSum"] = "checksum";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource information retrieved.", result.Message);

        var data = AssertCommandJsonData(result);
        Assert.Equal(AzureProvisioningController.GetAzureResourceCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("storage", data["resourceName"]?.GetValue<string>());
        Assert.Equal("westus2", data["azureLocation"]?.GetValue<string>());
        Assert.Equal("westus3", data["location"]?.GetValue<string>());
        Assert.Equal("westus3", data["effectiveLocation"]?.GetValue<string>());
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.DisplayImmediately);

        var azureContext = Assert.IsType<JsonObject>(data["azureContext"]);
        Assert.Equal(subscriptionId, azureContext["subscriptionId"]?.GetValue<string>());
        Assert.Equal(tenantId, azureContext["tenantId"]?.GetValue<string>());
        Assert.Equal(resourceGroup, azureContext["resourceGroup"]?.GetValue<string>());
        Assert.Equal("westus2", azureContext["location"]?.GetValue<string>());

        var deployment = Assert.IsType<JsonObject>(data["deployment"]);
        Assert.True(deployment["hasState"]?.GetValue<bool>());
        Assert.Equal(deploymentId, deployment["deploymentId"]?.GetValue<string>());
        Assert.Equal(resourceId, deployment["resourceId"]?.GetValue<string>());
        Assert.Equal("westus3", deployment["locationOverride"]?.GetValue<string>());
        Assert.Equal("checksum", deployment["checksum"]?.GetValue<string>());
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateRunning, deployment["provisioningState"]?.GetValue<string>());
        Assert.Contains("/DeploymentDetailsBlade/", deployment["deploymentPortalUrl"]?.GetValue<string>());
        Assert.Contains("/resource/subscriptions/", deployment["resourcePortalUrl"]?.GetValue<string>());

        var outputs = Assert.IsType<JsonObject>(deployment["outputs"]);
        Assert.Equal("https://storage.blob.core.windows.net/", outputs["blobEndpoint"]?["value"]?.GetValue<string>());
        var parameters = Assert.IsType<JsonObject>(deployment["parameters"]);
        Assert.Equal("westus2", parameters["location"]?["value"]?.GetValue<string>());
        var scope = Assert.IsType<JsonObject>(deployment["scope"]);
        Assert.Equal(resourceGroup, scope["resourceGroup"]?.GetValue<string>());

        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.True(live["checked"]?.GetValue<bool>());
        Assert.True(live["exists"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsMissingLiveResourceReasonWhenCachedResourceDoesNotExist()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider(Array.Empty<string>()), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);

        var data = AssertCommandJsonData(result);
        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.True(live["checked"]?.GetValue<bool>());
        Assert.False(live["exists"]?.GetValue<bool>());
        Assert.Equal("missing-live-resource", live["reason"]?.GetValue<string>());
        Assert.Equal("azure", live["source"]?.GetValue<string>());
        Assert.Equal("missing-live-resource", live["code"]?.GetValue<string>());
        Assert.Contains("resource was not found in Azure", live["message"]?.GetValue<string>(), StringComparison.Ordinal);
        var recommendedActions = Assert.IsType<JsonArray>(live["recommendedActions"]);
        Assert.Contains(recommendedActions, action => action?["code"]?.GetValue<string>() == "reprovision-or-forget-state");
    }

    [Theory]
    [InlineData(AzureProvisioningFailureDetails.MissingLiveResourceReason, "reprovision-or-forget-state")]
    [InlineData(AzureProvisioningFailureDetails.ResourceGroupBeingDeletedErrorCode, "change-resource-group")]
    [InlineData(AzureProvisioningFailureDetails.SubscriptionNotFoundErrorCode, "change-subscription")]
    [InlineData(AzureProvisioningFailureDetails.ServiceModelDeprecatedErrorCode, "choose-supported-model-version")]
    [InlineData(AzureProvisioningFailureDetails.InvalidResourcePropertiesErrorCode, "fix-resource-properties")]
    public void AzureProvisioningFailureDetails_ReturnsKnownRecommendedActions(string errorCodeOrReason, string expectedActionCode)
    {
        var actions = AzureProvisioningFailureDetails.GetRecommendedActions(errorCodeOrReason);

        Assert.Contains(actions, action => action.Code == expectedActionCode);
    }

    [Fact]
    public void AzureProvisioningFailureDetails_ParsesDocumentedDeploymentOperationErrorShape()
    {
        const string targetResourceId =
            "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";
        var response = new AzureProvisioningFailureTestResponse(400, "Bad Request",
            $$"""
            {
              "properties": {
                "statusCode": "BadRequest",
                "statusMessage": {
                  "error": {
                    "code": "InvalidAccountType",
                    "message": "The AccountType Standard_LRS1 is invalid. For more information, see - https://aka.ms/storageaccountskus"
                  }
                },
                "targetResource": {
                  "id": "{{targetResourceId}}",
                  "resourceType": "Microsoft.Storage/storageAccounts",
                  "resourceName": "storage"
                }
              }
            }
            """);

        var details = AzureProvisioningFailureDetails.FromRequestFailedException(
            new RequestFailedException(response),
            AzureProvisioningFailureDetails.ProvisionOperation);

        Assert.Equal("Microsoft.Storage", details.Provider);
        Assert.Equal("Microsoft.Storage/storageAccounts", details.ResourceType);
        Assert.Equal("storage", details.ResourceName);
        Assert.Equal(targetResourceId, details.TargetResourceId);
        Assert.Equal("InvalidAccountType", details.ErrorCode);
        Assert.Contains("Standard_LRS1", details.ErrorMessage, StringComparison.Ordinal);

        var json = details.ToJsonObject();
        Assert.Equal("Microsoft.Storage/storageAccounts", json["resourceType"]?.GetValue<string>());
        Assert.Equal("storage", json["resourceName"]?.GetValue<string>());
        Assert.Equal(targetResourceId, json["targetResourceId"]?.GetValue<string>());

        var properties = details.SetResourceProperties([], AzureProvisioningFailureDetails.ProvisionOperation);
        Assert.All(
            properties.Where(property => AzureProvisioningFailureDetails.IsFailureProperty(property.Name)),
            property =>
            {
                Assert.True(property.IsHighlighted);
                Assert.False(string.IsNullOrWhiteSpace(property.DisplayName));
            });

        var resourceNameProperty = properties.Single(property => property.Name == "azure.provisioning.error.resource.name");
        Assert.Equal(
            "storage",
            resourceNameProperty.Value?.ToString());
        Assert.Equal(AzureProvisioningStrings.FailurePropertyResourceNameDisplayName, resourceNameProperty.DisplayName);

        var targetResourceIdProperty = properties.Single(property => property.Name == "azure.provisioning.error.target.resource.id");
        Assert.Equal(
            targetResourceId,
            targetResourceIdProperty.Value?.ToString());
        Assert.Equal(AzureProvisioningStrings.FailurePropertyTargetResourceIdDisplayName, targetResourceIdProperty.DisplayName);
    }

    [Fact]
    public void AzureProvisioningFailureDetails_PromotesNestedProviderErrorAndKeepsTargetResource()
    {
        const string targetResourceId =
            "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/search";
        var response = new AzureProvisioningFailureTestResponse(400, "Bad Request",
            $$"""
            {
              "properties": {
                "targetResource": {
                  "id": "{{targetResourceId}}",
                  "resourceType": "Microsoft.Search/searchServices",
                  "resourceName": "search"
                },
                "statusMessage": {
                  "error": {
                    "code": "DeploymentFailed",
                    "message": "At least one resource deployment operation failed.",
                    "details": [
                      {
                        "code": "ResourceDeploymentFailure",
                        "message": "The resource write operation failed to complete successfully.",
                        "details": [
                          {
                            "code": "LocationNotAvailableForResourceType",
                            "message": "The provided location 'australiacentral' is not available for resource type 'Microsoft.Search/searchServices'.",
                            "target": "search"
                          }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """);

        var details = AzureProvisioningFailureDetails.FromRequestFailedException(
            new RequestFailedException(response),
            AzureProvisioningFailureDetails.ProvisionOperation);

        Assert.Equal("Microsoft.Search", details.Provider);
        Assert.Equal("Microsoft.Search/searchServices", details.ResourceType);
        Assert.Equal("search", details.ResourceName);
        Assert.Equal(targetResourceId, details.TargetResourceId);
        Assert.Equal(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, details.ErrorCode);
        Assert.Contains("australiacentral", details.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(AzureProvisioningFailureDetails.ProvisionOperation, details.Operation);
    }

    [Fact]
    public void AzureProvisioningFailureDetails_PromotesNestedResponseErrorAndKeepsTargetResource()
    {
        const string targetResourceId =
            "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Search/searchServices/search";
        var targetResource = new AzureDeploymentOperationTarget(
            targetResourceId,
            "Microsoft.Search/searchServices",
            "search");
        const string statusMessageContent = """
            {
              "status": "Failed",
              "error": {
                "code": "DeploymentFailed",
                "message": "At least one resource deployment operation failed.",
                "details": [
                  {
                    "code": "ResourceDeploymentFailure",
                    "message": "The resource write operation failed to complete successfully.",
                    "details": [
                      {
                        "code": "LocationNotAvailableForResourceType",
                        "message": "The provided location 'australiacentral' is not available for resource type 'Microsoft.Search/searchServices'.",
                        "target": "search"
                      }
                    ]
                  }
                ]
              }
            }
            """;

        var details = AzureProvisioningFailureDetails.FromResponseError(
            new ResponseError("DeploymentFailed", "At least one resource deployment operation failed."),
            targetResource,
            AzureProvisioningFailureDetails.ProvisionOperation,
            "BadRequest",
            "request-id",
            statusMessageContent);

        Assert.NotNull(details);
        Assert.Equal("Microsoft.Search", details.Provider);
        Assert.Equal("Microsoft.Search/searchServices", details.ResourceType);
        Assert.Equal("search", details.ResourceName);
        Assert.Equal(targetResourceId, details.TargetResourceId);
        Assert.Equal(AzureProvisioningFailureDetails.LocationNotAvailableForResourceTypeErrorCode, details.ErrorCode);
        Assert.Contains("australiacentral", details.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains(details.RecommendedActions, action => action.Code == "change-location");
    }

    [Fact]
    public void WithoutAzureProvisioningFailureProperties_RemovesOnlyFailureProperties()
    {
        ImmutableArray<ResourcePropertySnapshot> properties =
        [
            new("azure.provisioning.error.code", "LocationNotAvailableForResourceType"),
            new("azure.provisioning.error.message", "old failure"),
            new("azure.subscription.id", "12345678-1234-1234-1234-123456789012"),
            new("custom.property", "kept")
        ];

        var filtered = properties.WithoutAzureProvisioningFailureProperties();

        Assert.Collection(
            filtered,
            property =>
            {
                Assert.Equal("azure.subscription.id", property.Name);
                Assert.Equal("12345678-1234-1234-1234-123456789012", property.Value);
            },
            property =>
            {
                Assert.Equal("custom.property", property.Name);
                Assert.Equal("kept", property.Value);
            });
    }

    [Fact]
    public void WithoutAzureProvisioningFailureProperties_ReturnsOriginalPropertiesWhenNoFailurePropertiesExist()
    {
        ImmutableArray<ResourcePropertySnapshot> properties =
        [
            new("azure.subscription.id", "12345678-1234-1234-1234-123456789012"),
            new("custom.property", "kept")
        ];

        var filtered = properties.WithoutAzureProvisioningFailureProperties();

        Assert.True(properties.Equals(filtered));
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsMissingResourceIdReasonWhenCachedStateHasNoOutputId()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"blobEndpoint":{"value":"https://storage.blob.core.windows.net/"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.True(result.Data!.DisplayImmediately);

        var data = AssertCommandJsonData(result);
        var deployment = Assert.IsType<JsonObject>(data["deployment"]);
        Assert.True(deployment["hasState"]?.GetValue<bool>());
        Assert.Null(deployment["resourceId"]);

        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.False(live["checked"]?.GetValue<bool>());
        Assert.Null(live["exists"]);
        Assert.Equal("missing-resource-id", live["reason"]?.GetValue<string>());
        Assert.Equal("aspire", live["source"]?.GetValue<string>());
        Assert.Equal("missing-resource-id", live["code"]?.GetValue<string>());
        Assert.Contains("cached deployment state does not contain a resource ID", live["message"]?.GetValue<string>(), StringComparison.Ordinal);
        var recommendedActions = Assert.IsType<JsonArray>(live["recommendedActions"]);
        Assert.Contains(recommendedActions, action => action?["code"]?.GetValue<string>() == "reprovision-or-change-context");
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsStructuredRequestFailureWhenLiveProbeFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, armClientProvider: new ThrowingResourceProbeArmClientProvider(new RequestFailedException(403, "Forbidden", "AuthorizationFailed", null)), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);

        var data = AssertCommandJsonData(result);
        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.True(live["checked"]?.GetValue<bool>());
        Assert.Null(live["exists"]);
        Assert.Equal("request-failed", live["reason"]?.GetValue<string>());
        Assert.Equal("azure", live["source"]?.GetValue<string>());
        Assert.Equal(403, live["status"]?.GetValue<int>());
        Assert.Equal("Azure", live["provider"]?.GetValue<string>());
        Assert.Equal(403, live["httpStatus"]?.GetValue<int>());
        Assert.Equal("AuthorizationFailed", live["errorCode"]?.GetValue<string>());
        Assert.Equal("live-resource-check", live["operation"]?.GetValue<string>());
        Assert.Contains("Forbidden", live["message"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAzureResourceCommand_ReturnsCredentialUnavailableReasonWhenLiveProbeCannotAuthenticate()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, armClientProvider: new CredentialUnavailableArmClientProvider(), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var getResourceCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.GetAzureResourceCommandName);

        var result = await getResourceCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);

        var data = AssertCommandJsonData(result);
        var live = Assert.IsType<JsonObject>(data["live"]);
        Assert.True(live["checked"]?.GetValue<bool>());
        Assert.Null(live["exists"]);
        Assert.Equal("credential-unavailable", live["reason"]?.GetValue<string>());
        Assert.Contains("Credential unavailable", live["message"]?.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelDeploymentCommand_CancelsCachedDeploymentAndMarksStateCanceled()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var canceledDeploymentIds = new List<string>();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider(
            existingResourceIds: Array.Empty<string>(),
            deletedResourceIds: null,
            deploymentTargetResourceIds: null,
            canceledDeploymentIds: canceledDeploymentIds), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Waiting for Deployment", KnownResourceStateStyles.Info) });

        var cancelCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);

        var result = await cancelCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure deployment cancellation requested.", result.Message);
        Assert.Equal([deploymentId], canceledDeploymentIds);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Canceled", storageEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task CancelDeploymentCommand_IsEnabledDuringActiveDeploymentOperation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var provisioningTask = controller.EnsureProvisionedAsync(model, CancellationToken.None);

        await WaitForSignalBeforeOperationCompletesAsync(
            testBicepProvisioner.FirstProvisionStarted.Task,
            provisioningTask,
            "Provisioning completed before the first resource started provisioning.");
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Creating ARM Deployment", KnownResourceStateStyles.Info) });

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.ChangeResourceLocationCommandName, ResourceCommandState.Disabled);
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.GetAzureResourceCommandName, ResourceCommandState.Enabled);
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.CancelDeploymentCommandName, ResourceCommandState.Enabled);
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.DeleteAzureResourceCommandName, ResourceCommandState.Disabled);
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.ForgetStateCommandName, ResourceCommandState.Disabled);
        AssertCommandState(storageEvent.Snapshot, AzureProvisioningController.ReprovisionResourceCommandName, ResourceCommandState.Disabled);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        await provisioningTask;
    }

    [Fact]
    public async Task CancelDeploymentCommand_IsDisabledWhenResourceIsNotWaitingForDeployment()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateSucceeded;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        var cancelCommand = Assert.Single(storageEvent.Snapshot.Commands, c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);
        Assert.Equal(ResourceCommandState.Disabled, cancelCommand.State);
    }

    [Fact]
    public async Task CancelDeploymentCommand_DoesNotMarkCompletedDeploymentCanceled()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        AddTestAzureProvisioning(builder, armClientProvider: new CancelConflictArmClientProvider(), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateSucceeded;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var cancelCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.CancelDeploymentCommandName);

        var result = await cancelCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.False(result.Canceled);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateSucceeded, storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.Running, storageEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task DeleteAzureResourceCommand_DeletesCachedOutputAndDeploymentOperationTargets()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var deletedResourceIds = new List<string>();
        var canceledDeploymentIds = new List<string>();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string deploymentId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/storage";
        const string storageResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";
        const string partialIdentityResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/storage-identity";
        const string nestedDeploymentResourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Resources/deployments/nested";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider(
            existingResourceIds: [storageResourceId, partialIdentityResourceId, nestedDeploymentResourceId],
            deletedResourceIds: deletedResourceIds,
            deploymentTargetResourceIds: [partialIdentityResourceId, nestedDeploymentResourceId],
            canceledDeploymentIds: canceledDeploymentIds), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = deploymentId;
        storageSection.Data["Outputs"] = new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "String",
                ["value"] = storageResourceId
            }
        }.ToJsonString();
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        storageSection.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
        storage2.Resource.Outputs["blobEndpoint"] = "https://storage2.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Failed to Provision", KnownResourceStateStyles.Error) });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var deleteCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourceCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal([deploymentId], canceledDeploymentIds);
        Assert.Equal(2, deletedResourceIds.Count);
        Assert.Contains(storageResourceId, deletedResourceIds);
        Assert.Contains(partialIdentityResourceId, deletedResourceIds);
        Assert.DoesNotContain(nestedDeploymentResourceId, deletedResourceIds);

        var resultData = AssertCommandJsonData(result);
        Assert.Equal(2, resultData["deletedResourceCount"]?.GetValue<int>());
        var resultResourceIds = Assert.IsType<JsonArray>(resultData["deletedResourceIds"]);
        Assert.Equal(deletedResourceIds, resultResourceIds.Select(static resourceId => resourceId!.GetValue<string>()));

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
        Assert.False(storageSection.Data.ContainsKey("Id"));
        Assert.False(storageSection.Data.ContainsKey("Outputs"));

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.Empty(storage.Resource.Outputs);
        Assert.Equal("https://storage2.blob.core.windows.net/", storage2.Resource.Outputs["blobEndpoint"]);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
    }

    [Fact]
    public async Task DeleteAzureResourceCommand_UpdatesCommandStatesWhileOperationIsActive()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var armClient = new BlockingDeleteArmClient();
        const string subscriptionId = "12345678-1234-1234-1234-123456789012";
        const string resourceGroup = "test-rg";
        const string resourceId = $"/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Storage/storageAccounts/storage";

        builder.Configuration["Azure:SubscriptionId"] = subscriptionId;
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = resourceGroup;
        AddTestAzureProvisioning(builder, armClientProvider: new BlockingDeleteArmClientProvider(armClient), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var deleteCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourceCommandName);

        var commandTask = deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        await WaitForSignalBeforeOperationCompletesAsync(
            armClient.DeleteStarted.Task,
            commandTask,
            "Delete command completed before the ARM delete operation started.");

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Disabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Deleting", storageEvent.Snapshot.State?.Text);
        AssertAffectedResourceCommandsDuringOperation(storageEvent.Snapshot);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        AssertUnaffectedResourceCommandsDuringOperation(storage2Event.Snapshot);

        armClient.AllowDeleteToComplete.TrySetResult();
        var result = await commandTask;

        Assert.True(result.Success);
        Assert.Equal([resourceId], armClient.DeletedResourceIds);
    }

    [Fact]
    public async Task ForgetStateCommand_ClearsOnlyTargetedResourceStateAndSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        storage.Resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
        storage2.Resource.Outputs["blobEndpoint"] = "https://storage2.blob.core.windows.net/";

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls = [new("deployment", "https://portal.azure.com/storage", false)],
            Properties = [new(CustomResourceKnownProperties.Source, "storage-deployment"), new("custom.property", "keep-storage")]
        });

        await notifications.PublishUpdateAsync(storage2.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Urls = [new("deployment", "https://portal.azure.com/storage2", false)],
            Properties = [new(CustomResourceKnownProperties.Source, "storage2-deployment"), new("custom.property", "keep-storage2")]
        });

        var forgetCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ForgetStateCommandName);

        var result = await forgetCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource provisioning state reset.", result.Message);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.Empty(storage.Resource.Outputs);
        Assert.Equal("https://storage2.blob.core.windows.net/", storage2.Resource.Outputs["blobEndpoint"]);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.NotStarted, storageEvent.Snapshot.State?.Text);
        Assert.DoesNotContain(storageEvent.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
        Assert.Contains(storageEvent.Snapshot.Properties, p => p.Name == "custom.property");

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
        Assert.Contains(storage2Event.Snapshot.Properties, p => p.Name == CustomResourceKnownProperties.Source);
    }

    [Fact]
    public async Task ReprovisionCommand_ReprovisionsOnlyTargetedResource()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Message);
        Assert.Contains(storage.Resource.Outputs, output => output.Key == "blobEndpoint");

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("storage2-deployment", storage2Section.Data["Id"]?.GetValue<string>());

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Running", storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(KnownResourceStates.Running, storage2Event.Snapshot.State?.Text);
    }

    [Fact]
    public async Task ReprovisionCommand_UpdatesCommandStatesWhileOperationIsActive()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var commandTask = reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        await WaitForSignalBeforeOperationCompletesAsync(
            testBicepProvisioner.FirstProvisionStarted.Task,
            commandTask,
            "Reprovision command completed before provisioning started.");

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Disabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        AssertAffectedResourceCommandsDuringOperation(storageEvent.Snapshot);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        AssertUnaffectedResourceCommandsDuringOperation(storage2Event.Snapshot);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        var result = await commandTask;

        Assert.True(result.Success);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out environmentEvent));
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Enabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out storageEvent));
        AssertUnaffectedResourceCommandsDuringOperation(storageEvent.Snapshot);
    }

    [Fact]
    public async Task ReprovisionCommand_ReenablesCommandStatesWhenOperationFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingThrowingTestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var commandTask = reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        await WaitForSignalBeforeOperationCompletesAsync(
            testBicepProvisioner.FirstProvisionStarted.Task,
            commandTask,
            "Reprovision command completed before provisioning started.");

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Disabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        AssertAffectedResourceCommandsDuringOperation(storageEvent.Snapshot);

        testBicepProvisioner.AllowFirstProvisionToThrow.TrySetResult();
        var result = await commandTask;

        Assert.False(result.Success);
        Assert.False(result.Canceled);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out environmentEvent));
        Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Enabled, command.State));

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out storageEvent));
        AssertUnaffectedResourceCommandsDuringOperation(storageEvent.Snapshot);
    }

    [Fact]
    public async Task QueuedOperation_CancelledDuringInitialCommandStateRefreshCompletesAndReenablesCommands()
    {
        var builder = CreateBuilder(isRunMode: true);
        AddTestAzureProvisioning(builder, bicepProvisioner: new ThrowingTestBicepProvisioner());

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedOperation = CreateQueuedOperationForTest(model, "EnsureProvisionedIntent", completion, cts.Token);

        await InvokeProcessQueuedOperationAsync(controller, queuedOperation);

        await Assert.ThrowsAsync<TaskCanceledException>(() => completion.Task.WaitAsync(s_testSynchronizationTimeout));
        Assert.Equal(ResourceCommandState.Enabled, controller.GetEnvironmentCommandState());
    }

    [Fact]
    public async Task MutatingResourceCommands_ExecuteSequentiallyWhenInvokedConcurrently()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var reprovisionStorageCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        var reprovisionStorage2Command = Assert.Single(storage2.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var storageTask = reprovisionStorageCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        await WaitForSignalBeforeOperationCompletesAsync(
            testBicepProvisioner.FirstProvisionStarted.Task,
            storageTask,
            "First reprovision command completed before provisioning started.");

        var storage2Task = reprovisionStorage2Command.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage2.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(storageTask.IsCompleted);
        Assert.False(storage2Task.IsCompleted);
        Assert.Equal(["storage"], testBicepProvisioner.ProvisionedResources);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();

        var result = await storageTask;
        var result2 = await storage2Task;

        Assert.True(result.Success);
        Assert.True(result2.Success);
        Assert.Equal(["storage", "storage2"], testBicepProvisioner.ProvisionedResources);
    }

    [Fact]
    public async Task ChangeLocationCommand_UpdatesCommandStatesWhileOperationIsActive()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var commandTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(("location", "westus2"))
        });

        try
        {
            await WaitForSignalBeforeOperationCompletesAsync(
                testBicepProvisioner.FirstProvisionStarted.Task,
                commandTask,
                "Change location command completed before command states were disabled.");

            Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
            Assert.All(environmentEvent.Snapshot.Commands, command => Assert.Equal(ResourceCommandState.Disabled, command.State));

            Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
            var storageSnapshot = storageEvent.Snapshot;
            AssertAffectedResourceCommandsDuringOperation(storageSnapshot);

            Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
            AssertUnaffectedResourceCommandsDuringOperation(storage2Event.Snapshot);
        }
        finally
        {
            testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        }

        var result = await commandTask;

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ChangeLocationCommand_PersistsOverrideAndReprovisionsTargetedResource()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = new("Failed to Provision", KnownResourceStateStyles.Error) });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
        Assert.Equal("westus2", testBicepProvisioner.ProvisionedLocations["storage"]);
        Assert.DoesNotContain("storage2", testBicepProvisioner.ProvisionedLocations.Keys);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus2", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_WithArguments_DoesNotPromptAndReturnsJsonResult()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());

        var data = AssertCommandJsonData(result);
        Assert.Equal(1, data["schemaVersion"]?.GetValue<int>());
        Assert.Equal(AzureProvisioningController.ChangeResourceLocationCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("storage", data["resourceName"]?.GetValue<string>());
        Assert.Equal("westus3", data["location"]?.GetValue<string>());
        Assert.Equal("westus3", data["effectiveLocation"]?.GetValue<string>());
        Assert.Equal("eastus", data["azureLocation"]?.GetValue<string>());
        var azureContext = Assert.IsType<JsonObject>(data["azureContext"]);
        Assert.Equal("eastus", azureContext["location"]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_ForAnnotatedResource_PersistsOverrideUnderBicepResourceName()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var visibleResource = new AnnotatedAzureResource("storage");
        var bicepResource = new AzureBicepResource("storage-deployment", templateString: "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        visibleResource.Annotations.Add(new AzureBicepResourceAnnotation(bicepResource));
        builder.AddResource(visibleResource);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
        await notifications.PublishUpdateAsync(visibleResource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(visibleResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = visibleResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage-deployment"]);

        var bicepSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage-deployment");
        Assert.Equal("westus3", bicepSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());

        var visibleSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.False(visibleSection.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task ChangeLocationCommand_UsesPersistedAzureContextForSelectableLocations()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "eastus";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);
        var commandInputs = CloneInputs(changeLocationCommand.Arguments);
        var commandLocationInput = commandInputs[AzureBicepResource.KnownParameters.Location];

        await LoadInputAsync(app.Services, commandInputs, commandLocationInput);

        Assert.Equal("westus3", commandLocationInput.Value);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        var locationInput = interaction.Inputs[AzureBicepResource.KnownParameters.Location];

        Assert.Equal(InputType.Choice, locationInput.InputType);
        var options = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, string>>>(locationInput.Options);
        Assert.Contains(options, option => option.Key == "westus2");
        Assert.Equal("westus3", locationInput.Value);

        locationInput.Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Message);
    }

    [Fact]
    public async Task ChangeLocationCommand_DeletesCachedResourceBeforeReprovisioningNewLocation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds), bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
    }

    [Fact]
    public async Task ChangeLocationCommand_DeletesCachedResourceUsingPersistedLocationWhenSnapshotLocationIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds), bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Parameters"] = """
            {
              // The cached deployment location is used when the resource snapshot has not been published yet.
              "location": {
                "value": "westus2",
              },
            }
            """;
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}",
              },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var result = await changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments((AzureBicepResource.KnownParameters.Location, "westus3"))
        });

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);
    }

    [Fact]
    public async Task ChangeLocationCommand_UsesRequestedLocationWhenChangingExistingOverride()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds), bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["LocationOverride"] = "westus2";
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal([resourceId], deletedResourceIds);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_TreatsDeletedCachedResourceAsAlreadyAbsent()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, armClientProvider: new DeleteResourceFailureArmClientProvider(resourceId, new RequestFailedException(404, "Not found.")), bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus3";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus3", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionAllCommand_PreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Id"] = "storage2-deployment";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionAllCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionAllCommandName);

        var result = await reprovisionAllCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure reprovisioning completed.", result.Message);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus2", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("test-rg", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeAzureContextCommand_WithArguments_PersistsContextAndReturnsJsonResult()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg-é"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"),
                ("tenantId", "87654321-4321-4321-4321-210987654321"))
        });

        Assert.True(result.Success);
        Assert.Equal("Azure context updated and resources reprovisioned.", result.Message);
        Assert.Contains("storage", testBicepProvisioner.ProvisionedResources);
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus3", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("cli-rg-é", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());

        var data = AssertCommandJsonData(result);
        Assert.Equal(1, data["schemaVersion"]?.GetValue<int>());
        Assert.Equal(AzureProvisioningController.ChangeAzureContextCommandName, data["command"]?.GetValue<string>());
        Assert.Equal("12345678-1234-1234-1234-123456789012", data["subscriptionId"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", data["tenantId"]?.GetValue<string>());
        Assert.Equal("cli-rg-é", data["resourceGroup"]?.GetValue<string>());
        Assert.Equal("westus3", data["azureLocation"]?.GetValue<string>());
        var azureContext = Assert.IsType<JsonObject>(data["azureContext"]);
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureContext["subscriptionId"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureContext["tenantId"]?.GetValue<string>());
        Assert.Equal("cli-rg-é", azureContext["resourceGroup"]?.GetValue<string>());
        Assert.Equal("westus3", azureContext["location"]?.GetValue<string>());
        Assert.Contains("cli-rg-é", result.Data!.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u00E9", result.Data.Value, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, data["resourceCount"]?.GetValue<int>());

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.Running, environmentEvent.Snapshot.State?.Text);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.subscription.id", "12345678-1234-1234-1234-123456789012", AzureProvisioningStrings.ContextPropertySubscriptionIdDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.resource.group", "cli-rg-é", AzureProvisioningStrings.ContextPropertyResourceGroupDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.location", "westus3", AzureProvisioningStrings.ContextPropertyLocationDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.tenant.id", "87654321-4321-4321-4321-210987654321", AzureProvisioningStrings.ContextPropertyTenantIdDisplayName);
    }

    [Fact]
    public async Task ChangeAzureContextCommand_WithArgumentsWithoutTenant_ClearsPersistedTenant()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, deploymentStateManager: deploymentStateManager);

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success, result.Message);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.False(azureSection.Data.ContainsKey("TenantId"));

        var data = AssertCommandJsonData(result);
        Assert.Null(data["tenantId"]);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.DoesNotContain(environmentEvent.Snapshot.Properties, p => p.Name == "azure.tenant.id");
    }

    [Fact]
    public async Task ChangeAzureContextCommand_DoesNotInferResourceLocationOverrides()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Parameters"] = """{"location":{"value":"westus2"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with
        {
            State = KnownResourceStates.Running,
            Properties = [new("azure.location", "westus2")]
        });

        var changeContextCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeAzureContextCommandName);

        var result = await changeContextCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = CreateArguments(
                ("subscriptionId", "12345678-1234-1234-1234-123456789012"),
                ("resourceGroup", "cli-rg"),
                (AzureBicepResource.KnownParameters.Location, "West US 3"))
        });

        Assert.True(result.Success, result.Message);
        Assert.NotEqual("westus2", testBicepProvisioner.ProvisionedLocations["storage"]);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.False(storageSection.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task ReprovisionAllCommand_NormalizesPersistedLocationOverride()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data[AzureProvisioningController.LocationOverrideKey] = "West US 3";
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await controller.ReprovisionAllAsync(model);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionAllCommand_PreservesLocationOverrideFromPersistedParameters()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Parameters"] = """
            {
              // Preserve resource-specific locations from cached deployment parameters.
              "location": { "value": "westus3", },
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await controller.ReprovisionAllAsync(model);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionResourceCommand_PreservesInMemoryLocationOverrideWhenCachedStateIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storage2 = model.Resources.OfType<AzureBicepResource>().Single(r => r.Name == "storage2");
        await notifications.PublishUpdateAsync(storage2, state => state with
        {
            State = KnownResourceStates.Running,
            Properties =
            [
                new("azure.location", "westus3"),
                new("azure.subscription.id", "12345678-1234-1234-1234-123456789012")
            ]
        });

        var reprovisionCommand = Assert.Single(storage2.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage2.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Message);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ForgetResourceStateCommand_ClearsInMemoryLocationParameter()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner, deploymentStateManager: deploymentStateManager);

        builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var storage = model.Resources.OfType<AzureBicepResource>().Single(r => r.Name == "storage");
        storage.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        storageSection.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.ForgetResourceStateAsync(model, "storage");

        Assert.False(storage.Parameters.ContainsKey(AzureBicepResource.KnownParameters.Location));

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
    }

    [Fact]
    public async Task ReprovisionResourceCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);

        AddTestAzureProvisioning(builder, bicepProvisioner: new ThrowingTestBicepProvisioner());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task ChangeLocationCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        AddTestAzureProvisioning(builder, bicepProvisioner: new ThrowingTestBicepProvisioner());

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.False(result.Success);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task ReprovisionCommand_ReturnsStructuredProviderFailureDetailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var requestFailedException = new RequestFailedException(
            400,
            "The provided location 'asia' is not available for resource type 'Microsoft.ManagedIdentity/userAssignedIdentities'.",
            "LocationNotAvailableForResourceType",
            innerException: null);

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "asia";
        AddTestAzureProvisioning(builder, bicepProvisioner: new ThrowingTestBicepProvisioner(requestFailedException));

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.Contains("Azure provisioning failed.", result.Message, StringComparison.Ordinal);
        Assert.Contains("LocationNotAvailableForResourceType", result.Message, StringComparison.Ordinal);
        Assert.Contains(AzureProvisioningController.ChangeResourceLocationCommandName, result.Message, StringComparison.Ordinal);

        var data = AssertCommandJsonData(result);
        Assert.False(data["success"]?.GetValue<bool>());
        Assert.Equal("failed", data["status"]?.GetValue<string>());
        var diagnostics = Assert.IsType<JsonArray>(data["diagnostics"]);
        var diagnostic = Assert.IsType<JsonObject>(Assert.Single(diagnostics));
        Assert.Equal("azure", diagnostic["source"]?.GetValue<string>());
        Assert.Equal("Microsoft.ManagedIdentity", diagnostic["provider"]?.GetValue<string>());
        Assert.Equal("Microsoft.ManagedIdentity/userAssignedIdentities", diagnostic["resourceType"]?.GetValue<string>());
        Assert.Equal(400, diagnostic["httpStatus"]?.GetValue<int>());
        Assert.Equal("LocationNotAvailableForResourceType", diagnostic["errorCode"]?.GetValue<string>());
        Assert.Equal("provision", diagnostic["operation"]?.GetValue<string>());
        var resultRecommendedActions = Assert.IsType<JsonArray>(diagnostic["recommendedActions"]);
        Assert.Contains(resultRecommendedActions, action => action?["code"]?.GetValue<string>() == "change-location");

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal("Failed to Provision", storageEvent.Snapshot.State?.Text);
        Assert.All(
            storageEvent.Snapshot.Properties.Where(property => AzureProvisioningFailureDetails.IsFailureProperty(property.Name)),
            property =>
            {
                Assert.True(property.IsHighlighted);
                Assert.False(string.IsNullOrWhiteSpace(property.DisplayName));
            });
        Assert.Equal("Microsoft.ManagedIdentity", storageEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.provider").Value?.ToString());
        Assert.Equal("Microsoft.ManagedIdentity/userAssignedIdentities", storageEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.resource.type").Value?.ToString());
        Assert.Equal("LocationNotAvailableForResourceType", storageEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.code").Value?.ToString());
        Assert.Equal(400, storageEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.http.status").Value);
        var snapshotRecommendedActions = Assert.IsType<string[]>(storageEvent.Snapshot.Properties.Single(p => p.Name == "azure.provisioning.error.recommendedActions").Value);
        Assert.Contains(snapshotRecommendedActions, action => action.Contains(AzureProvisioningController.ChangeResourceLocationCommandName, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResourceCommandCancellation_ReturnsCanceledResult()
    {
        var builder = CreateBuilder(isRunMode: true);

        AddTestAzureProvisioning(builder);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        var reprovisionCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await reprovisionCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = cts.Token,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.True(result.Canceled);
    }

    [Fact]
    public async Task CheckForDriftAsync_MarksResourceMissingInAzure()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider(existingResourceIds: []), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        azureSection.Data["Tenant"] = "testdomain.onmicrosoft.com";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(AzureProvisioningController.DriftedState, environmentEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, environmentEvent.Snapshot.State?.Style);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.subscription.id", "12345678-1234-1234-1234-123456789012", AzureProvisioningStrings.ContextPropertySubscriptionIdDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.resource.group", "test-rg", AzureProvisioningStrings.ContextPropertyResourceGroupDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.location", "westus2", AzureProvisioningStrings.ContextPropertyLocationDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.tenant.id", "87654321-4321-4321-4321-210987654321", AzureProvisioningStrings.ContextPropertyTenantIdDisplayName);
        AssertHighlightedContextProperty(environmentEvent.Snapshot.Properties, "azure.tenant.domain", "testdomain.onmicrosoft.com", AzureProvisioningStrings.ContextPropertyTenantDomainDisplayName);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var resourceEvent));
        Assert.Equal(AzureProvisioningController.MissingInAzureState, resourceEvent.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, resourceEvent.Snapshot.State?.Style);
    }

    [Fact]
    public async Task CheckForDriftAsync_LeavesRunningResourcesWhenAzureResourcesStillExist()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([resourceId]), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{resourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.Running, environmentEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var resourceEvent));
        Assert.Equal(KnownResourceStates.Running, resourceEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task CheckForDriftAsync_MarksOnlyMissingResourceWhenOtherAzureResourcesStillExist()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        const string existingResourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage";
        const string missingResourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage2";

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider([existingResourceId]), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");
        var storage2 = builder.AddBicepTemplateString("storage2", "resource storage2 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{existingResourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        storage2Section.Data["Outputs"] = $$"""
            {
              "id": {
                "value": "{{missingResourceId}}"
              }
            }
            """;
        await deploymentStateManager.SaveSectionAsync(storage2Section);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage2.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(AzureProvisioningController.DriftedState, environmentEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var storageEvent));
        Assert.Equal(KnownResourceStates.Running, storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage2.Resource.Name, out var storage2Event));
        Assert.Equal(AzureProvisioningController.MissingInAzureState, storage2Event.Snapshot.State?.Text);
        Assert.Equal(KnownResourceStateStyles.Error, storage2Event.Snapshot.State?.Style);
    }

    [Fact]
    public async Task CheckForDriftAsync_SkipsResourcesWithoutCachedResourceIds()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProvider(existingResourceIds: []), deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Outputs"] = """{"blobEndpoint":{"value":"https://storage.blob.core.windows.net/"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(KnownResourceStates.Running, environmentEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var resourceEvent));
        Assert.Equal(KnownResourceStates.Running, resourceEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_DeletesCurrentResourceGroupAndPreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var resourceGroup = new TestResourceGroupResource("test-rg");
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        AddTestAzureProvisioning(builder, armClientProvider: new TestArmClientProvider(resourceGroup), provisioningContextProvider: testProvisioningContextProvider, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(1, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus2", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("test-rg", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);

        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_DoesNotDeleteConfiguredResourceGroupWhenPersistedContextIsMissing()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var resourceGroup = new TestResourceGroupResource("configured-rg");
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "configured-rg";
        AddTestAzureProvisioning(builder, armClientProvider: new TestArmClientProvider(resourceGroup), provisioningContextProvider: testProvisioningContextProvider, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(0, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_TreatsMissingResourceGroupAsSuccessAndClearsState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        AddTestAzureProvisioning(builder, armClientProvider: ProvisioningTestHelpers.CreateArmClientProviderForMissingResourceGroup(), provisioningContextProvider: testProvisioningContextProvider, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "missing-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Message);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Empty(storageSection.Data);
        Assert.Empty(storage.Resource.Outputs);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_PublishesFailureWhenResourceGroupDeleteFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var resourceGroup = new TestResourceGroupResource("test-rg", new RequestFailedException(409, "Resource group is locked."));

        AddTestAzureProvisioning(builder, armClientProvider: new TestArmClientProvider(resourceGroup), provisioningContextProvider: testProvisioningContextProvider, deploymentStateManager: deploymentStateManager);

        var storage = builder.AddBicepTemplateString("storage", "resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {}");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var environmentResource = Assert.Single(model.Resources.OfType<AzureEnvironmentResource>());
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus2";
        azureSection.Data["ResourceGroup"] = "test-rg";
        await deploymentStateManager.SaveSectionAsync(azureSection);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        storageSection.Data["Id"] = "storage-deployment";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        var deleteCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.DeleteAzureResourcesCommandName);

        var result = await deleteCommand.ExecuteCommand(new ExecuteCommandContext
        {
            Services = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        });

        Assert.False(result.Success);
        Assert.Equal(1, resourceGroup.DeleteCallCount);
        Assert.Equal(0, testProvisioningContextProvider.CreateProvisioningContextCallCount);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal("Failed to Delete", environmentEvent.Snapshot.State?.Text);

        storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("storage-deployment", storageSection.Data["Id"]?.GetValue<string>());
    }

    [Fact]
    public async Task EnsureProvisioned_WaitsForReferencedAzureResources()
    {
        var builder = CreateBuilder(isRunMode: true);
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        AddTestAzureProvisioning(builder, bicepProvisioner: testBicepProvisioner);

        var storage = new AzureProvisioningResource("storage", _ => { });
        storage.Outputs["name"] = "storage";
        var storageRoles = new AzureProvisioningResource("storage-roles", infra =>
        {
            new BicepOutputReference("name", storage).AsProvisioningParameter(infra);
        });
        builder.AddResource(storageRoles);
        builder.AddResource(storage);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        var reprovisionTask = controller.EnsureProvisionedAsync(model, CancellationToken.None);

        await WaitForSignalBeforeOperationCompletesAsync(
            testBicepProvisioner.FirstProvisionStarted.Task,
            reprovisionTask,
            "Provisioning completed before the first resource started provisioning.");
        Assert.Equal(["storage"], testBicepProvisioner.ProvisionedResources);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        await reprovisionTask;

        Assert.Equal(["storage", "storage-roles"], testBicepProvisioner.ProvisionedResources);
    }

    [Fact]
    public async Task EnsureProvisioned_FaultsDependentsWhenDependencyProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);

        AddTestAzureProvisioning(builder, bicepProvisioner: new ThrowingTestBicepProvisioner());

        var storage = new AzureProvisioningResource("storage", _ => { });
        storage.Outputs["name"] = "storage";
        var storageRoles = new AzureProvisioningResource("storage-roles", infra =>
        {
            new BicepOutputReference("name", storage).AsProvisioningParameter(infra);
        });
        builder.AddResource(storageRoles);
        builder.AddResource(storage);

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var controller = app.Services.GetRequiredService<AzureProvisioningController>();

        await controller.EnsureProvisionedAsync(model);

        Assert.True(notifications.TryGetCurrentState(storage.Name, out var storageEvent));
        Assert.Equal("Failed to Provision", storageEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storageRoles.Name, out var storageRolesEvent));
        Assert.Equal("Failed to Provision", storageRolesEvent.Snapshot.State?.Text);
    }

    [Fact]
    public void AddAzureEnvironment_InPublishMode_CreatesStableDeploymentName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        builder.Configuration["AppHost:ProjectNameSha256"] = "ABCDE12345";

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment", resource.Name);
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_CreatesDiscoverableControlResourceName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: true);

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment", resource.Name);
    }

    [Fact]
    public void AzureEnvironmentResource_PreservesDefaultResourceNameValidation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var location = new ParameterResource("location", _ => "westus");
        var resourceGroupName = new ParameterResource("resourceGroupName", _ => "rg");
        var principalId = new ParameterResource("principalId", _ => "principal");
        var resource = new AzureEnvironmentResource("azure_environment", location, resourceGroupName, principalId);

        var ex = Assert.Throws<ArgumentException>(() => builder.AddResource(resource));
        Assert.Equal("Resource name 'azure_environment' is invalid. Name must contain only ASCII letters, digits, and hyphens. (Parameter 'name')", ex.Message);
    }

    [Fact]
    public void AddAzureEnvironment_CreatesFallbackNameWhenAzureResourceNameExists()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: true);
        builder.AddParameter("azure-environment", "value");

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal("azure-environment2", resource.Name);
    }

    [Fact]
    public void WithLocation_ShouldSetLocationProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedLocation = builder.AddParameter("location", "eastus2");

        // Act
        resourceBuilder.WithLocation(expectedLocation);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedLocation.Resource, resource.Location);
    }

    [Fact]
    public void WithResourceGroup_ShouldSetResourceGroupNameProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedResourceGroup = builder.AddParameter("resourceGroupName", "my-resource-group");

        // Act
        resourceBuilder.WithResourceGroup(expectedResourceGroup);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedResourceGroup.Resource, resource.ResourceGroupName);
    }

    private static IDistributedApplicationBuilder CreateBuilder(bool isRunMode = false)
    {
        var operation = isRunMode ? DistributedApplicationOperation.Run : DistributedApplicationOperation.Publish;
        return TestDistributedApplicationBuilder.Create(operation);
    }

    /// <summary>
    /// Registers test implementations of Azure provisioning services and calls
    /// <c>AddAzureProvisioning</c>. All production service registrations use
    /// <c>TryAddSingleton</c>, so pre-registering test implementations here
    /// prevents the production versions from being added. Pass custom
    /// implementations via optional parameters; defaults are used for any
    /// parameter left <c>null</c>.
    /// </summary>
    private static void AddTestAzureProvisioning(
        IDistributedApplicationBuilder builder,
        IArmClientProvider? armClientProvider = null,
        ITokenCredentialProvider? tokenCredentialProvider = null,
        IBicepProvisioner? bicepProvisioner = null,
        IProvisioningContextProvider? provisioningContextProvider = null,
        IDeploymentStateManager? deploymentStateManager = null,
        IAzureProvisioningOptionsManager? provisioningOptionsManager = null)
    {
        var stateManager = deploymentStateManager ?? new TestDeploymentStateManager();
        builder.Services.AddSingleton<IArmClientProvider>(armClientProvider ?? ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(tokenCredentialProvider ?? ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.AddSingleton<IBicepProvisioner>(bicepProvisioner ?? new TestBicepProvisioner());
        builder.Services.AddSingleton<IDeploymentStateManager>(stateManager);
        builder.Services.AddSingleton<IProvisioningContextProvider>(provisioningContextProvider ?? new TestProvisioningContextProvider());
        builder.Services.AddSingleton<IAzureProvisioningOptionsManager>(provisioningOptionsManager ?? new TestAzureProvisioningOptionsManager(stateManager));
        builder.AddAzureProvisioning();
    }

    private static InteractionInputCollection CreateArguments(params (string Name, string? Value)[] values)
    {
        return new InteractionInputCollection([.. values.Select(static value => new InteractionInput
        {
            Name = value.Name,
            InputType = InputType.Text,
            Value = value.Value
        })]);
    }

    private static InteractionInputCollection CloneInputs(IReadOnlyList<InteractionInput> inputs)
    {
        return new InteractionInputCollection([.. inputs.Select(static input => new InteractionInput
        {
            Name = input.Name,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType,
            Required = input.Required,
            Options = input.Options,
            DynamicLoading = input.DynamicLoading,
            Value = input.Value,
            Placeholder = input.Placeholder,
            AllowCustomChoice = input.AllowCustomChoice,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength
        })]);
    }

    private static Task LoadInputAsync(IServiceProvider services, InteractionInputCollection inputs, InteractionInput input)
    {
        return input.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputs,
            CancellationToken = CancellationToken.None,
            Input = input,
            Services = services
        });
    }

    private static object CreateQueuedOperationForTest(
        DistributedApplicationModel model,
        string intentTypeName,
        TaskCompletionSource<object?> completion,
        CancellationToken cancellationToken)
    {
        // This targets the private queue boundary directly because the regression window is between
        // ProcessOperationLoopAsync's pre-dispatch cancellation check and ProcessQueuedOperationAsync's
        // initial command-state refresh. Command-level tests cannot deterministically cancel in that
        // narrow synchronous window without relying on timing.
        var controllerType = typeof(AzureProvisioningController);
        var intentType = controllerType.GetNestedType(intentTypeName, BindingFlags.NonPublic);
        Assert.NotNull(intentType);
        var intent = Activator.CreateInstance(intentType, nonPublic: true);
        Assert.NotNull(intent);

        var queuedOperationType = controllerType.GetNestedType("QueuedOperation", BindingFlags.NonPublic);
        Assert.NotNull(queuedOperationType);
        var queuedOperation = Activator.CreateInstance(
            queuedOperationType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: [model, intent, completion, cancellationToken],
            culture: null);
        Assert.NotNull(queuedOperation);

        return queuedOperation;
    }

    private static async Task InvokeProcessQueuedOperationAsync(AzureProvisioningController controller, object queuedOperation)
    {
        var method = typeof(AzureProvisioningController).GetMethod("ProcessQueuedOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method.Invoke(controller, [queuedOperation]));
        await task.WaitAsync(s_testSynchronizationTimeout).ConfigureAwait(false);
    }

    private static JsonObject AssertCommandJsonData(ExecuteCommandResult result)
    {
        Assert.NotNull(result.Data);
        var data = result.Data!;
        Assert.Equal(CommandResultFormat.Json, data.Format);
        return Assert.IsType<JsonObject>(JsonNode.Parse(data.Value));
    }

    private static void AssertAffectedResourceCommandsDuringOperation(CustomResourceSnapshot snapshot)
    {
        AssertCommandState(snapshot, AzureProvisioningController.ChangeResourceLocationCommandName, ResourceCommandState.Disabled);
        AssertCommandState(snapshot, AzureProvisioningController.GetAzureResourceCommandName, ResourceCommandState.Enabled);
        AssertCommandState(snapshot, AzureProvisioningController.CancelDeploymentCommandName, ResourceCommandState.Disabled);
        AssertCommandState(snapshot, AzureProvisioningController.DeleteAzureResourceCommandName, ResourceCommandState.Disabled);
        AssertCommandState(snapshot, AzureProvisioningController.ForgetStateCommandName, ResourceCommandState.Disabled);
        AssertCommandState(snapshot, AzureProvisioningController.ReprovisionResourceCommandName, ResourceCommandState.Disabled);
    }

    private static void AssertUnaffectedResourceCommandsDuringOperation(CustomResourceSnapshot snapshot)
    {
        AssertCommandState(snapshot, AzureProvisioningController.ChangeResourceLocationCommandName, ResourceCommandState.Enabled);
        AssertCommandState(snapshot, AzureProvisioningController.GetAzureResourceCommandName, ResourceCommandState.Enabled);
        AssertCommandState(snapshot, AzureProvisioningController.CancelDeploymentCommandName, ResourceCommandState.Disabled);
        AssertCommandState(snapshot, AzureProvisioningController.DeleteAzureResourceCommandName, ResourceCommandState.Enabled);
        AssertCommandState(snapshot, AzureProvisioningController.ForgetStateCommandName, ResourceCommandState.Enabled);
        AssertCommandState(snapshot, AzureProvisioningController.ReprovisionResourceCommandName, ResourceCommandState.Enabled);
    }

    private static void AssertCommandState(CustomResourceSnapshot snapshot, string commandName, ResourceCommandState expectedState)
    {
        var command = Assert.Single(snapshot.Commands, c => c.Name == commandName);
        Assert.Equal(expectedState, command.State);
    }

    private static void AssertHighlightedContextProperty(IEnumerable<ResourcePropertySnapshot> properties, string name, object value, string displayName)
    {
        var property = Assert.Single(properties, p => p.Name == name);

        Assert.Equal(value, property.Value);
        Assert.Equal(displayName, property.DisplayName);
        Assert.True(property.IsHighlighted);
    }

    private sealed class AzureProvisioningFailureTestResponse : Response
    {
        private readonly int _status;
        private readonly string _reasonPhrase;

        public AzureProvisioningFailureTestResponse(int status, string reasonPhrase, string content)
        {
            _status = status;
            _reasonPhrase = reasonPhrase;
            ContentStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        }

        public override int Status => _status;

        public override string ReasonPhrase => _reasonPhrase;

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool TryGetHeader(
            string name,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
        {
            value = null;

            return false;
        }

        protected override bool TryGetHeaderValues(
            string name,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;

            return false;
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
    }

    private static readonly TimeSpan s_testSynchronizationTimeout = TimeSpan.FromSeconds(30);

    private static async Task WaitForSignalBeforeOperationCompletesAsync(Task signalTask, Task operationTask, string completionMessage)
    {
        using var watchdog = new CancellationTokenSource(s_testSynchronizationTimeout);
        Task completedTask;

        try
        {
            completedTask = await Task.WhenAny(signalTask, operationTask).WaitAsync(watchdog.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (watchdog.IsCancellationRequested)
        {
            Assert.Fail($"Timed out after {s_testSynchronizationTimeout} waiting for the test synchronization signal. Signal status: {signalTask.Status}. Operation status: {operationTask.Status}. {completionMessage}");
            return;
        }

        if (completedTask == signalTask || signalTask.IsCompleted)
        {
            await signalTask.ConfigureAwait(false);
            return;
        }

        if (operationTask.IsFaulted || operationTask.IsCanceled)
        {
            await operationTask.ConfigureAwait(false);
        }

        Assert.Fail(completionMessage);
    }

    private static async Task<(IReadOnlyList<PipelineStep> Steps, PipelineContext PipelineContext)> CreateAzureEnvironmentPipelineStepsAsync(
        AzureEnvironmentResource environmentResource,
        DistributedApplicationModel model,
        IServiceProvider services)
    {
        var pipelineContext = new PipelineContext(
            model,
            services.GetRequiredService<DistributedApplicationExecutionContext>(),
            services,
            NullLogger.Instance,
            CancellationToken.None);

        var annotation = Assert.Single(environmentResource.Annotations.OfType<PipelineStepAnnotation>());
        var steps = await annotation.CreateStepsAsync(new PipelineStepFactoryContext
        {
            PipelineContext = pipelineContext,
            Resource = environmentResource
        });

        return ([.. steps], pipelineContext);
    }

    private sealed class AzureContextOptionsArmClientProvider(AzureContextOptionsArmClient armClient) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => armClient;

        public IArmClient GetArmClient(TokenCredential credential)
            => armClient;
    }

    private sealed class AzureContextOptionsArmClient : IArmClient
    {
        public const string FirstTenantId = "11111111-1111-1111-1111-111111111111";
        public const string SecondTenantId = "22222222-2222-2222-2222-222222222222";
        public const string FirstSubscriptionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        public const string SecondSubscriptionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        private readonly IReadOnlyList<ContextTenantResource> _tenants =
        [
            new(FirstTenantId, "First tenant"),
            new(SecondTenantId, "Second tenant")
        ];
        private readonly Dictionary<string, IReadOnlyList<ContextSubscriptionResource>> _subscriptionsByTenant = new(StringComparer.OrdinalIgnoreCase)
        {
            [FirstTenantId] = [new(FirstSubscriptionId, "First subscription", FirstTenantId)],
            [SecondTenantId] = [new(SecondSubscriptionId, "Second subscription", SecondTenantId)]
        };
        private readonly Dictionary<string, IReadOnlyList<(string Name, string Location)>> _resourceGroupsBySubscription = new(StringComparer.OrdinalIgnoreCase)
        {
            [FirstSubscriptionId] = [("rg-first", "eastus")],
            [SecondSubscriptionId] = [("rg-second", "westus2")]
        };
        private readonly IReadOnlyList<(string Name, string DisplayName)> _locations =
        [
            ("eastus", "East US"),
            ("westus2", "West US 2")
        ];

        public string? LastSubscriptionTenantId { get; private set; }

        public string? LastResourceGroupSubscriptionId { get; private set; }

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
        {
            var subscription = _subscriptionsByTenant[FirstTenantId].Single();
            var tenant = _tenants.Single(t => t.TenantId == Guid.Parse(FirstTenantId));
            return Task.FromResult<(ISubscriptionResource, ITenantResource)>((subscription, tenant));
        }

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
        {
            IEnumerable<ITenantResource> result = _tenants;
            return Task.FromResult(result);
        }

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
        {
            IEnumerable<ISubscriptionResource> result = _subscriptionsByTenant.Values.SelectMany(static subscriptions => subscriptions);
            return Task.FromResult(result);
        }

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
        {
            LastSubscriptionTenantId = tenantId;

            IEnumerable<ISubscriptionResource> result = tenantId is not null && _subscriptionsByTenant.TryGetValue(tenantId, out var subscriptions)
                ? subscriptions
                : [];
            return Task.FromResult(result);
        }

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = _subscriptionsByTenant.Values
                .SelectMany(static subscriptions => subscriptions)
                .Single(s => string.Equals(s.Id.Name, subscriptionId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<ISubscriptionResource>(subscription);
        }

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IEnumerable<(string Name, string DisplayName)>>(_locations);
        }

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            LastResourceGroupSubscriptionId = subscriptionId;

            IEnumerable<(string Name, string Location)> result = _resourceGroupsBySubscription.TryGetValue(subscriptionId, out var resourceGroups)
                ? resourceGroups
                : [];
            return Task.FromResult(result);
        }

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>([]);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(
            string deploymentId,
            bool recursive = true,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ContextTenantResource(string tenantId, string displayName) : ITenantResource
    {
        public Guid? TenantId { get; } = Guid.Parse(tenantId);

        public string? DisplayName { get; } = displayName;

        public string? DefaultDomain { get; } = $"{displayName.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant()}.onmicrosoft.com";

        public IArmDeploymentCollection GetArmDeployments()
            => new TestArmDeploymentCollection([]);
    }

    private sealed class ContextSubscriptionResource(string subscriptionId, string displayName, string tenantId) : ISubscriptionResource
    {
        public ResourceIdentifier Id { get; } = new($"/subscriptions/{subscriptionId}");

        public string? DisplayName { get; } = displayName;

        public Guid? TenantId { get; } = Guid.Parse(tenantId);

        public IArmDeploymentCollection GetArmDeployments()
            => new TestArmDeploymentCollection([]);

        public IResourceGroupCollection GetResourceGroups()
            => new TestResourceGroupCollection([]);
    }

    private sealed class AnnotatedAzureResource(string name) : Resource(name);

    private sealed class TestDeploymentStateManager : IDeploymentStateManager
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, JsonObject> _sections = new(StringComparer.Ordinal);

        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            JsonObject data;
            lock (_lock)
            {
                _sections.TryGetValue(sectionName, out var existingData);
                data = existingData?.DeepClone().AsObject() ?? [];
            }

            return Task.FromResult(new DeploymentStateSection(sectionName, data, version: 0));
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sections.Remove(section.SectionName);
            }

            return Task.CompletedTask;
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sections[section.SectionName] = section.Data.DeepClone().AsObject();
            }

            return Task.CompletedTask;
        }

        public Task ClearAllStateAsync(CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _sections.Clear();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestBicepProvisioner : IBicepProvisioner
    {
        private readonly object _lock = new();
        private readonly List<string> _configuredResources = [];
        private readonly List<string> _provisionedResources = [];
        private readonly Dictionary<string, string?> _provisionedLocations = new(StringComparer.Ordinal);

        public int ConfigureResourceCallCount { get; private set; }

        public int GetOrCreateResourceCallCount { get; private set; }

        public IReadOnlyList<string> ConfiguredResources
        {
            get
            {
                lock (_lock)
                {
                    return [.. _configuredResources];
                }
            }
        }

        public IReadOnlyList<string> ProvisionedResources
        {
            get
            {
                lock (_lock)
                {
                    return [.. _provisionedResources];
                }
            }
        }

        public IReadOnlyDictionary<string, string?> ProvisionedLocations
        {
            get
            {
                lock (_lock)
                {
                    return new Dictionary<string, string?>(_provisionedLocations, StringComparer.Ordinal);
                }
            }
        }

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                ConfigureResourceCallCount++;
                _configuredResources.Add(resource.Name);
            }

            return Task.FromResult(false);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                GetOrCreateResourceCallCount++;
                _provisionedResources.Add(resource.Name);
                _provisionedLocations[resource.Name] = resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location)
                    ? location?.ToString()
                    : null;
            }

            resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
            return Task.CompletedTask;
        }
    }

    private sealed class CachedStateTestBicepProvisioner : IBicepProvisioner
    {
        public int ConfigureResourceCallCount { get; private set; }

        public int GetOrCreateResourceCallCount { get; private set; }

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken)
        {
            ConfigureResourceCallCount++;
            resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
            return Task.FromResult(true);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            GetOrCreateResourceCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class TestProvisioningContextProvider : IProvisioningContextProvider
    {
        private readonly ProvisioningContext _context;

        public TestProvisioningContextProvider()
            : this(ProvisioningTestHelpers.CreateTestProvisioningContext())
        {
        }

        public TestProvisioningContextProvider(ProvisioningContext context)
        {
            _context = context;
        }

        public int CreateProvisioningContextCallCount { get; private set; }

        public Task<ProvisioningContext> CreateProvisioningContextAsync(CancellationToken cancellationToken = default)
        {
            CreateProvisioningContextCallCount++;
            return Task.FromResult(_context);
        }
    }

    private sealed class TestAzureProvisioningOptionsManager(IDeploymentStateManager deploymentStateManager) : IAzureProvisioningOptionsManager
    {
        public Task<bool> EnsureProvisioningOptionsAsync(bool forcePrompt, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task PersistProvisioningOptionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<AzureProvisioningOptionsState> ApplyProvisioningOptionsAsync(AzureProvisioningOptionsUpdate options, CancellationToken cancellationToken = default)
        {
            var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", cancellationToken);
            azureSection.Data["SubscriptionId"] = options.SubscriptionId;
            azureSection.Data["Location"] = options.Location;
            azureSection.Data["ResourceGroup"] = options.ResourceGroup;

            if (!string.IsNullOrEmpty(options.TenantId))
            {
                azureSection.Data["TenantId"] = options.TenantId;
            }
            else
            {
                azureSection.Data.Remove("TenantId");
            }

            await deploymentStateManager.SaveSectionAsync(azureSection, cancellationToken);
            return new AzureProvisioningOptionsState(options.SubscriptionId, options.ResourceGroup, options.Location, options.TenantId);
        }
    }

    private sealed class BlockingTestBicepProvisioner : IBicepProvisioner
    {
        private readonly object _lock = new();
        private readonly List<string> _provisionedResources = [];

        public TaskCompletionSource FirstProvisionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstProvisionToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> ProvisionedResources
        {
            get
            {
                lock (_lock)
                {
                    return [.. _provisionedResources];
                }
            }
        }

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            bool isFirstProvision;
            lock (_lock)
            {
                _provisionedResources.Add(resource.Name);
                isFirstProvision = _provisionedResources.Count == 1;
            }

            if (isFirstProvision)
            {
                FirstProvisionStarted.TrySetResult();
                await AllowFirstProvisionToComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            resource.Outputs["blobEndpoint"] = $"https://{resource.Name}.blob.core.windows.net/";
        }
    }

    private sealed class BlockingThrowingTestBicepProvisioner : IBicepProvisioner
    {
        public TaskCompletionSource FirstProvisionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstProvisionToThrow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            FirstProvisionStarted.TrySetResult();
            await AllowFirstProvisionToThrow.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class ThrowingTestBicepProvisioner(Exception? exception = null) : IBicepProvisioner
    {
        private readonly Exception _exception = exception ?? new InvalidOperationException("boom");

        public Task<bool> ConfigureResourceAsync(AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
            => Task.FromException(_exception);
    }

    private sealed class CancelConflictArmClientProvider : IArmClientProvider
    {
        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential, string subscriptionId)
            => new CancelConflictArmClient();

        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential)
            => new CancelConflictArmClient();
    }

    private sealed class CancelConflictArmClient : IArmClient
    {
        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>([]);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new RequestFailedException(409, "The deployment is already completed.");

        public async IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(
            string deploymentId,
            bool recursive = true,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BlockingDeleteArmClientProvider(BlockingDeleteArmClient armClient) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => armClient;

        public IArmClient GetArmClient(TokenCredential credential)
            => armClient;
    }

    private sealed class BlockingDeleteArmClient : IArmClient
    {
        private readonly object _lock = new();
        private readonly List<string> _deletedResourceIds = [];

        public TaskCompletionSource DeleteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDeleteToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> DeletedResourceIds
        {
            get
            {
                lock (_lock)
                {
                    return [.. _deletedResourceIds];
                }
            }
        }

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>([]);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public async Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _deletedResourceIds.Add(resourceId);
            }

            DeleteStarted.TrySetResult();
            await AllowDeleteToComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(
            string deploymentId,
            bool recursive = true,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CredentialUnavailableArmClientProvider : IArmClientProvider
    {
        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential, string subscriptionId)
            => new CredentialUnavailableArmClient();

        public IArmClient GetArmClient(global::Azure.Core.TokenCredential credential)
            => new CredentialUnavailableArmClient();
    }

    private sealed class CredentialUnavailableArmClient : IArmClient
    {
        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<string>>([]);

        public IRoleAssignmentCollection GetRoleAssignments(global::Azure.Core.ResourceIdentifier scope)
            => throw new NotSupportedException();

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(new global::Azure.Identity.CredentialUnavailableException("Credential unavailable."));

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(
            string deploymentId,
            bool recursive = true,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingResourceProbeArmClientProvider(RequestFailedException exception) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => new ThrowingResourceProbeArmClient(exception);

        public IArmClient GetArmClient(TokenCredential credential)
            => new ThrowingResourceProbeArmClient(exception);
    }

    private sealed class ThrowingResourceProbeArmClient(RequestFailedException exception) : IArmClient
    {
        private readonly TestArmClient _inner = new();

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAndTenantAsync(cancellationToken);

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableTenantsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(tenantId, cancellationToken);

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableLocationsAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => _inner.GetSupportedLocationsAsync(subscriptionId, resourceType, cancellationToken);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => _inner.GetRoleAssignments(scope);

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromException<bool>(exception);

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => _inner.DeleteResourceAsync(resourceId, cancellationToken);

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.CancelDeploymentAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentTargetResourceIdsAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(string deploymentId, bool recursive = true, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentOperationsAsync(deploymentId, recursive, cancellationToken);
    }

    private sealed class DeleteResourceFailureArmClientProvider(string existingResourceId, RequestFailedException deleteException) : IArmClientProvider
    {
        public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
            => new DeleteResourceFailureArmClient(existingResourceId, deleteException);

        public IArmClient GetArmClient(TokenCredential credential)
            => new DeleteResourceFailureArmClient(existingResourceId, deleteException);
    }

    private sealed class DeleteResourceFailureArmClient(string existingResourceId, RequestFailedException deleteException) : IArmClient
    {
        private readonly TestArmClient _inner = new();

        public Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAndTenantAsync(cancellationToken);

        public Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableTenantsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(cancellationToken);

        public Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableSubscriptionsAsync(tenantId, cancellationToken);

        public Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetSubscriptionAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableLocationsAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
            => _inner.GetAvailableResourceGroupsWithLocationAsync(subscriptionId, cancellationToken);

        public Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
            => _inner.GetSupportedLocationsAsync(subscriptionId, resourceType, cancellationToken);

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
            => _inner.GetRoleAssignments(scope);

        public Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Equals(resourceId, existingResourceId, StringComparison.OrdinalIgnoreCase));

        public Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
            => string.Equals(resourceId, existingResourceId, StringComparison.OrdinalIgnoreCase)
                ? Task.FromException(deleteException)
                : _inner.DeleteResourceAsync(resourceId, cancellationToken);

        public Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.CancelDeploymentAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentTargetResourceIdsAsync(deploymentId, cancellationToken);

        public IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(string deploymentId, bool recursive = true, CancellationToken cancellationToken = default)
            => _inner.GetDeploymentOperationsAsync(deploymentId, recursive, cancellationToken);
    }
}
