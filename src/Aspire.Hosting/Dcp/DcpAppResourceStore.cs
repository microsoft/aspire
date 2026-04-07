// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Hosting.Dcp;

/// <summary>
/// Shared collection of DCP application resources, used by all components that need to read or write resource state.
/// </summary>
internal sealed class DcpAppResourceStore
{
    private readonly ConcurrentBag<IAppResource> _resources = [];

    public void Add(IAppResource resource) => _resources.Add(resource);

    public void AddRange(IEnumerable<IAppResource> resources)
    {
        foreach (var item in resources)
        {
            _resources.Add(item);
        }
    }

    public IEnumerable<IAppResource> Get() => _resources;
}
