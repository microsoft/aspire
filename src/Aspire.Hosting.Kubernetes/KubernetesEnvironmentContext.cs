// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Kubernetes;

internal sealed class KubernetesEnvironmentContext(KubernetesEnvironmentResource environment, ILogger logger)
{
    private readonly Dictionary<IResource, KubernetesResource> _kubernetesComponents = new(new ResourceNameComparer());

    public ILogger Logger => logger;

    public async Task<KubernetesResource> CreateKubernetesResourceAsync(IResource resource, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (_kubernetesComponents.TryGetValue(resource, out var existingResource))
        {
            return existingResource;
        }

        logger.LogInformation("Creating Kubernetes resource for {ResourceName}", resource.Name);

        var serviceResource = new KubernetesResource(resource.Name, resource, environment);
        _kubernetesComponents[resource] = serviceResource;

        await serviceResource.ProcessResourceAsync(this, executionContext, cancellationToken).ConfigureAwait(false);

        return serviceResource;
    }
}
