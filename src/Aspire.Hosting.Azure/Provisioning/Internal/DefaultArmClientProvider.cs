// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IArmClientProvider"/>.
/// </summary>
internal sealed class DefaultArmClientProvider : IArmClientProvider
{
    public IArmClient GetArmClient(TokenCredential credential, string subscriptionId)
    {
        var armClient = new ArmClient(credential, subscriptionId);
        return new DefaultArmClient(armClient);
    }

    public IArmClient GetArmClient(TokenCredential credential)
    {
        var armClient = new ArmClient(credential);
        return new DefaultArmClient(armClient);
    }

    private sealed class DefaultArmClient(ArmClient armClient) : IArmClient
    {
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
                if (subscription.Data.TenantId?.ToString().Equals(tenantId, StringComparison.OrdinalIgnoreCase) == true)
                {
                    subscriptions.Add(new DefaultSubscriptionResource(subscription));
                }
            }

            return subscriptions;
        }

        public async Task<IEnumerable<(string Name, string DisplayName)>> GetAvailableLocationsAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            var locations = new List<(string Name, string DisplayName)>();

            foreach (var location in subscription.Value.GetLocations(cancellationToken: cancellationToken))
            {
                locations.Add((location.Name, location.DisplayName ?? location.Name));
            }

            return locations.OrderBy(l => l.DisplayName);
        }

        public async Task<IEnumerable<(string Name, string Location)>> GetAvailableResourceGroupsWithLocationAsync(string subscriptionId, CancellationToken cancellationToken = default)
        {
            var subscription = await armClient.GetSubscriptions().GetAsync(subscriptionId, cancellationToken).ConfigureAwait(false);
            var resourceGroups = new List<(string Name, string Location)>();

            await foreach (var resourceGroup in subscription.Value.GetResourceGroups().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                resourceGroups.Add((resourceGroup.Data.Name, resourceGroup.Data.Location.Name));
            }

            return resourceGroups.OrderBy(rg => rg.Name);
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

        public async Task DeleteResourceAsync(string resourceId, CancellationToken cancellationToken = default)
        {
            var resource = armClient.GetGenericResource(new ResourceIdentifier(resourceId));
            await resource.DeleteAsync(WaitUntil.Completed, cancellationToken).ConfigureAwait(false);
        }

        private sealed class DefaultTenantResource(TenantResource tenantResource) : ITenantResource
        {
            public Guid? TenantId => tenantResource.Data.TenantId;
            public string? DisplayName => tenantResource.Data.DisplayName;
            public string? DefaultDomain => tenantResource.Data.DefaultDomain;
        }
    }
}
