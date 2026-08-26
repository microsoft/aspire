// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Azure.Provisioning;
using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources.Models;

namespace Aspire.Hosting.Azure.Tests;

public class ProvisioningContextTests
{
    [Fact]
    public void ProvisioningContext_CanBeCreatedWithInterfaces()
    {
        // Arrange & Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Assert
        Assert.NotNull(context);
        Assert.NotNull(context.Credential);
        Assert.NotNull(context.ArmClient);
        Assert.NotNull(context.Subscription);
        Assert.NotNull(context.ResourceGroup);
        Assert.NotNull(context.Tenant);
        Assert.NotNull(context.Location.Name);
        Assert.NotNull(context.Principal);
    }

    [Fact]
    public void ProvisioningContext_ExposesCorrectSubscriptionProperties()
    {
        // Arrange & Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Assert
        Assert.Equal("Test Subscription", context.Subscription.DisplayName);
        Assert.Contains("subscriptions", context.Subscription.Id.ToString());
        Assert.NotNull(context.Subscription.TenantId);
    }

    [Fact]
    public void ProvisioningContext_ExposesCorrectResourceGroupProperties()
    {
        // Arrange & Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Assert
        Assert.Equal("test-rg", context.ResourceGroup.Name);
        Assert.Contains("resourceGroups", context.ResourceGroup.Id.ToString());
    }

    [Fact]
    public void ProvisioningContext_ExposesCorrectTenantProperties()
    {
        // Arrange & Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Assert
        Assert.NotNull(context.Tenant.TenantId);
        Assert.Equal("testdomain.onmicrosoft.com", context.Tenant.DefaultDomain);
    }

    [Fact]
    public void ProvisioningContext_ExposesCorrectLocationProperties()
    {
        // Arrange & Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Assert
        Assert.Equal("westus2", context.Location.Name);
        Assert.Equal("westus2", context.Location.ToString());
    }

    [Fact]
    public async Task ProvisioningContext_TokenCredential_CanGetToken()
    {
        // Arrange
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();
        var requestContext = new TokenRequestContext(["https://management.azure.com/.default"]);

        // Act
        var token = await context.Credential.GetTokenAsync(requestContext, CancellationToken.None);

        // Assert
        Assert.NotNull(token.Token);
    }

    [Fact]
    public async Task ProvisioningContext_ArmClient_CanGetSubscriptionAndTenant()
    {
        // Arrange
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Act
        var (subscription, tenant) = await context.ArmClient.GetSubscriptionAndTenantAsync();

        // Assert
        Assert.NotNull(subscription);
        Assert.Equal("Test Subscription", subscription.DisplayName);
        Assert.NotNull(tenant);
        Assert.NotNull(tenant.TenantId);
    }

    [Fact]
    public async Task ProvisioningContext_ResourceGroup_CanGetArmDeployments()
    {
        // Arrange
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Act
        var deployments = context.ResourceGroup.GetArmDeployments();
        var operation = await deployments.CreateOrUpdateAsync(
            WaitUntil.Started,
            "test-deployment",
            new ArmDeploymentContent(
                new ArmDeploymentProperties(ArmDeploymentMode.Incremental)));

        // Assert
        Assert.NotNull(deployments);
        Assert.NotNull(operation);
        Assert.True(operation.HasCompleted);
        Assert.True(operation.HasValue);
    }

    [Fact]
    public async Task ProvisioningContext_Subscription_CanGetResourceGroups()
    {
        // Arrange
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext();

        // Act
        var resourceGroups = context.Subscription.GetResourceGroups();
        var response = await resourceGroups.GetAsync("test-rg");

        // Assert
        Assert.NotNull(resourceGroups);
        Assert.NotNull(response);
        Assert.Equal("test-rg", response.Value.Name);
    }

    [Fact]
    public void ProvisioningContext_CanBeCustomized()
    {
        // Arrange
        var customPrincipal = new AzurePrincipal(Guid.NewGuid(), "custom@example.com");

        // Act
        var context = ProvisioningTestHelpers.CreateTestProvisioningContext(
            principal: customPrincipal);

        // Assert
        Assert.Equal("custom@example.com", context.Principal.Name);
    }
}

public class ProvisioningServicesTests
{
    [Fact]
    public void ProvisioningTestHelpers_CanCreateAllInterfaces()
    {
        // Arrange & Act
        var armClientProvider = ProvisioningTestHelpers.CreateArmClientProvider();
        var secretClientProvider = ProvisioningTestHelpers.CreateSecretClientProvider();
        var bicepCliExecutor = ProvisioningTestHelpers.CreateBicepCompiler();
        var userSecretsManager = ProvisioningTestHelpers.CreateUserSecretsManager();

        // Assert
        Assert.NotNull(armClientProvider);
        Assert.NotNull(secretClientProvider);
        Assert.NotNull(bicepCliExecutor);
        Assert.NotNull(userSecretsManager);
    }

    [Fact]
    public async Task TestBicepCliExecutor_ReturnsValidJson()
    {
        // Arrange
        var executor = ProvisioningTestHelpers.CreateBicepCompiler();

        // Act
        var result = await executor.CompileBicepToArmAsync("test.bicep");

        // Assert
        Assert.NotNull(result);
        Assert.Contains("contentVersion", result);
        Assert.Contains("resources", result);

        // Verify it's valid JSON
        var parsed = JsonNode.Parse(result);
        Assert.NotNull(parsed);
    }

    [Fact]
    public async Task TestUserSecretsManager_CanSaveAndLoadSection()
    {
        // Arrange
        var manager = ProvisioningTestHelpers.CreateUserSecretsManager();

        // Act
        var azureSection = await manager.AcquireSectionAsync("Azure");
        azureSection.Data["SubscriptionId"] = "test-id";
        await manager.SaveSectionAsync(azureSection);

        var loadedSection = await manager.AcquireSectionAsync("Azure");

        // Assert
        Assert.NotNull(loadedSection);
        Assert.Equal("test-id", loadedSection.Data["SubscriptionId"]?.ToString());
    }

    [Fact]
    public async Task TestAzurePrincipalProvider_CanGetPrincipal()
    {
        var provider = ProvisioningTestHelpers.CreateAzurePrincipalProvider();

        var principal = await provider.GetPrincipalAsync();

        Assert.NotNull(principal);
        Assert.NotEqual(Guid.Empty, principal.Id);
        Assert.False(string.IsNullOrEmpty(principal.Name));
    }
}
