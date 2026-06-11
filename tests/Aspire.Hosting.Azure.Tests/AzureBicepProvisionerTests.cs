// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREFILESYSTEM001

using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Security.KeyVault.Secrets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class AzureBicepProvisionerTests
{
    [Theory]
    [InlineData("1alpha")]
    [InlineData("-alpha")]
    [InlineData("")]
    [InlineData(" alpha")]
    [InlineData("alpha 123")]
    public void WithParameterDoesNotAllowParameterNamesWhichAreInvalidBicepIdentifiers(string bicepParameterName)
    {
        Assert.Throws<ArgumentException>(() =>
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            builder.AddAzureInfrastructure("infrastructure", _ => { })
                   .WithParameter(bicepParameterName);
        });
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("a1pha")]
    [InlineData("_alpha")]
    [InlineData("__alpha")]
    [InlineData("alpha1_")]
    [InlineData("Alpha1_A")]
    public void WithParameterAllowsParameterNamesWhichAreValidBicepIdentifiers(string bicepParameterName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureInfrastructure("infrastructure", _ => { })
                .WithParameter(bicepParameterName);
    }

    [Fact]
    public async Task NestedChildResourcesShouldGetUpdated()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        using var builder = TestDistributedApplicationBuilder.Create();

        var cosmos = builder.AddAzureCosmosDB("cosmosdb");
        var db = cosmos.AddCosmosDatabase("db");
        var entries = db.AddContainer("entries", "/id");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        await app.StartAsync(cts.Token);

        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await foreach (var resourceEvent in rns.WatchAsync(cts.Token).WithCancellation(cts.Token))
        {
            if (resourceEvent.Resource == entries.Resource)
            {
                var parentProperty = resourceEvent.Snapshot.Properties.FirstOrDefault(x => x.Name == KnownProperties.Resource.ParentName)?.Value?.ToString();
                Assert.Equal("db", parentProperty);
                return;
            }
        }

        Assert.Fail();
    }

    [Fact]
    public void BicepProvisioner_CanBeInstantiated()
    {
        // Test that BicepProvisioner can be instantiated with required dependencies

        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        var services = builder.Services.BuildServiceProvider();

        var bicepExecutor = new TestBicepCliExecutor();
        var secretClientProvider = new TestSecretClientProvider();
        var tokenCredentialProvider = new TestTokenCredentialProvider();

        // Act
        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            bicepExecutor,
            secretClientProvider,
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        // Assert
        Assert.NotNull(provisioner);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_InPublishMode_ThrowsForUnknownPrincipalParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Services.AddSingleton<IDeploymentStateManager>(new MockDeploymentStateManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage-roles", templateString: "output id string = 'ok'");
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalId] = null;
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalName] = null;
        resource.Parameters[AzureBicepResource.KnownParameters.PrincipalType] = null;

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            executionContext: new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Contains("Azure principal parameter was not supplied", exception.Message);
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_UsesEffectiveResourceLocationInSnapshot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage", templateString: "output name string = 'storage'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("westus3", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.location").Value?.ToString());
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PublishesPredictedDeploymentIdBeforeDeploymentStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new ThrowingResourceGroupResource("test-rg");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PublishesSubscriptionScopedPredictedDeploymentIdAndUrlWhileWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("subscriptionDeployment", templateString: "output name string = 'subscriptionDeployment'")
        {
            Scope = new("test-rg", "test-subscription")
        };

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(subscription: new WaitingThrowingSubscriptionResource());

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        var expectedDeploymentId = "/subscriptions/12345678-1234-1234-1234-123456789012/providers/Microsoft.Resources/deployments/subscriptionDeployment";
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal(expectedDeploymentId, resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
        Assert.Equal(BicepProvisioner.GetDeploymentUrl(new ResourceIdentifier(expectedDeploymentId)), resourceEvent.Snapshot.Urls.Single(u => u.Name == "deployment").Url);
    }

    [Fact]
    public async Task ConfigureResourceAsync_DoesNotReuseOverrideOnlyDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.False(reused);
        Assert.Empty(resource.Outputs);
    }

    [Fact]
    public async Task ConfigureResourceAsync_DoesNotReuseInProgressDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey] = BicepProvisioner.DeploymentStateProvisioningStateRunning;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.False(reused);
        Assert.Empty(resource.Outputs);
    }

    [Fact]
    public async Task ConfigureResourceAsync_PublishesAzureIdentityPropertiesFromDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", CancellationToken.None);
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["ResourceGroup"] = "test-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        azureSection.Data["Tenant"] = "microsoft.onmicrosoft.com";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection, CancellationToken.None);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.True(reused);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("12345678-1234-1234-1234-123456789012", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.subscription.id").Value?.ToString());
        Assert.Equal("test-rg", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.resource.group").Value?.ToString());
        Assert.Equal("87654321-4321-4321-4321-210987654321", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.id").Value?.ToString());
        Assert.Equal("microsoft.onmicrosoft.com", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.tenant.domain").Value?.ToString());
        Assert.Equal("westus2", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.location").Value?.ToString());
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", resourceEvent.Snapshot.Properties.Single(p => p.Name == CustomResourceKnownProperties.Source).Value?.ToString());
    }

    [Fact]
    public async Task ConfigureResourceAsync_PublishesResourceGroupFromCachedDeploymentId()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var azureSection = await deploymentStateManager.AcquireSectionAsync("Azure", CancellationToken.None);
        azureSection.Data["SubscriptionId"] = "12345678-1234-1234-1234-123456789012";
        azureSection.Data["ResourceGroup"] = "environment-rg";
        azureSection.Data["TenantId"] = "87654321-4321-4321-4321-210987654321";
        azureSection.Data["Tenant"] = "microsoft.onmicrosoft.com";
        azureSection.Data["Location"] = "westus2";
        await deploymentStateManager.SaveSectionAsync(azureSection, CancellationToken.None);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data["Id"] = "/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/scoped-rg/providers/Microsoft.Resources/deployments/storage2";
        section.Data["Parameters"] = parameters.ToJsonString();
        section.Data["Outputs"] = """{"name":{"value":"storage2"}}""";
        section.Data["CheckSum"] = checksum;
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(resource, CancellationToken.None);

        Assert.True(reused);

        var notifications = services.GetRequiredService<ResourceNotificationService>();
        Assert.True(notifications.TryGetCurrentState(resource.Name, out var resourceEvent));
        Assert.Equal("scoped-rg", resourceEvent.Snapshot.Properties.Single(p => p.Name == "azure.resource.group").Value?.ToString());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PreservesLocationOverrideInDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        resource.Parameters[AzureBicepResource.KnownParameters.Location] = "westus3";

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal("westus3", section.Data[AzureProvisioningController.LocationOverrideKey]?.GetValue<string>());
        Assert.False(section.Data.ContainsKey(BicepProvisioner.DeploymentStateProvisioningStateKey));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_ClearsStaleLocationOverrideWhenEffectiveLocationChanges()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        section.Data[AzureProvisioningController.LocationOverrideKey] = "westus3";
        await deploymentStateManager.SaveSectionAsync(section, CancellationToken.None);

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(location: AzureLocation.WestUS2);

        await provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None);

        section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.False(section.Data.ContainsKey(AzureProvisioningController.LocationOverrideKey));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_SavesInProgressDeploymentStateBeforeWaiting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new WaitingThrowingArmDeploymentCollection());

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<RequestFailedException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg/providers/Microsoft.Resources/deployments/storage2", section.Data["Id"]?.GetValue<string>());
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateRunning, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
        Assert.True(section.Data.ContainsKey("Parameters"));
        Assert.True(section.Data.ContainsKey("CheckSum"));
        Assert.False(section.Data.ContainsKey("Outputs"));
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_CancelsStartedDeploymentWhenWaitIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new CancelingArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);
        Assert.Equal("storage2", deploymentCollection.CanceledDeploymentName);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_CancelsPendingDeploymentWhenStartIsCanceled()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new PendingCancelArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);
        Assert.Equal("storage2", deploymentCollection.CanceledDeploymentName);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_PersistsCanceledStateWhenCancelFindsAlreadyInactiveDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var deploymentCollection = new AlreadyCanceledArmDeploymentCollection();
        var resourceGroup = new DeploymentCollectionResourceGroupResource(deploymentCollection);

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        await Assert.ThrowsAsync<OperationCanceledException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Equal(1, deploymentCollection.CancelCallCount);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(BicepProvisioner.DeploymentStateProvisioningStateCanceled, section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task GetOrCreateResourceAsync_SavesTerminalDeploymentStateWhenDeploymentFails()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var resourceGroup = new DeploymentCollectionResourceGroupResource(new FailingArmDeploymentCollection());

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            deploymentStateManager,
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(resourceGroup: resourceGroup);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.GetOrCreateResourceAsync(resource, context, CancellationToken.None));

        Assert.Contains(ResourcesProvisioningState.Failed.ToString(), exception.Message);

        var section = await deploymentStateManager.AcquireSectionAsync("Azure:Deployments:storage2", CancellationToken.None);
        Assert.Equal(ResourcesProvisioningState.Failed.ToString(), section.Data[BicepProvisioner.DeploymentStateProvisioningStateKey]?.GetValue<string>());
    }

    [Fact]
    public async Task BicepCliExecutor_CompilesBicepToArm()
    {
        // Test the mock bicep executor behavior

        // Arrange
        var bicepExecutor = new TestBicepCliExecutor();

        // Act
        var result = await bicepExecutor.CompileBicepToArmAsync("test.bicep", CancellationToken.None);

        // Assert
        Assert.True(bicepExecutor.CompileBicepToArmAsyncCalled);
        Assert.Equal("test.bicep", bicepExecutor.LastCompiledPath);
        Assert.NotNull(result);
        Assert.Contains("$schema", result);
    }

    [Fact]
    public void SecretClientProvider_CreatesSecretClient()
    {
        // Test the mock secret client provider behavior

        // Arrange
        var secretClientProvider = new TestSecretClientProvider();
        var vaultUri = new Uri("https://test.vault.azure.net/");

        // Act
        var client = secretClientProvider.GetSecretClient(vaultUri);

        // Assert
        Assert.True(secretClientProvider.GetSecretClientCalled);
        // Client will be null in our mock, but the call was tracked
        Assert.Null(client);
    }

    [Fact]
    public void TestTokenCredential_ProvidesAccessToken()
    {
        // Test the mock token credential behavior

        // Arrange
        var tokenProvider = new TestTokenCredentialProvider();
        var credential = tokenProvider.TokenCredential;
        var requestContext = new TokenRequestContext(["https://management.azure.com/.default"]);

        // Act
        var token = credential.GetToken(requestContext, CancellationToken.None);

        // Assert
        Assert.Equal("mock-token", token.Token);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task TestTokenCredential_ProvidesAccessTokenAsync()
    {
        // Test the mock token credential async behavior

        // Arrange
        var provider = new TestTokenCredentialProvider();
        var credential = provider.TokenCredential;
        var requestContext = new TokenRequestContext(["https://management.azure.com/.default"]);

        // Act
        var token = await credential.GetTokenAsync(requestContext, CancellationToken.None);

        // Assert
        Assert.Equal("mock-token", token.Token);
        Assert.True(token.ExpiresOn > DateTimeOffset.UtcNow);
    }

    private sealed class TestTokenCredentialProvider : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential => new MockTokenCredential();

        private sealed class MockTokenCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new("mock-token", DateTimeOffset.UtcNow.AddHours(1));

            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                ValueTask.FromResult(new AccessToken("mock-token", DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestBicepCliExecutor : IBicepCompiler
    {
        public bool CompileBicepToArmAsyncCalled { get; private set; }
        public string? LastCompiledPath { get; private set; }
        public string CompilationResult { get; set; } = """{"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#"}""";

        public Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default)
        {
            CompileBicepToArmAsyncCalled = true;
            LastCompiledPath = bicepFilePath;
            return Task.FromResult(CompilationResult);
        }
    }

    private sealed class TestSecretClientProvider : ISecretClientProvider
    {
        public bool GetSecretClientCalled { get; private set; }

        public SecretClient GetSecretClient(Uri vaultUri)
        {
            GetSecretClientCalled = true;
            // Return null - this will fail in actual secret operations but allows testing the call
            return null!;
        }
    }

    private sealed class MockDeploymentStateManager : IDeploymentStateManager
    {
        public string? StateFilePath => null;

        public Task<DeploymentStateSection> AcquireSectionAsync(string sectionName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new DeploymentStateSection(sectionName, [], 0));
        }

        public Task DeleteSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SaveSectionAsync(DeploymentStateSection section, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ClearAllStateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class DeploymentCollectionResourceGroupResource(IArmDeploymentCollection armDeploymentCollection) : IResourceGroupResource
    {
        public ResourceIdentifier Id { get; } = new("/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/test-rg");
        public string Name => "test-rg";

        public IArmDeploymentCollection GetArmDeployments() => armDeploymentCollection;

        public Task<ArmOperation> DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation>(new TestDeleteArmOperation());

        public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ThrowingResourceGroupResource(string name) : IResourceGroupResource
    {
        private int _deleteCallCount;

        public int DeleteCallCount => _deleteCallCount;

        public ResourceIdentifier Id => new($"/subscriptions/12345678-1234-1234-1234-123456789012/resourceGroups/{name}");
        public string Name => name;

        public IArmDeploymentCollection GetArmDeployments() => new ThrowingArmDeploymentCollection();

        public Task<ArmOperation> DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default)
        {
            _deleteCallCount++;
            return Task.FromResult<ArmOperation>(new TestDeleteArmOperation());
        }

        public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class WaitingThrowingSubscriptionResource : ISubscriptionResource
    {
        public ResourceIdentifier Id { get; } = new("/subscriptions/12345678-1234-1234-1234-123456789012");

        public string? DisplayName => "Test Subscription";

        public Guid? TenantId => Guid.Parse("87654321-4321-4321-4321-210987654321");

        public IResourceGroupCollection GetResourceGroups() => new TestResourceGroupCollection();

        public IArmDeploymentCollection GetArmDeployments() => new WaitingThrowingArmDeploymentCollection();
    }

    private sealed class WaitingThrowingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new WaitingThrowingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class WaitingThrowingArmDeploymentOperation : ArmOperation<ArmDeploymentResource>
    {
        public override string Id { get; } = Guid.NewGuid().ToString();

        public override ArmDeploymentResource Value => throw new InvalidOperationException("The deployment did not complete successfully.");

        public override bool HasCompleted => false;

        public override bool HasValue => false;

        public override Response GetRawResponse() => new MockResponse(200);

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => new MockResponse(200);

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Response>(new MockResponse(200));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new RequestFailedException(409, "Deployment wait failed."));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new RequestFailedException(409, "Deployment wait failed."));

        public override Response<ArmDeploymentResource> WaitForCompletion(CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment wait failed.");

        public override Response<ArmDeploymentResource> WaitForCompletion(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment wait failed.");
    }

    private sealed class ThrowingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment creation failed.");

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CancelingArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }
        public string? CanceledDeploymentName { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new CancelingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            CanceledDeploymentName = deploymentName;
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingArmDeploymentOperation : ArmOperation<ArmDeploymentResource>
    {
        public override string Id { get; } = Guid.NewGuid().ToString();

        public override ArmDeploymentResource Value => throw new InvalidOperationException("The deployment did not complete successfully.");

        public override bool HasCompleted => false;

        public override bool HasValue => false;

        public override Response GetRawResponse() => new MockResponse(200);

        public override Response UpdateStatus(CancellationToken cancellationToken = default) => new MockResponse(200);

        public override ValueTask<Response> UpdateStatusAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<Response>(new MockResponse(200));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public override ValueTask<Response<ArmDeploymentResource>> WaitForCompletionAsync(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Response<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public override Response<ArmDeploymentResource> WaitForCompletion(CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);

        public override Response<ArmDeploymentResource> WaitForCompletion(TimeSpan pollingInterval, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException(cancellationToken);
    }

    private sealed class PendingCancelArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }
        public string? CanceledDeploymentName { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ArmOperation<ArmDeploymentResource>>(new OperationCanceledException(cancellationToken));

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            CanceledDeploymentName = deploymentName;
            return Task.CompletedTask;
        }
    }

    private sealed class AlreadyCanceledArmDeploymentCollection : IArmDeploymentCollection
    {
        public int CancelCallCount { get; private set; }

        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new CancelingArmDeploymentOperation());

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            throw new RequestFailedException(409, "The deployment is already canceled.");
        }
    }

    private sealed class FailingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ArmOperation<ArmDeploymentResource>>(new TestArmOperation<ArmDeploymentResource>(
                new TestArmDeploymentResource(deploymentName, [], ResourcesProvisioningState.Failed)));

        public Task CancelAsync(string deploymentName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
