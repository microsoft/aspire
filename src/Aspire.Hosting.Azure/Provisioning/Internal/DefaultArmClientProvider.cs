// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Authorization;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IArmClientProvider"/>.
/// </summary>
internal sealed class DefaultArmClientProvider : IArmClientProvider
{
    private static readonly TimeSpan s_keyVaultPurgePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_keyVaultPurgeTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_keyVaultDeletePollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_keyVaultDeleteTimeout = TimeSpan.FromMinutes(10);

    private readonly ArmClientOptions? _options;
    private readonly TimeProvider _timeProvider;

    public DefaultArmClientProvider()
    {
        _timeProvider = TimeProvider.System;
    }

    public DefaultArmClientProvider(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        _timeProvider = timeProvider;
    }

    internal DefaultArmClientProvider(ArmClientOptions options, TimeProvider? timeProvider = null)
    {
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
    {
        var armClient = new ArmClient(credential, subscriptionId, _options);
        return new DefaultArmClient(armClient, _timeProvider);
    }

    public IArmClient GetArmClient(TokenCredential credential)
    {
        var armClient = new ArmClient(credential, default, _options);
        return new DefaultArmClient(armClient, _timeProvider);
    }

    private sealed class DefaultArmClient(ArmClient armClient, TimeProvider timeProvider) : IArmClient
    {
        private const string KeyVaultResourceType = "Microsoft.KeyVault/vaults";

        public async Task<(ISubscriptionResource subscription, ITenantResource tenant)> GetSubscriptionAndTenantAsync(CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetDefaultSubscriptionAsync(cancellationToken).ConfigureAwait(false);
            var subscriptionResource = new DefaultSubscriptionResource(subscription);

            ITenantResource? tenantResource = null;

            await foreach (var tenant in armClient.GetTenants().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (tenant.Data.TenantId == subscriptionResource.TenantId)
                {
                    tenantResource = new DefaultTenantResource(tenant);
                    break;
                }
            }

            if (tenantResource is null)
            {
                throw new InvalidOperationException($"Could not find tenant id {subscriptionResource.TenantId} for subscription {subscriptionResource.DisplayName}.");
            }

            return (subscriptionResource, tenantResource);
        }

        public async Task<IEnumerable<ITenantResource>> GetAvailableTenantsAsync(CancellationToken cancellationToken = default)
        {
            var tenants = new List<ITenantResource>();

            await foreach (var tenant in armClient.GetTenants().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                tenants.Add(new DefaultTenantResource(tenant));
            }

            return tenants;
        }

        public async Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(CancellationToken cancellationToken = default)
        {
            var subscriptions = new List<ISubscriptionResource>();

            await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                subscriptions.Add(new DefaultSubscriptionResource(subscription));
            }

            return subscriptions;
        }

        public async Task<IEnumerable<ISubscriptionResource>> GetAvailableSubscriptionsAsync(string? tenantId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(tenantId))
            {
                return await GetAvailableSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
            }

            var subscriptions = new List<ISubscriptionResource>();

            await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                // Filter subscriptions by tenant ID
                if (subscription.Data.TenantId?.ToString().Equals(tenantId, StringComparisons.AzureTenantId) == true)
                {
                    subscriptions.Add(new DefaultSubscriptionResource(subscription));
                }
            }

            return subscriptions;
        }

        public async Task<ISubscriptionResource> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

            return new DefaultSubscriptionResource(subscription.Value);
        }

        public async Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);

            // Azure locations are ARM protocol values, so keep option ordering deterministic
            // instead of allowing the current UI culture to change the sort order.
            return GetAvailableLocations(subscription.Value, cancellationToken).OrderBy(static l => l.DisplayName, StringComparers.AzureLocation);
        }

        public async Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            var resourceGroups = new List<(string Name, string Location)>();

            await foreach (var resourceGroup in subscription.Value.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                resourceGroups.Add((resourceGroup.Data.Name, resourceGroup.Data.Location.Name));
            }

            return resourceGroups.OrderBy(static rg => rg.Name, StringComparers.AzureResourceGroupName);
        }

        public async Task<IEnumerable<string>> GetSupportedLocationsAsync(string subscriptionId, string resourceType, CancellationToken cancellationToken = default)
        {
            if (!TrySplitResourceType(resourceType, out var providerNamespace, out var providerResourceType))
            {
                return [];
            }

            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            // Use the ArmClient pipeline so this metadata request follows the same ArmEnvironment
            // endpoint and token scope as the rest of provisioning. The SDK requests provider
            // metadata equivalent to:
            //   GET /subscriptions/{subscriptionId}/providers/Microsoft.Search
            // This is advisory diagnostics only; callers keep the original provider error if this
            // metadata request fails.
            var locationNameByProviderValue = CreateLocationNameLookup(GetAvailableLocations(subscription.Value, cancellationToken));
            var provider = await subscription.Value.GetResourceProviderAsync(providerNamespace, cancellationToken: cancellationToken).ConfigureAwait(false);

            // ARM provider metadata is shaped as:
            //   { "resourceTypes": [ { "resourceType": "searchServices", "locations": [ "East US", "West US 2" ] } ] }
            // The locations are often display names, while Aspire commands accept canonical names
            // like "eastus", so map through the subscription location list before surfacing them.
            if (provider.Value.Data.ResourceTypes is not { Count: > 0 } resourceTypes)
            {
                return [];
            }

            foreach (var resourceTypeMetadata in resourceTypes)
            {
                if (!string.Equals(resourceTypeMetadata.ResourceType, providerResourceType, StringComparisons.AzureResourceType) ||
                    resourceTypeMetadata.Locations is not { Count: > 0 } locations)
                {
                    continue;
                }

                return locations
                    .Where(static location => !string.IsNullOrWhiteSpace(location))
                    .Select(location => TryGetLocationName(locationNameByProviderValue, location) ?? location)
                    .Distinct(StringComparers.AzureLocation)
                    .OrderBy(static location => location, StringComparers.AzureLocation)
                    .ToArray();
            }

            return [];
        }

        public IRoleAssignmentCollection GetRoleAssignments(ResourceIdentifier scope)
        {
            return new DefaultRoleAssignmentCollection(armClient.GetRoleAssignments(scope));
        }

        public async Task<bool> ResourceExistsAsync(string resourceId, CancellationToken cancellationToken = default)
        {
            try
            {
                var resource = armClient.GetGenericResource(new ResourceIdentifier(resourceId));
                await resource.GetAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
        }

        public async Task DeleteResourceAsync(string resourceId, string? resourceLocation = null, string? fallbackResourceLocation = null, CancellationToken cancellationToken = default)
        {
            var resourceIdentifier = new ResourceIdentifier(resourceId);
            var resource = armClient.GetGenericResource(resourceIdentifier);
            if (!IsKeyVaultResource(resourceIdentifier))
            {
                await resource.DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
                return;
            }

            var keyVaultLocations = new List<string>();
            AddLocationIfPresent(keyVaultLocations, resourceLocation);
            AddLocationIfPresent(keyVaultLocations, fallbackResourceLocation);

            var liveVaultExists = true;
            try
            {
                var response = await resource.GetAsync(cancellationToken).ConfigureAwait(false);
                AddLocationIfPresent(keyVaultLocations, response.Value.Data.Location.Name, insertFirst: true);
            }
            catch (RequestFailedException ex) when (ex.Status == 404 && keyVaultLocations.Count > 0)
            {
                // Continue with the caller-provided locations. This handles retrying after the live
                // vault was already deleted, while the soft-delete tombstone still reserves the name.
                liveVaultExists = false;
            }

            if (liveVaultExists)
            {
                try
                {
                    await resource.DeleteAsync(WaitUntil.Started, cancellationToken).ConfigureAwait(false);
                    await WaitForKeyVaultToBeDeletedAsync(resource, timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // A retry can arrive after the live vault was deleted but before the soft-deleted
                    // vault was purged. Keep going so the delete command can still make the deterministic
                    // vault name reusable.
                }
            }

            foreach (var keyVaultLocation in keyVaultLocations)
            {
                if (await TryPurgeDeletedKeyVaultAsync(resourceIdentifier, keyVaultLocation, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }

        public async Task CancelDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
        {
            var deployment = armClient.GetArmDeploymentResource(new ResourceIdentifier(deploymentId));
            await deployment.CancelAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> TryPurgeDeletedKeyVaultAsync(ResourceIdentifier vaultResourceId, string location, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(vaultResourceId.SubscriptionId))
            {
                throw new InvalidOperationException($"Unable to purge deleted Azure Key Vault '{vaultResourceId}' because the subscription ID is missing or invalid.");
            }

            var deletedVaultResourceId = DeletedKeyVaultResource.CreateResourceIdentifier(
                vaultResourceId.SubscriptionId,
                new AzureLocation(location),
                vaultResourceId.Name);
            var deletedVault = armClient.GetDeletedKeyVaultResource(deletedVaultResourceId);
            try
            {
                await deletedVault.PurgeDeletedAsync(WaitUntil.Started, cancellationToken).ConfigureAwait(false);
                await WaitForDeletedKeyVaultToBePurgedAsync(deletedVault, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }

            return true;
        }

        private static async Task WaitForKeyVaultToBeDeletedAsync(GenericResource keyVault, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(s_keyVaultDeleteTimeout, timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (true)
            {
                try
                {
                    await keyVault.GetAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for Azure Key Vault '{keyVault.Id}' to be deleted.");
                }

                try
                {
                    await Task.Delay(s_keyVaultDeletePollInterval, timeProvider, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for Azure Key Vault '{keyVault.Id}' to be deleted.");
                }
            }
        }

        private static async Task WaitForDeletedKeyVaultToBePurgedAsync(DeletedKeyVaultResource deletedVault, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            using var timeoutCts = new CancellationTokenSource(s_keyVaultPurgeTimeout, timeProvider);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (true)
            {
                try
                {
                    await deletedVault.GetAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for deleted Azure Key Vault '{deletedVault.Id}' to be purged.");
                }

                try
                {
                    await Task.Delay(s_keyVaultPurgePollInterval, timeProvider, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for deleted Azure Key Vault '{deletedVault.Id}' to be purged.");
                }
            }
        }

        private static bool IsKeyVaultResource(ResourceIdentifier resourceId)
            => string.Equals(resourceId.ResourceType.ToString(), KeyVaultResourceType, StringComparison.OrdinalIgnoreCase);

        private static void AddLocationIfPresent(List<string> locations, string? location, bool insertFirst = false)
        {
            if (string.IsNullOrWhiteSpace(location) ||
                locations.Contains(location, StringComparers.AzureLocation))
            {
                return;
            }

            if (insertFirst)
            {
                locations.Insert(0, location);
            }
            else
            {
                locations.Add(location);
            }
        }

        public async Task<AzureDeploymentState?> GetDeploymentAsync(string deploymentId, CancellationToken cancellationToken = default)
        {
            var deployment = armClient.GetArmDeploymentResource(new ResourceIdentifier(deploymentId));
            try
            {
                var response = await deployment.GetAsync(cancellationToken).ConfigureAwait(false);
                var data = response.Value.Data;
                return new AzureDeploymentState(
                    data.Properties.ProvisioningState?.ToString() ?? string.Empty,
                    data.Properties.Outputs?.ToObjectFromJson<JsonObject>());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        public async IAsyncEnumerable<string> GetDeploymentTargetResourceIdsAsync(string deploymentId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var operation in GetDeploymentOperationsAsync(deploymentId, recursive: true, cancellationToken).ConfigureAwait(false))
            {
                if (operation.TargetResource?.Id is { Length: > 0 } resourceId)
                {
                    yield return resourceId;
                }
            }
        }

        public async IAsyncEnumerable<AzureDeploymentOperationDetails> GetDeploymentOperationsAsync(
            string deploymentId,
            bool recursive = true,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var pendingDeployments = new Queue<ResourceIdentifier>();
            var visitedDeployments = new HashSet<string>(StringComparers.AzureResourceId);
            pendingDeployments.Enqueue(new ResourceIdentifier(deploymentId));

            // ARM operation lists are per deployment, but Bicep frequently emits nested deployment
            // resources. Walk those child deployments breadth-first so Aspire can surface the
            // provider failure (for example Microsoft.Search/searchServices) instead of stopping at
            // the outer Microsoft.Resources/deployments wrapper.
            while (pendingDeployments.Count > 0)
            {
                var currentDeploymentIds = new List<ResourceIdentifier>();
                while (pendingDeployments.Count > 0)
                {
                    var currentDeploymentId = pendingDeployments.Dequeue();
                    if (visitedDeployments.Add(currentDeploymentId.ToString()))
                    {
                        currentDeploymentIds.Add(currentDeploymentId);
                    }
                }

                // Fetch each breadth-first level concurrently. Deployments discovered from the same
                // parent level have independent ARM operation lists; Task.WhenAll preserves input
                // order so diagnostics stay deterministic while avoiding serial round trips.
                var operationGroups = await Task.WhenAll(
                    currentDeploymentIds.Select(deploymentId => GetDeploymentOperationsForDeploymentAsync(deploymentId, cancellationToken)))
                    .ConfigureAwait(false);

                foreach (var operationGroup in operationGroups)
                {
                    foreach (var operationDetails in operationGroup)
                    {
                        yield return operationDetails;

                        if (recursive &&
                            operationDetails.IsNestedDeploymentCreate &&
                            operationDetails.TargetResource?.Id is { Length: > 0 } nestedDeploymentId &&
                            ResourceIdentifier.TryParse(nestedDeploymentId, out var nestedResourceId) &&
                            nestedResourceId is not null)
                        {
                            pendingDeployments.Enqueue(nestedResourceId);
                        }
                    }
                }
            }
        }

        private async Task<AzureDeploymentOperationDetails[]> GetDeploymentOperationsForDeploymentAsync(ResourceIdentifier deploymentId, CancellationToken cancellationToken)
        {
            var operations = new List<AzureDeploymentOperationDetails>();
            var deployment = armClient.GetArmDeploymentResource(deploymentId);
            await foreach (var operation in deployment.GetDeploymentOperationsAsync(top: null, cancellationToken).ConfigureAwait(false))
            {
                operations.Add(CreateDeploymentOperationDetails(operation, deploymentId.ToString()));
            }

            return [.. operations];
        }

        private static AzureDeploymentOperationDetails CreateDeploymentOperationDetails(ArmDeploymentOperation operation, string deploymentId)
        {
            var properties = operation.Properties;
            var targetResource = properties.TargetResource is { } target
                ? new AzureDeploymentOperationTarget(target.Id, target.ResourceType?.ToString(), target.ResourceName)
                : null;

            // Deployment operations carry provider failures in properties.statusMessage.error and
            // the target resource beside it. Capture both together so command JSON can include the
            // failing resource ID/name even when the error payload itself only has code/message.
            var failureDetails = AzureProvisioningFailureDetails.FromResponseError(
                properties.StatusMessage?.Error,
                targetResource,
                properties.ProvisioningOperation?.ToString(),
                properties.StatusCode,
                properties.ServiceRequestId,
                properties.StatusMessage is null
                    ? null
                    : ModelReaderWriter.Write(properties.StatusMessage, ModelReaderWriterOptions.Json).ToString());

            return new(
                OperationId: operation.OperationId,
                DeploymentId: deploymentId,
                ProvisioningOperation: properties.ProvisioningOperation?.ToString(),
                ProvisioningState: properties.ProvisioningState,
                Timestamp: properties.Timestamp,
                Duration: properties.Duration,
                StatusCode: properties.StatusCode,
                ServiceRequestId: properties.ServiceRequestId,
                TargetResource: targetResource,
                FailureDetails: failureDetails);
        }

        private static IEnumerable<(string Name, string DisplayName)> GetAvailableLocations(SubscriptionResource subscription, CancellationToken cancellationToken)
        {
            var locations = new List<(string Name, string DisplayName)>();

            foreach (var location in subscription.GetLocations(cancellationToken: cancellationToken))
            {
                locations.Add((location.Name, location.DisplayName ?? location.Name));
            }

            return locations;
        }

        private static Dictionary<string, string> CreateLocationNameLookup(IEnumerable<(string Name, string DisplayName)> availableLocations)
        {
            var locationNameByProviderValue = new Dictionary<string, string>(StringComparers.AzureLocation);
            foreach (var (name, displayName) in availableLocations)
            {
                AddLocation(name, name);
                AddLocation(displayName, name);
                AddLocation(NormalizeLocation(displayName), name);
            }

            return locationNameByProviderValue;

            void AddLocation(string? providerValue, string locationName)
            {
                if (!string.IsNullOrWhiteSpace(providerValue))
                {
                    locationNameByProviderValue.TryAdd(providerValue, locationName);
                }
            }
        }

        private static bool TrySplitResourceType(string resourceType, out string providerNamespace, out string providerResourceType)
        {
            var separator = resourceType.IndexOf('/');
            if (separator <= 0 || separator == resourceType.Length - 1)
            {
                providerNamespace = string.Empty;
                providerResourceType = string.Empty;
                return false;
            }

            providerNamespace = resourceType[..separator];
            providerResourceType = resourceType[(separator + 1)..];
            return true;
        }

        private static string? TryGetLocationName(Dictionary<string, string> locationNameByProviderValue, string providerValue)
        {
            if (locationNameByProviderValue.TryGetValue(providerValue, out var locationName))
            {
                return locationName;
            }

            return locationNameByProviderValue.TryGetValue(NormalizeLocation(providerValue), out locationName)
                ? locationName
                : null;
        }

        private static string NormalizeLocation(string location)
            => string.Concat(location.Where(static c => !char.IsWhiteSpace(c))).ToLowerInvariant();

        private sealed class DefaultTenantResource(TenantResource tenantResource) : ITenantResource
        {
            public Guid? TenantId => tenantResource.Data.TenantId;
            public string? DisplayName => tenantResource.Data.DisplayName;
            public string? DefaultDomain => tenantResource.Data.DefaultDomain;

            public IArmDeploymentCollection GetArmDeployments()
            {
                return new DefaultArmDeploymentCollection(tenantResource.GetArmDeployments());
            }
        }
    }
}
