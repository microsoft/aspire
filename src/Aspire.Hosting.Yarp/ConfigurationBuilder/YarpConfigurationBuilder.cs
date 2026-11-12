// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;

namespace Aspire.Hosting;

internal class YarpConfigurationBuilder(IResourceBuilder<YarpResource> parent) : YarpProjectConfigurationBuilder(parent)
{
    private readonly IResourceBuilder<YarpResource> _parent = parent;

    /// <inheritdoc/>
    public new YarpRoute AddRoute(string path, YarpCluster cluster)
    {
        var route = base.AddRoute(path, cluster);
        _parent.Resource.Routes.Add(route);
        return route;
    }

    /// <inheritdoc/>
    public new YarpCluster AddCluster(EndpointReference endpoint)
    {
        var destination = base.AddCluster(endpoint);
        _parent.Resource.Clusters.Add(destination);
        return destination;
    }

    /// <inheritdoc/>
    public new YarpCluster AddCluster(IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        var destination = base.AddCluster(resource);
        _parent.Resource.Clusters.Add(destination);
        return destination;
    }

    /// <inheritdoc/>
    public new YarpCluster AddCluster(IResourceBuilder<ExternalServiceResource> externalService)
    {
        var destination = base.AddCluster(externalService);
        _parent.Resource.Clusters.Add(destination);
        return destination;
    }

    /// <inheritdoc/>
    public new YarpCluster AddCluster(string clusterName, object[] destinations)
    {
        var destination = base.AddCluster(clusterName, destinations);
        _parent.Resource.Clusters.Add(destination);
        return destination;
    }
}
