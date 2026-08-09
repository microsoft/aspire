// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;

namespace Aspire.Hosting;

internal class YarpConfigurationBuilder(IResourceBuilder<YarpResource> parent) : IYarpConfigurationBuilder
{
    private readonly IResourceBuilder<YarpResource> _parent = parent;

    /// <inheritdoc/>
    public YarpRoute AddRoute(string path, YarpCluster cluster)
    {
        var routeId = $"route{_parent.Resource.Routes.Count}";
        var route = new YarpRoute(cluster, routeId);
        if (path != null)
        {
            route.WithMatchPath(path);
        }
        _parent.Resource.Routes.Add(route);
        return route;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(EndpointReference endpoint)
    {
        var destination = new YarpCluster(endpoint);
        _parent.Resource.Clusters.Add(destination);
        _parent.WithReference(endpoint);
        return destination;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        var destination = new YarpCluster(resource.Resource);
        _parent.Resource.Clusters.Add(destination);
        _parent.WithReference(resource);
        return destination;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(IResourceBuilder<ExternalServiceResource> externalService)
    {
        var destination = new YarpCluster(externalService.Resource);
        _parent.Resource.Clusters.Add(destination);
        _parent.WithReference(externalService);
        return destination;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(string clusterName, object[] destinations)
    {
        ArgumentNullException.ThrowIfNull(clusterName);
        ArgumentNullException.ThrowIfNull(destinations);

        if (destinations.Length == 0)
        {
            throw new ArgumentException("At least one destination must be provided.", nameof(destinations));
        }

        // Validate that each destination is a supported type
        foreach (var dest in destinations)
        {
            if (dest is not (IValueProvider or string or Uri))
            {
                throw new ArgumentException(
                    $"Destination must be an IValueProvider, string, or Uri. Got: {dest?.GetType().FullName ?? "null"}",
                    nameof(destinations));
            }
        }

        var destination = new YarpCluster(clusterName, destinations);
        _parent.Resource.Clusters.Add(destination);
        return destination;
    }

    internal YarpRoute AddRoute(string path, object destination)
    {
        var cluster = AddCluster(YarpConfigurationBuilderHelpers.CreateSyntheticClusterName(path, destination.ToString()!), [destination]);
        return AddRoute(path, cluster);
    }
}
