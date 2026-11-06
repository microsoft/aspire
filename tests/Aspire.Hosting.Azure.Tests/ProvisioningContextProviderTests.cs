// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Tests;

public class ProvisioningContextProviderTests
{
    private readonly TestInteractionService _defaultInteractionService = new() { IsAvailable = false };

    [Theory]
    [InlineData("dev-a")]
    [InlineData("")]
    [InlineData(null)]
    public async Task PublishMode_TenantResourceSuffix_PromptsOnceAndPersists(string? selectedSuffix)
    {
        var interactionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions();
        var deploymentState = ProvisioningTestHelpers.CreateUserSecretsManager();
        var resource = new AzureBicepResource("registration", templateString: "param tenantResourceSuffix string = ''");
        resource.Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary<string, string>()));
        var provider = CreatePublishModeProvider(interactionService, options, deploymentState, new([resource]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var promptTask = provider.RetrieveAzureProvisioningOptions(cts.Token);
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);
        Assert.Equal(AzureProvisioningStrings.InputsTitle, interaction.Title);
        Assert.Equal(AzureProvisioningStrings.TenantResourceSuffixSelectionMessage, interaction.Message);
        var input = Assert.Single(interaction.Inputs);
        Assert.Equal(BaseProvisioningContextProvider.TenantResourceSuffixName, input.Name);
        Assert.Equal(AzureProvisioningStrings.TenantResourceSuffixLabel, input.Label);
        Assert.Equal(InputType.Text, input.InputType);
        Assert.False(input.Required);
        input.Value = selectedSuffix;
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));
        await promptTask.WaitAsync(cts.Token);

        Assert.Equal(selectedSuffix ?? string.Empty, options.Value.TenantResourceSuffix);
        await provider.CreateProvisioningContextAsync(cts.Token).WaitAsync(cts.Token);
        var saved = await deploymentState.AcquireSectionAsync("Azure", cts.Token);
        Assert.Equal(selectedSuffix ?? string.Empty, saved.Data["TenantResourceSuffix"]!.GetValue<string>());
        Assert.False(interactionService.Interactions.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(true, false, true, "configured")]
    [InlineData(true, false, true, "")]
    [InlineData(false, false, true, null)]
    [InlineData(true, true, true, null)]
    [InlineData(true, false, false, null)]
    public async Task PublishMode_TenantResourceSuffix_SkipsUnnecessaryPrompt(
        bool tenantScoped, bool excluded, bool interactive, string? configuredSuffix)
    {
        var interactionService = new TestInteractionService { IsAvailable = interactive };
        var options = ProvisioningTestHelpers.CreateOptions();
        options.Value.TenantResourceSuffix = configuredSuffix;
        var resource = new AzureBicepResource("registration", templateString: "param tenantResourceSuffix string = ''");
        if (tenantScoped)
        {
            resource.Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary<string, string>()));
        }
        if (excluded)
        {
            resource.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        }
        var provider = CreatePublishModeProvider(
            interactionService, options, ProvisioningTestHelpers.CreateUserSecretsManager(), new([resource]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await provider.RetrieveAzureProvisioningOptions(cts.Token).WaitAsync(cts.Token);

        Assert.Equal(configuredSuffix, options.Value.TenantResourceSuffix);
        Assert.False(interactionService.Interactions.Reader.TryRead(out _));
    }

    [Fact]
    public async Task PublishMode_TenantResourceSuffix_CancellationStopsContextCreation()
    {
        var interactionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions();
        var resource = new AzureBicepResource("registration", templateString: "param tenantResourceSuffix string = ''");
        resource.Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary<string, string>()));
        var provider = CreatePublishModeProvider(
            interactionService, options, ProvisioningTestHelpers.CreateUserSecretsManager(), new([resource]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var contextTask = provider.CreateProvisioningContextAsync(cts.Token);
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);
        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());

        await Assert.ThrowsAsync<OperationCanceledException>(() => contextTask.WaitAsync(cts.Token));
        Assert.Null(options.Value.TenantResourceSuffix);
    }

    private static PublishModeProvisioningContextProvider CreatePublishModeProvider(
        TestInteractionService interactionService,
        IOptions<AzureProvisionerOptions> options,
        IDeploymentStateManager deploymentState,
        DistributedApplicationModel model) =>
        new(interactionService,
            options,
            ProvisioningTestHelpers.CreateEnvironment(),
            ProvisioningTestHelpers.CreateLogger<PublishModeProvisioningContextProvider>(),
            ProvisioningTestHelpers.CreateArmClientProvider(),
            ProvisioningTestHelpers.CreateAzurePrincipalProvider(),
            ProvisioningTestHelpers.CreateTokenCredentialProvider(),
            deploymentState,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            new NullPublishingActivityReporter(),
            model);

    [Theory]
    [InlineData(false, false, null, "")]
    [InlineData(true, true, null, "")]
    [InlineData(true, false, null, "dev-a")]
    [InlineData(true, false, "configured", "")]
    public async Task TenantResourceSuffix_IsConditionallyPromptedAndPersisted(
        bool usesSuffix, bool excluded, string? configuredSuffix, string selectedSuffix)
    {
        var interactionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions();
        options.Value.TenantResourceSuffix = configuredSuffix;
        var deploymentState = ProvisioningTestHelpers.CreateUserSecretsManager();
        var resource = new AzureBicepResource("registration", templateString: "param tenantResourceSuffix string = ''");
        if (usesSuffix)
        {
            resource.Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary<string, string>()));
        }
        if (excluded)
        {
            resource.Annotations.Add(ManifestPublishingCallbackAnnotation.Ignore);
        }
        var model = new DistributedApplicationModel([resource]);
        using var provider = new RunModeProvisioningContextProvider(
            interactionService,
            options,
            ProvisioningTestHelpers.CreateEnvironment(),
            ProvisioningTestHelpers.CreateLogger(),
            ProvisioningTestHelpers.CreateArmClientProvider(),
            ProvisioningTestHelpers.CreateAzurePrincipalProvider(),
            ProvisioningTestHelpers.CreateTokenCredentialProvider(),
            deploymentState,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            model);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var promptTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, cts.Token);
        var interaction = await interactionService.Interactions.Reader.ReadAsync(cts.Token);
        Assert.Equal(usesSuffix && !excluded ? 5 : 4, interaction.Inputs.Count);
        if (usesSuffix && !excluded)
        {
            var input = interaction.Inputs[BaseProvisioningContextProvider.TenantResourceSuffixName];
            Assert.Equal(InputType.Text, input.InputType);
            Assert.False(input.Required);
            Assert.Equal(AzureProvisioningStrings.TenantResourceSuffixLabel, input.Label);
            Assert.Equal(AzureProvisioningStrings.TenantResourceSuffixDescription, input.Description);
            Assert.Equal(configuredSuffix, input.Value);
            input.Value = selectedSuffix;
        }
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));
        Assert.True(await promptTask);
        await provider.PersistProvisioningOptionsAsync(cts.Token);

        var saved = await deploymentState.AcquireSectionAsync("Azure", cts.Token);
        Assert.Equal(usesSuffix && !excluded ? selectedSuffix : configuredSuffix,
            saved.Data["TenantResourceSuffix"]?.GetValue<string>());
        Assert.True(await provider.EnsureProvisioningOptionsAsync(forcePrompt: false, cts.Token));
        Assert.Equal(usesSuffix && !excluded ? selectedSuffix : configuredSuffix,
            (await provider.GetProvisioningOptionsAsync(cts.Token)).TenantResourceSuffix);
    }

    [Fact]
    public async Task TenantResourceSuffix_CommandUpdatePreservesOrClearsSavedValue()
    {
        var options = ProvisioningTestHelpers.CreateOptions();
        options.Value.TenantResourceSuffix = "configured";
        var deploymentState = ProvisioningTestHelpers.CreateUserSecretsManager();
        using var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            ProvisioningTestHelpers.CreateEnvironment(),
            ProvisioningTestHelpers.CreateLogger(),
            ProvisioningTestHelpers.CreateArmClientProvider(),
            ProvisioningTestHelpers.CreateAzurePrincipalProvider(),
            ProvisioningTestHelpers.CreateTokenCredentialProvider(),
            deploymentState,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        var updated = await provider.ApplyProvisioningOptionsAsync(new(null, null, null, null, "dev-a"));
        Assert.Equal("dev-a", updated.TenantResourceSuffix);
        updated = await provider.ApplyProvisioningOptionsAsync(new(null, null, null, null, null));
        Assert.Equal("dev-a", updated.TenantResourceSuffix);
        updated = await provider.ApplyProvisioningOptionsAsync(new(null, null, null, null, string.Empty));
        Assert.Equal(string.Empty, updated.TenantResourceSuffix);
        Assert.Equal(string.Empty, (await provider.GetProvisioningOptionsAsync()).TenantResourceSuffix);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_ReturnsValidContext()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
    public async Task CreateProvisioningContextAsync_RehydratesStringBooleanAllowResourceGroupCreation()
    {
        var optionValues = new AzureProvisionerOptions();
        var options = Options.Create(optionValues);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();
        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", CancellationToken.None);
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["Location"] = "westus3";
        azureSection.Data["ResourceGroup"] = "rehydrated-rg";
        azureSection.Data["AllowResourceGroupCreation"] = "true";
        await deploymentStateManager.SaveSectionAsync(azureSection, CancellationToken.None);

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        Assert.Equal("westus3", context.Location.Name);
        Assert.Equal("rehydrated-rg", context.ResourceGroup.Name);
        Assert.True(optionValues.AllowResourceGroupCreation);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_ThrowsWhenLocationMissing()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions(location: null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            new TestInteractionService() { IsAvailable = false },
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.ResourceGroup);
        Assert.Equal(resourceGroupName, context.ResourceGroup.Name);
    }

    [Fact]
    public async Task CreateProvisioningContextAsync_RetrievesPrincipal()
    {
        // Arrange
        var options = ProvisioningTestHelpers.CreateOptions();
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.Tenant);
        Assert.Equal(Guid.Parse("87654321-4321-4321-4321-210987654321"), context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
    }

    [Fact]
    public async Task EnsureProvisioningOptionsAsync_PromptsIfNoOptions()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(null, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));
        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal(AzureProvisioningStrings.InputsTitle, inputsInteraction.Title);
        var interactionOptions = Assert.IsType<InputsDialogInteractionOptions>(inputsInteraction.Options);
        Assert.True(interactionOptions.EnableMessageMarkdown);
        Assert.Equal(AzureProvisioningStrings.InputsPrimaryButtonText, interactionOptions.PrimaryButtonText);
        Assert.Equal(AzureProvisioningStrings.InputsSecondaryButtonText, interactionOptions.SecondaryButtonText);

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
                Assert.Equal(InputType.Text, input.InputType);
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

        Assert.True(await ensureTask);
        await provider.PersistProvisioningOptionsAsync(CancellationToken.None);

        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(context.Tenant);
        Assert.Equal(Guid.Parse("87654321-4321-4321-4321-210987654321"), context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012", context.Subscription.Id.ToString());
        Assert.Equal("westus", context.Location.Name);
        Assert.Equal("test-new-rg", context.ResourceGroup.Name);
    }

    [Fact]
    public async Task EnsureProvisioningOptionsAsync_Prompt_ValidatesSubAndResourceGroup()
    {
        var testInteractionService = new TestInteractionService();
        var options = ProvisioningTestHelpers.CreateOptions(null, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

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

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
    }

    [Fact]
    public async Task EnsureProvisioningOptionsAsync_IncludesTenantWhenSubscriptionIdProvided()
    {
        // Arrange
        var testInteractionService = new TestInteractionService();
        var subscriptionId = "12345678-1234-1234-1234-123456789012";
        var options = ProvisioningTestHelpers.CreateOptions(subscriptionId, null, null);
        var environment = ProvisioningTestHelpers.CreateEnvironment();
        var logger = ProvisioningTestHelpers.CreateLogger();
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();
        Assert.Equal(AzureProvisioningStrings.InputsTitle, inputsInteraction.Title);
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
                Assert.True(input.Disabled);
                Assert.True(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.ResourceGroupName, input.Name);
                Assert.Equal("Resource group", input.Label);
                Assert.Equal(InputType.Text, input.InputType);
                Assert.False(input.Required);
            },
            input =>
            {
                Assert.Equal(BaseProvisioningContextProvider.LocationName, input.Name);
                Assert.Equal("Location", input.Label);
                Assert.Equal(InputType.Choice, input.InputType);
                Assert.True(input.Required);
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

        Assert.True(await ensureTask);
        await provider.PersistProvisioningOptionsAsync(CancellationToken.None);

        var context = await provider.CreateProvisioningContextAsync(CancellationToken.None);

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new PublishModeProvisioningContextProvider(
            _defaultInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            new NullPublishingActivityReporter(),
            new DistributedApplicationModel([]));

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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert that subscription ID input starts disabled when not configured
        Assert.True(subscriptionInput.Disabled, "Subscription ID input should be disabled initially when not configured");
        Assert.NotNull(subscriptionInput.DynamicLoading);
        Assert.Equal(InputType.Choice, subscriptionInput.InputType);

        // Assert Resource Group input starts with the generated default used by the configure dialog.
        var resourceGroupInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName];
        Assert.StartsWith("rg-aspire-testapp-", resourceGroupInput.Value, StringComparison.Ordinal);

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Assert that subscription ID has dynamic loading that depends on tenant
        Assert.NotNull(subscriptionInput.DynamicLoading);
        Assert.NotNull(subscriptionInput.DynamicLoading.DependsOnInputs);
        var dependsOnInputs = Assert.Single(subscriptionInput.DynamicLoading.DependsOnInputs);
        Assert.Equal(BaseProvisioningContextProvider.TenantName, dependsOnInputs);

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

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

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the subscription input
        var subscriptionInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName];

        // Forced configuration lets users change the subscription, but it stays disabled until tenant selection.
        Assert.NotNull(subscriptionInput.DynamicLoading);
        Assert.True(subscriptionInput.Disabled, "Subscription ID input should start disabled until tenant selection");
        Assert.Equal(InputType.Choice, subscriptionInput.InputType);
        Assert.Equal(subscriptionId, subscriptionInput.Value);

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
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
        var azurePrincipalProvider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();
        var tokenCredentialProvider = ProvisioningTestHelpers.CreateTokenCredentialProvider();
        var deploymentStateManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        var provider = new RunModeProvisioningContextProvider(
            testInteractionService,
            options,
            environment,
            logger,
            armClientProvider,
            azurePrincipalProvider,
            tokenCredentialProvider,
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            new DistributedApplicationModel([]));

        // Act
        var ensureTask = provider.EnsureProvisioningOptionsAsync(forcePrompt: true, CancellationToken.None);

        // Wait for the inputs interaction
        var inputsInteraction = await testInteractionService.Interactions.Reader.ReadAsync();

        // Find the resource group input
        var resourceGroupInput = inputsInteraction.Inputs[BaseProvisioningContextProvider.ResourceGroupName];

        Assert.StartsWith("rg-aspire-testapp-", resourceGroupInput.Value, StringComparison.Ordinal);

        // Set subscription ID to trigger resource group loading
        inputsInteraction.Inputs[BaseProvisioningContextProvider.SubscriptionIdName].Value = "12345678-1234-1234-1234-123456789012";

        Assert.Null(resourceGroupInput.DynamicLoading);
        Assert.Equal(InputType.Text, resourceGroupInput.InputType);

        inputsInteraction.CompletionTcs.SetResult(InteractionResult.Cancel<InteractionInputCollection>());
        Assert.False(await ensureTask);
    }
}
