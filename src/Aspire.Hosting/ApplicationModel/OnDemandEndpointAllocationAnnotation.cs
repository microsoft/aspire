// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Stores a resource-owned endpoint allocator that can run before normal allocation completes.
/// </summary>
internal sealed class OnDemandEndpointAllocationAnnotation(Func<EndpointAnnotation, NetworkIdentifier, AllocatedEndpoint?> allocator) : IResourceAnnotation
{
    private Func<EndpointAnnotation, NetworkIdentifier, AllocatedEndpoint?>? _allocator = allocator;

    public AllocatedEndpoint? TryAllocate(EndpointAnnotation endpoint, NetworkIdentifier networkId)
    {
        var allocator = _allocator;

        return allocator?.Invoke(endpoint, networkId);
    }

    public void StopAllocating()
    {
        Interlocked.Exchange(ref _allocator, null);
    }
}
