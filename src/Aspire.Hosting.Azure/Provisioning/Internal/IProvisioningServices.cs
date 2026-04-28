// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using Azure.Security.KeyVault.Secrets;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Provides access to Azure ARM client functionality.
/// </summary>
internal interface IArmClientProvider
{
    /// <summary>
    /// Gets the ARM client for Azure resource management.
    /// </summary>
    IArmClient GetArmClient(TokenCredential credential, string subscriptionId);

    /// <summary>
    /// Gets the ARM client for Azure resource management without a specific subscription.
    /// </summary>
    IArmClient GetArmClient(TokenCredential credential);
}

/// <summary>
/// Provides access to Azure Key Vault secret client functionality.
/// </summary>
internal interface ISecretClientProvider
{
    /// <summary>
    /// Gets a secret client for the specified vault URI.
    /// </summary>
    SecretClient GetSecretClient(Uri vaultUri);
}

/// <summary>
/// Provides bicep CLI execution functionality.
/// </summary>
internal interface IBicepCompiler
{
    /// <summary>
    /// Compiles a bicep file to ARM template JSON.
    /// </summary>
    Task<string> CompileBicepToArmAsync(string bicepFilePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides provisioning context creation functionality.
/// </summary>
internal interface IProvisioningContextProvider
{
    /// <summary>
    /// Creates a provisioning context for Azure resource operations.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A provisioning context.</returns>
    Task<ProvisioningContext> CreateProvisioningContextAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for Azure ArmClient.
/// </summary>
internal interface IArmClient
{
    /// <summary>
    /// Gets the default subscription and its matching tenant.
    /// </summary>
    Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all tenants accessible to the current user.
    /// </summary>
    Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions accessible to the current user.
    /// </summary>
    Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all subscriptions accessible to the current user filtered by tenant ID.
    /// </summary>
    Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all available locations for the specified subscription.
    /// </summary>
    Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed information about available resource groups including their locations.
    /// </summary>
    Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for Azure SubscriptionResource.
/// </summary>
internal interface ISubscriptionResource
{
    /// <summary>
    /// Gets the subscription resource identifier.
    /// </summary>
    ResourceIdentifier Id { get; }

    /// <summary>
    /// Gets the subscription display name.
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// Gets the tenant ID.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Gets resource groups collection.
    /// </summary>
    IResourceGroupCollection GetResourceGroups();

    /// <summary>
    /// Gets ARM deployments collection.
    /// </summary>
    IArmDeploymentCollection GetArmDeployments();
}

/// <summary>
/// Abstraction for Azure ResourceGroupCollection.
/// </summary>
internal interface IResourceGroupCollection
{
    /// <summary>
    /// Gets a resource group.
    /// </summary>
    Task<Response<IResourceGroupResource>> GetAsync(string resourceGroupName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a resource group.
    /// </summary>
    Task<ArmOperation<IResourceGroupResource>> CreateOrUpdateAsync(WaitUntil waitUntil, string resourceGroupName, ResourceGroupData data, CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for Azure ResourceGroupResource.
/// </summary>
internal interface IResourceGroupResource
{
    /// <summary>
    /// Gets the resource group resource identifier.
    /// </summary>
    ResourceIdentifier Id { get; }

    /// <summary>
    /// Gets the resource group name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets ARM deployments collection.
    /// </summary>
    IArmDeploymentCollection GetArmDeployments();

    /// <summary>
    /// Deletes the resource group.
    /// </summary>
    Task DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists all resources in the resource group.
    /// </summary>
    /// <returns>A list of resources with their name and type.</returns>
    IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for Azure ArmDeploymentCollection.
/// </summary>
internal interface IArmDeploymentCollection
{
    /// <summary>
    /// Creates or updates a deployment.
    /// </summary>
    Task<ArmOperation<ArmDeploymentResource>> CreateOrUpdateAsync(
        WaitUntil waitUntil,
        string deploymentName,
        ArmDeploymentContent content,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Abstraction for Azure TenantResource.
/// </summary>
internal interface ITenantResource
{
    /// <summary>
    /// Gets the tenant ID.
    /// </summary>
    Guid? TenantId { get; }

    /// <summary>
    /// Gets the display name.
    /// </summary>
    string? DisplayName { get; }

    /// <summary>
    /// Gets the default domain.
    /// </summary>
    string? DefaultDomain { get; }
}

/// <summary>
/// Provides user principal retrieval functionality.
/// </summary>
internal interface IUserPrincipalProvider
{
    /// <summary>
    /// Gets the user principal.
    /// </summary>
    Task<UserPrincipal> GetUserPrincipalAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides access to Azure token credentials.
/// </summary>
internal interface ITokenCredentialProvider
{
    /// <summary>
    /// Gets the token credential for Azure authentication.
    /// </summary>
    TokenCredential TokenCredential { get; }
}
