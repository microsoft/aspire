// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;
using Aspire.Hosting.Yarp;

namespace Aspire.Hosting;

internal class YarpProjectConfigurationBuilder(IResourceBuilder<DotnetProjectResource> parent) : IYarpConfigurationBuilder
{
    private readonly IResourceBuilder<DotnetProjectResource> _parent = parent;
    internal List<YarpRoute> Routes { get; } = [];

    internal List<YarpCluster> Clusters { get; } = [];

    /// <inheritdoc/>
    public YarpRoute AddRoute(string path, YarpCluster cluster)
    {
        var routeId = $"route{Routes.Count}";
        var route = new YarpRoute(cluster, routeId);
        if (path != null)
        {
            route.WithMatchPath(path);
        }
        Routes.Add(route);
        return route;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(EndpointReference endpoint)
    {
        var destination = new YarpCluster(endpoint);
        Clusters.Add(destination);
        _parent.WithReference(endpoint);
        return destination;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        var destination = new YarpCluster(resource.Resource);
       Clusters.Add(destination);
        _parent.WithReference(resource);
        return destination;
    }

    /// <inheritdoc/>
    public YarpCluster AddCluster(IResourceBuilder<ExternalServiceResource> externalService)
    {
        var destination = new YarpCluster(externalService.Resource);
        Clusters.Add(destination);
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
        Clusters.Add(destination);
        return destination;
    }

    internal YarpRoute AddRoute(string path, object destination)
    {
        var cluster = AddCluster(YarpConfigurationBuilderHelpers.CreateSyntheticClusterName(path, destination.ToString()!), [destination]);
        return AddRoute(path, cluster);
    }
}
