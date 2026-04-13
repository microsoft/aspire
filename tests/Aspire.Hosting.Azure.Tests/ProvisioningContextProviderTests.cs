// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES001

using System.Reflection;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class ProvisioningContextProviderTests
{
    private readonly TestInteractionService _defaultInteractionService = new() { IsAvailable = false };

    [Fact]
    public async Task CreateProvisioningContextAsync_ReturnsValidContext()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Credential);
        Assert.NotNull(context.ArmClient);
        Assert.NotNull(context.Subscription);
        Assert.NotNull(context.ResourceGroup);
        Assert.NotNull(context.Tenant);
        Assert.NotNull(context.Location.DisplayName);
        Assert.NotNull(context.Principal);
        Assert.Equal("westus2", context.Location.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_ThrowsWhenSubscriptionIdMissing()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MissingConfigurationException>(
            () => provider.CreateProvisioningContextAsync(CancellationToken.None));
        Assert.Contains("Azure subscription id is required", exception.Message);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_DoesNotReuseStaleInMemoryOptionsAfterReset()
    {
        // Arrange
        var optionValues = new AzureProvisionerOptions();
        var options = Options.Create(optionValues);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Simulate previously prompted values still hanging around in memory after reset.
        optionValues.SubscriptionId = "12345678-1234-1234-1234-123456789012";
        optionValues.Location = "westus2";
        optionValues.ResourceGroup = "stale-rg";
        optionValues.AllowResourceGroupCreation = true;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MissingConfigurationException>(
            () => provider.CreateProvisioningContextAsync(CancellationToken.None));
        Assert.Contains("Azure subscription id is required", exception.Message);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_ThrowsWhenLocationMissing()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions(location: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            new TestInteractionService() { IsAvailable = false },
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<MissingConfigurationException>(
            () => provider.CreateProvisioningContextAsync(CancellationToken.None));
        Assert.Contains("azure location/region is required", exception.Message);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_GeneratesResourceGroupNameWhenNotProvided()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions(resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.ResourceGroup);
        Assert.NotNull(context.ResourceGroup.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_UsesProvidedResourceGroupName()
    {
        // Arrange
        var resourceGroupName = "my-custom-rg";
        var options = ProvisioningTestHelpers.CreateOptions(resourceGroup: resourceGroupName);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.ResourceGroup);
        Assert.Equal(resourceGroupName, context.ResourceGroup.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_RetrievesUserPrincipal()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.Principal);
        Assert.Equal("test@example.com", context.Principal.Name);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), context.Principal.Id);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_SetsCorrectTenant()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.Tenant);
        Assert.Equal(Guid.Parse("87654321-4321-4321-4321-210987654321"), context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_PromptsIfNoOptions()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(null, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));
        // Act
        var createTask = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure provisioning", messageBarInteraction.Title);

        // Complete the message bar interaction to proceed to inputs dialog
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));// Data = true (user clicked Enter Values)

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure provisioning", inputsInteraction.Title);
        Assert.True(inputsInteraction.Options!.EnableMessageMarkdown);

        Assert.Collection(inputsInteraction.Inputs,
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.TenantName, input.Name);
                Assert.Equal("Tenant ID", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.SubscriptionIdName, input.Name);
                Assert.Equal("Subscription ID", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.ResourceGroupName, input.Name);
                Assert.Equal("Resource group", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.LocationName, input.Name);
                Assert.Equal("Location", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            });

        inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName].Value = "12345678-1234-1234-1234-123456789012";

        // Trigger dynamic update of resource groups based on subscription.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // Set a custom resource group name (new resource group)
        inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName].Value = "test-new-rg";

        // Trigger dynamic update of locations based on subscription and resource group.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Value = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Options!.First(kvp => kvp.Key == "westus").Value;

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        // Wait for the create task to complete
        var context = await createTask;

        // Assert
        Assert.NotNull(context.Tenant);
        Assert.Equal(Guid.Parse("87654321-4321-4321-4321-210987654321"), context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012", context.Subscription.Id.ToString());
        Assert.Equal("westus", context.Location.Name);
        Assert.Equal("test-new-rg", context.ResourceGroup.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_Prompt_ValidatesSubAndResourceGroup()
    {
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(null, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        var createTask = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        // Complete the message bar interaction to proceed to inputs dialog
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));// Data = true (user clicked Enter Values)

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName].Value = "not a guid";

        // Trigger dynamic update of locations based on subscription.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Value = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Options!.First(kvp => kvp.Key == "westus").Value;
        inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName].Value = "invalid group";

        var context = new InputsDialogValidationContext
        {
            CancellationToken = CancellationToken.None,
            Services = new ServiceCollection().BuildServiceProvider(),
            Inputs = inputsInteraction.Inputs
        };

        var inputOptions = Assert.IsType<InputsDialogInteractionOptions>(inputsInteraction.Options);
        Assert.NotNull(inputOptions.ValidationCallback);
        await inputOptions.ValidationCallback(context);

        Assert.True((bool)context.GetType().GetProperty("HasErrors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context, null)!);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_DoesNotPromptForTenantWhenSubscriptionIdProvided()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var subscriptionId = "12345678-1234-1234-1234-123456789012";
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var createTask = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure provisioning", messageBarInteraction.Title);

        // Complete the message bar interaction to proceed to inputs dialog
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true)); // Data = true (user clicked Enter Values)

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal("Azure provisioning", inputsInteraction.Title);
        Assert.True(inputsInteraction.Options!.EnableMessageMarkdown);

        // Assert that only 3 inputs are present (no tenant input since subscription is provided)
        Assert.Collection(inputsInteraction.Inputs,
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.SubscriptionIdName, input.Name);
                Assert.Equal("Subscription ID", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.True(input.Disabled);
                Assert.True(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.ResourceGroupName, input.Name);
                Assert.Equal("Resource group", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.LocationName, input.Name);
                Assert.Equal("Location", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
            });

        // Trigger dynamic update of resource groups based on subscription.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // Set a custom resource group name
        inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName].Value = "test-new-rg";

        // Trigger dynamic update of locations based on subscription and resource group.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // Trigger dynamic update of locations based on subscription.
        await inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName],
            Services = new ServiceCollection().BuildServiceProvider()
        });

        inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Value = inputsInteraction.Inputs[BaseProvisioningContextProvider.LocationName].Options!.First(kvp => kvp.Key == "westus").Value;

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Ok(inputsInteraction.Inputs));

        // Wait for the create task to complete
        var context = await createTask;

        // Assert
        Assert.NotNull(context.Tenant);
        Assert.Equal(Guid.Parse("87654321-4321-4321-4321-210987654321"), context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012", context.Subscription.Id.ToString());
        Assert.Equal("westus", context.Location.Name);
        Assert.Equal("test-new-rg", context.ResourceGroup.Name);
    }

    [Fact]
    public async Task PublishMode_CreateProvisioningContextAsync_ReturnsValidContext()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger<PublishModeProvisioningContextProvider>();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new PublishModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            new NullPublishingActivityReporter());

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Credential);
        Assert.NotNull(context.ArmClient);
        Assert.NotNull(context.Subscription);
        Assert.NotNull(context.ResourceGroup);
        Assert.NotNull(context.Tenant);
        Assert.NotNull(context.Location.DisplayName);
        Assert.NotNull(context.Principal);
        Assert.Equal("westus2", context.Location.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_SubscriptionInputStartsDisabledWhenNotConfigured()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId: null, location: null, resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        _ = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert that subscription ID input starts disabled when not configured
        Assert.True(subscriptionInput.Disabled, "Subscription ID input should be disabled initially when not configured");
        Assert.NotNull(subscriptionInput.DynamicLoading);
        Assert.Equal(InputType.Choice, subscriptionInput.InputType);

        // Assert Resource Group input has no default value initially
        var resourceGroupInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName];
        Assert.Null(resourceGroupInput.Value);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_SubscriptionInputDependsOnTenantWhenNotConfigured()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId: null, location: null, resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        _ = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert that subscription ID has dynamic loading that depends on tenant
        Assert.NotNull(subscriptionInput.DynamicLoading);
        Assert.NotNull(subscriptionInput.DynamicLoading.DependsOnInputs);
        var dependsOnInputs = Assert.Single(subscriptionInput.DynamicLoading.DependsOnInputs);
        Assert.Equal(BaseProvisioningContextProvider.TenantName, dependsOnInputs);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_SubscriptionInputBecomesEnabledAfterTenantSelection()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId: null, location: null, resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        var createTask = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Set tenant ID
        inputsInteraction.Inputs[BaseProvisioningContextProvider.TenantName].Value = "87654321-4321-4321-4321-210987654321";

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert subscription is initially disabled
        Assert.True(subscriptionInput.Disabled);

        // Trigger dynamic loading callback for subscription based on tenant selection
        await subscriptionInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = subscriptionInput,
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // Assert that subscription input is now enabled after tenant selection
        Assert.False(subscriptionInput.Disabled, "Subscription ID input should be enabled after tenant selection");
        Assert.NotNull(subscriptionInput.Options);
        Assert.NotEmpty(subscriptionInput.Options);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_SubscriptionInputHasNoDynamicLoadingWhenConfigured()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var subscriptionId = "12345678-1234-1234-1234-123456789012";
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId, location: null, resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        _ = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert that subscription ID has no dynamic loading when pre-configured
        Assert.Null(subscriptionInput.DynamicLoading);
        Assert.True(subscriptionInput.Disabled, "Subscription ID input should remain disabled when pre-configured");
        Assert.Equal(InputType.Text, subscriptionInput.InputType);
        Assert.Equal(subscriptionId, subscriptionInput.Value);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_ResourceGroupHasNoDefaultValueInitially()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId: null, location: null, resourceGroup: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var userPrincipalProvider = ProvisioningTestHelpers.CreateUserPrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            userPrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run));

        // Act
        _ = provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert - Wait for the first interaction (message bar)
        var messageBarInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        messageBarInteraction.CompletionTcs.SetResult(InteractionResult.Ok(true));

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the resource group input
        var resourceGroupInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName];

        // Resource group should have no default value initially (PR #13065 change)
        Assert.Null(resourceGroupInput.Value);

        // Set subscription ID to trigger resource group loading
        inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName].Value = "12345678-1234-1234-1234-123456789012";

        // Trigger dynamic loading callback for resource group
        await resourceGroupInput.DynamicLoading!.LoadCallback(new LoadInputContext
        {
            AllInputs = inputsInteraction.Inputs,
            CancellationToken = CancellationToken.None,
            Input = resourceGroupInput,
            Services = new ServiceCollection().BuildServiceProvider()
        });

        // After dynamic loading with existing resource groups found, value should still be null
        // Default value is only set when NO existing resource groups are found
        Assert.Null(resourceGroupInput.Value);
        Assert.NotEmpty(resourceGroupInput.Options!);
    }
}
