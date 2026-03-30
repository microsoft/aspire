#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
        // Arrange
        var builder = CreateBuilder(isRunMode: true);

        // Act
        var resourceBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        var resetCommand = Assert.Single(environmentResource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ResetProvisioningStateCommandName);
        Assert.Equal("Reset provisioning state", resetCommand.DisplayName);
        Assert.Contains("not delete live Azure resources", resetCommand.DisplayDescription);
        Assert.Contains("may be left orphaned", resetCommand.ConfirmationMessage);
    }

    [Fact]
    public async Task ResetProvisioningStateCommand_ClearsCachedStateAndResetsSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
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
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure provisioning state reset.", result.Result);

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
    public async Task EnsureProvisionedAsync_UsesControllerProvisioningFlow()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
    public async Task OnBeforeStartAsync_AddsPerResourceCommandsToDeployableAzureResourcesOnly()
    {
        var builder = CreateBuilder(isRunMode: true);
        builder.AddAzureProvisioning();

        var storage = builder.AddAzureStorage("storage");
        var blobs = storage.AddBlobs("blobs");

        using var app = builder.Build();

        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var preparer = new AzureResourcePreparer(
            app.Services.GetRequiredService<IOptions<AzureProvisioningOptions>>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>());

        await preparer.OnBeforeStartAsync(new BeforeStartEvent(app.Services, model), CancellationToken.None);

        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ForgetStateCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);
        Assert.Contains(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
        Assert.DoesNotContain(blobs.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c =>
            c.Name == AzureProvisioningController.ForgetStateCommandName ||
            c.Name == AzureProvisioningController.ReprovisionResourceCommandName);
    }

    [Fact]
    public async Task ForgetStateCommand_ClearsOnlyTargetedResourceStateAndSnapshots()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource provisioning state reset.", result.Result);

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
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Result);
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
    public async Task ChangeLocationCommand_PersistsOverrideAndReprovisionsTargetedResource()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Result);
        Assert.Equal("westus2", testBicepProvisioner.ProvisionedLocations["storage"]);
        Assert.DoesNotContain("storage2", testBicepProvisioner.ProvisionedLocations.Keys);

        var storageSection = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage");
        Assert.Equal("westus2", storageSection.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ChangeLocationCommand_UsesPersistedAzureContextForSelectableLocations()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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

        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        var changeLocationCommand = Assert.Single(storage.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == AzureProvisioningController.ChangeResourceLocationCommandName);

        var executionTask = changeLocationCommand.ExecuteCommand(new ExecuteCommandContext
        {
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        var locationInput = interaction.Inputs[AzureBicepResource.KnownParameters.Location];

        Assert.Equal(InputType.Choice, locationInput.InputType);
        var options = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, string>>>(locationInput.Options);
        Assert.Contains(options, option => option.Key == "westus2");

        locationInput.Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.True(result.Success);
        Assert.Equal("Azure resource location updated and reprovisioning completed.", result.Result);
    }

    [Fact]
    public async Task ChangeLocationCommand_DeletesCachedResourceBeforeReprovisioningNewLocation()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();
        var deletedResourceIds = new List<string>();
        const string resourceId = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage26wmkwq4f4li52";

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "westus2";
        builder.Configuration["Azure:ResourceGroup"] = "test-rg";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<IArmClientProvider>();
        builder.Services.RemoveAll<ITokenCredentialProvider>();
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider([resourceId], deletedResourceIds));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
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
    public async Task ReprovisionAllCommand_PreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure reprovisioning completed.", result.Result);

        azureSection = await deploymentStateManager.AcquireSectionAsync("Azure");
        Assert.Equal("12345678-1234-1234-1234-123456789012", azureSection.Data["SubscriptionId"]?.GetValue<string>());
        Assert.Equal("westus2", azureSection.Data["Location"]?.GetValue<string>());
        Assert.Equal("test-rg", azureSection.Data["ResourceGroup"]?.GetValue<string>());
        Assert.Equal("87654321-4321-4321-4321-210987654321", azureSection.Data["TenantId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionAllCommand_NormalizesPersistedLocationOverride()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
        storage2Section.Data["Parameters"] = """{"location":{"value":"westus3"}}""";
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
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider());
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage2.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resource reprovisioning completed.", result.Result);

        Assert.Equal("westus3", testBicepProvisioner.ProvisionedLocations["storage2"]);

        var storage2Section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2");
        Assert.Equal("westus3", storage2Section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
    }

    [Fact]
    public async Task ReprovisionResourceCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            new ThrowingTestBicepProvisioner(),
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.False(result.Success);
    }

    [Fact]
    public async Task ChangeLocationCommand_FailsWhenProvisioningFails()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testInteractionService = new TestInteractionService();

        builder.Configuration["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        builder.Configuration["Azure:Location"] = "eastus";
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);
        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            new ThrowingTestBicepProvisioner(),
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = storage.Resource.Name,
            CancellationToken = CancellationToken.None
        });

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync();
        interaction.Inputs[AzureBicepResource.KnownParameters.Location].Value = "westus2";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await executionTask;

        Assert.False(result.Success);
    }

    [Fact]
    public async Task CheckForDriftAsync_MarksResourceMissingInAzure()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.Services.AddSingleton<IArmClientProvider>(ProvisioningTestHelpers.CreateArmClientProvider(existingResourceIds: []));
        builder.Services.AddSingleton<ITokenCredentialProvider>(ProvisioningTestHelpers.CreateTokenCredentialProvider());
        builder.AddAzureProvisioning();

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
        storageSection.Data["Outputs"] = """{"id":{"value":"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Storage/storageAccounts/storage"}}""";
        await deploymentStateManager.SaveSectionAsync(storageSection);

        await notifications.PublishUpdateAsync(environmentResource, state => state with { State = KnownResourceStates.Running });
        await notifications.PublishUpdateAsync(storage.Resource, state => state with { State = KnownResourceStates.Running });

        await controller.CheckForDriftAsync(model);

        Assert.True(notifications.TryGetCurrentState(environmentResource.Name, out var environmentEvent));
        Assert.Equal(AzureProvisioningController.DriftedState, environmentEvent.Snapshot.State?.Text);

        Assert.True(notifications.TryGetCurrentState(storage.Resource.Name, out var resourceEvent));
        Assert.Equal(AzureProvisioningController.MissingInAzureState, resourceEvent.Snapshot.State?.Text);
    }

    [Fact]
    public async Task DeleteAzureResourcesCommand_DeletesCurrentResourceGroupAndPreservesAzureContextState()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testBicepProvisioner = new TestBicepProvisioner();
        var resourceGroup = new TestResourceGroupResource("test-rg");
        var testProvisioningContextProvider = new TestProvisioningContextProvider(ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup));

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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
            ServiceProvider = app.Services,
            ResourceName = environmentResource.Name,
            CancellationToken = CancellationToken.None
        });

        Assert.True(result.Success);
        Assert.Equal("Azure resources deleted and provisioning state reset.", result.Result);
        Assert.Equal(1, resourceGroup.DeleteCallCount);

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
    public async Task EnsureProvisioned_WaitsForReferencedAzureResources()
    {
        var builder = CreateBuilder(isRunMode: true);
        var deploymentStateManager = new TestDeploymentStateManager();
        var testProvisioningContextProvider = new TestProvisioningContextProvider();
        var testBicepProvisioner = new BlockingTestBicepProvisioner();

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager);
        builder.AddAzureProvisioning();
        builder.Services.RemoveAll<AzureProvisioningController>();
        builder.Services.AddSingleton(sp => new AzureProvisioningController(
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<IOptions<AzureProvisionerOptions>>(),
            sp,
            testBicepProvisioner,
            deploymentStateManager,
            sp.GetRequiredService<IDistributedApplicationEventing>(),
            testProvisioningContextProvider,
            sp.GetRequiredService<IAzureProvisioningOptionsManager>(),
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            sp.GetRequiredService<ILogger<AzureProvisioningController>>()));

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

        await testBicepProvisioner.FirstProvisionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["storage"], testBicepProvisioner.ProvisionedResources);

        testBicepProvisioner.AllowFirstProvisionToComplete.TrySetResult();
        await reprovisionTask;

        Assert.Equal(["storage", "storage-roles"], testBicepProvisioner.ProvisionedResources);
    }

    [Fact]
    public void AddAzureEnvironment_CreatesDefaultName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.StartsWith("azure", resource.Name);
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

    private sealed class TestDeploymentStateManager : IDeploymentStateManager
    {
        private readonly Dictionary<string, JsonObject> _sections = new(StringComparer.Ordinal);

        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            _sections.TryGetValue(sectionName, out var existingData);
            var data = existingData?.DeepClone().AsObject() ?? [];

            return Task.FromResult(new DeploymentStateSection(sectionName, data, version: 0));
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            _sections.Remove(section.SectionName);
            return Task.CompletedTask;
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            _sections[section.SectionName] = section.Data.DeepClone().AsObject();
            return Task.CompletedTask;
        }
    }

    private sealed class TestBicepProvisioner : IBicepProvisioner
    {
        public int ConfigureResourceCallCount { get; private set; }

        public int GetOrCreateResourceCallCount { get; private set; }

        public List<string> ConfiguredResources { get; } = [];

        public List<string> ProvisionedResources { get; } = [];
        public Dictionary<string, string?> ProvisionedLocations { get; } = new(StringComparer.Ordinal);

        public Task<bool> ConfigureResourceAsync(IConfiguration configuration, AzureBicepResource resource, CancellationToken cancellationToken)
        {
            ConfigureResourceCallCount++;
            ConfiguredResources.Add(resource.Name);
            return Task.FromResult(false);
        }

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            GetOrCreateResourceCallCount++;
            ProvisionedResources.Add(resource.Name);
            ProvisionedLocations[resource.Name] = resource.Parameters.TryGetValue(AzureBicepResource.KnownParameters.Location, out var location)
                ? location?.ToString()
                : null;
            resource.Outputs["blobEndpoint"] = "https://storage.blob.core.windows.net/";
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

    private sealed class BlockingTestBicepProvisioner : IBicepProvisioner
    {
        public TaskCompletionSource FirstProvisionStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowFirstProvisionToComplete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> ProvisionedResources { get; } = [];

        public Task<bool> ConfigureResourceAsync(IConfiguration configuration, AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public async Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
        {
            ProvisionedResources.Add(resource.Name);
            if (ProvisionedResources.Count == 1)
            {
                FirstProvisionStarted.TrySetResult();
                await AllowFirstProvisionToComplete.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            resource.Outputs["blobEndpoint"] = $"https://{resource.Name}.blob.core.windows.net/";
        }
    }

    private sealed class ThrowingTestBicepProvisioner : IBicepProvisioner
    {
        public Task<bool> ConfigureResourceAsync(IConfiguration configuration, AzureBicepResource resource, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task GetOrCreateResourceAsync(AzureBicepResource resource, ProvisioningContext context, CancellationToken cancellationToken)
            => Task.FromException(new InvalidOperationException("boom"));
    }
}
