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
using Microsoft.Extensions.Configuration;
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
    public async Task ConfigureResourceAsync_DoesNotReuseOverrideOnlyDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:Deployments:storage2:LocationOverride"] = "westus3"
            })
            .Build();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");

        var provisioner = new BicepProvisioner(
            services.GetRequiredService<ResourceNotificationService>(),
            services.GetRequiredService<ResourceLoggerService>(),
            new TestBicepCliExecutor(),
            new TestSecretClientProvider(),
            services.GetRequiredService<IDeploymentStateManager>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            services.GetRequiredService<IFileSystemService>(),
            NullLogger<BicepProvisioner>.Instance);

        var reused = await provisioner.ConfigureResourceAsync(configuration, resource, CancellationToken.None);

        Assert.False(reused);
        Assert.Empty(resource.Outputs);
    }

    [Fact]
    public async Task ConfigureResourceAsync_PublishesAzureIdentityPropertiesFromCachedDeploymentState()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<IDeploymentStateManager>(ProvisioningTestHelpers.CreateUserSecretsManager());
        using var services = builder.Services.BuildServiceProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:SubscriptionId"] = "12345678-1234-1234-1234-123456789012",
                ["Azure:ResourceGroup"] = "test-rg",
                ["Azure:TenantId"] = "87654321-4321-4321-4321-210987654321",
                ["Azure:Tenant"] = "microsoft.onmicrosoft.com",
                ["Azure:Location"] = "westus2"
            })
            .Build();

        var resource = new AzureBicepResource("storage2", templateString: "output name string = 'storage2'");
        var parameters = new JsonObject();
        var checksum = BicepUtilities.GetChecksum(resource, parameters, scope: null);

        var deploymentStateManager = services.GetRequiredService<IDeploymentStateManager>();
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

        var reused = await provisioner.ConfigureResourceAsync(configuration, resource, CancellationToken.None);

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
    }

    private sealed class ThrowingArmDeploymentCollection : IArmDeploymentCollection
    {
        public Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
            WaitUntil waitUntil,
            string deploymentName,
            ArmDeploymentContent content,
            CancellationToken cancellationToken = default) =>
            throw new RequestFailedException(409, "Deployment creation failed.");
    }
}
