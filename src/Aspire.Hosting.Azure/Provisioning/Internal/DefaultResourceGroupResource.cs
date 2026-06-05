// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure;
using Azure.Core;
using Azure.ResourceManager.Resources;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Default implementation of <see cref="IResourceGroupResource"/>.
/// </summary>
internal sealed class DefaultResourceGroupResource(ResourceGroupResource resourceGroupResource) : IResourceGroupResource
{
    public ResourceIdentifier Id => resourceGroupResource.Id;
    public string Name => resourceGroupResource.Data.Name;

    public IArmDeploymentCollection GetArmDeployments()
    {
        return new DefaultArmDeploymentCollection(resourceGroupResource.GetArmDeployments());
    }

    public async Task DeleteAsync(WaitUntil waitUntil, CancellationToken cancellationToken = default)
    {
        await resourceGroupResource.DeleteAsync(waitUntil, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<(string Name, string ResourceType)> GetResourcesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var resource in resourceGroupResource.GetGenericResourcesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return (resource.Data.Name, resource.Data.ResourceType.ToString());
        }
    }
}
