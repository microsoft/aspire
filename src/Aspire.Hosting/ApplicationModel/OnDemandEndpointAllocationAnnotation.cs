// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores resource-owned endpoint allocation callbacks that can run before normal allocation completes.
/// </summary>
internal sealed class OnDemandEndpointAllocationAnnotation : IResourceAnnotation
{
    private readonly Dictionary<EndpointAnnotation, OnDemandEndpointAllocation> _allocations = [];

    public void Add(EndpointAnnotation endpoint, Func<NetworkIdentifier, AllocatedEndpoint?> provider)
    {
        _allocations[endpoint] = new(provider);
    }

    public AllocatedEndpoint? TryAllocate(EndpointAnnotation endpoint, NetworkIdentifier networkId)
    {
        return _allocations.TryGetValue(endpoint, out var allocation)
            ? allocation.TryAllocate(networkId)
            : null;
    }

    public void Clear(EndpointAnnotation endpoint)
    {
        if (_allocations.TryGetValue(endpoint, out var allocation))
        {
            allocation.Clear();
        }
    }

    private sealed class OnDemandEndpointAllocation(Func<NetworkIdentifier, AllocatedEndpoint?> provider)
    {
        private Func<NetworkIdentifier, AllocatedEndpoint?>? _provider = provider;

        public AllocatedEndpoint? TryAllocate(NetworkIdentifier networkId)
        {
            var provider = _provider;

            return provider?.Invoke(networkId);
        }

        public void Clear()
        {
            Interlocked.Exchange(ref _provider, null);
        }
    }
}
