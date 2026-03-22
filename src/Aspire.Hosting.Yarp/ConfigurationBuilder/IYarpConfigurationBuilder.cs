// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;

namespace Aspire.Hosting;

/// <summary>
/// Interface to build a configuration file for YARP
/// </summary>
[AspireExport(ExposeMethods = true)]
public interface IYarpConfigurationBuilder
{
    /// <summary>
    /// Add a new route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="path">The path to match for this route.</param>
    /// <param name="cluster">The target cluster for this route.</param>
    /// <returns></returns>
    public YarpRoute AddRoute(string path, YarpCluster cluster);

    /// <summary>
    /// Add a new cluster to YARP.
    /// </summary>
    /// <param name="endpoint">The endpoint target for this cluster.</param>
    /// <returns></returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <c>addClusterFromEndpoint</c> helper instead.</remarks>
    [AspireExportIgnore(Reason = "Use the addClusterFromEndpoint method instead.")]
    public YarpCluster AddCluster(EndpointReference endpoint);

    /// <summary>
    /// Add a new cluster to YARP based on a resource that supports service discovery.
    /// </summary>
    /// <param name="resource">The resource target for this cluster.</param>
    /// <returns></returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <c>addClusterFromResource</c> helper instead.</remarks>
    [AspireExportIgnore(Reason = "Use the addClusterFromResource method instead.")]
    public YarpCluster AddCluster(IResourceBuilder<IResourceWithServiceDiscovery> resource);

    /// <summary>
    /// Add a new cluster to YARP based on an external service resource.
    /// </summary>
    /// <param name="externalService">The external service used by this cluster.</param>
    /// <returns></returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <c>addClusterFromExternalService</c> helper instead.</remarks>
    [AspireExportIgnore(Reason = "Use the addClusterFromExternalService method instead.")]
    public YarpCluster AddCluster(IResourceBuilder<ExternalServiceResource> externalService);

    /// <summary>
    /// Add a new cluster to YARP based on a collection of urls.
    /// </summary>
    /// <param name="clusterName">The name of the cluster.</param>
    /// <param name="destinations">The destinations used by this cluster.</param>
    /// <returns></returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <c>addClusterWithDestinations</c> helper instead.</remarks>
    [AspireExportIgnore(Reason = "Use the addClusterWithDestinations method instead.")]
    public YarpCluster AddCluster(string clusterName, object[] destinations);

    /// <summary>
    /// Add a new cluster to YARP based on a collection of urls.
    /// </summary>
    /// <param name="clusterName">The name of the cluster.</param>
    /// <param name="destination">The destinations used by this cluster.</param>
    /// <returns></returns>
    /// <remarks>This overload is not available in polyglot app hosts. Use the <c>addClusterWithDestination</c> helper instead.</remarks>
    [AspireExportIgnore(Reason = "Use the addClusterWithDestination method instead.")]
    public YarpCluster AddCluster(string clusterName, object destination)
    {
        return AddCluster(clusterName, [destination]);
    }
}

/// <summary>
/// Collection of extensions methods for <see cref="IYarpConfigurationBuilder"/>
/// </summary>
public static class YarpConfigurationBuilderExtensions
{
    private const string CatchAllPath = "/{**catchall}";

    /// <summary>
    /// Adds a cluster for an endpoint reference.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="endpoint">The endpoint target for this cluster.</param>
    /// <returns>The created cluster.</returns>
    [AspireExport("addClusterFromEndpoint", Description = "Adds a YARP cluster for an endpoint reference.")]
    internal static YarpCluster AddClusterFromEndpoint(this IYarpConfigurationBuilder builder, EndpointReference endpoint)
    {
        return builder.AddCluster(endpoint);
    }

    /// <summary>
    /// Adds a cluster for a resource that supports service discovery.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="resource">The resource target for this cluster.</param>
    /// <returns>The created cluster.</returns>
    [AspireExport("addClusterFromResource", Description = "Adds a YARP cluster for a resource that supports service discovery.")]
    internal static YarpCluster AddClusterFromResource(this IYarpConfigurationBuilder builder, IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        return builder.AddCluster(resource);
    }

    /// <summary>
    /// Adds a cluster for an external service resource.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="externalService">The external service used by this cluster.</param>
    /// <returns>The created cluster.</returns>
    [AspireExport("addClusterFromExternalService", Description = "Adds a YARP cluster for an external service resource.")]
    internal static YarpCluster AddClusterFromExternalService(this IYarpConfigurationBuilder builder, IResourceBuilder<ExternalServiceResource> externalService)
    {
        return builder.AddCluster(externalService);
    }

    /// <summary>
    /// Adds a cluster from multiple destinations.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="clusterName">The name of the cluster.</param>
    /// <param name="destinations">The destinations used by this cluster.</param>
    /// <returns>The created cluster.</returns>
    [AspireExport("addClusterWithDestinations", Description = "Adds a YARP cluster with multiple destinations.")]
    internal static YarpCluster AddClusterWithDestinations(this IYarpConfigurationBuilder builder, string clusterName, object[] destinations)
    {
        return builder.AddCluster(clusterName, destinations);
    }

    /// <summary>
    /// Adds a cluster from a single destination.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="clusterName">The name of the cluster.</param>
    /// <param name="destination">The destination used by this cluster.</param>
    /// <returns>The created cluster.</returns>
    [AspireExport("addClusterWithDestination", Description = "Adds a YARP cluster with a single destination.")]
    internal static YarpCluster AddClusterWithDestination(this IYarpConfigurationBuilder builder, string clusterName, object destination)
    {
        return builder.AddCluster(clusterName, destination);
    }

    /// <summary>
    /// Add a new catch all route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="cluster">The target cluster for this route.</param>
    /// <returns></returns>
    [AspireExport("addCatchAllRoute", Description = "Adds a YARP catch-all route for an existing cluster.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, YarpCluster cluster)
    {
        return builder.AddRoute(CatchAllPath, cluster);
    }

    /// <summary>
    /// Add a new catch all route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="endpoint">The target endpoint for this route.</param>
    /// <returns></returns>
    [AspireExport("addCatchAllRouteFromEndpoint", Description = "Adds a YARP catch-all route for an endpoint reference.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, EndpointReference endpoint)
    {
        return builder.AddRoute(CatchAllPath, endpoint);
    }

    /// <summary>
    /// Add a new catch all route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="resource">The target resource for this route.</param>
    /// <returns></returns>
    [AspireExport("addCatchAllRouteFromResource", Description = "Adds a YARP catch-all route for a resource that supports service discovery.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        return builder.AddRoute(CatchAllPath, resource);
    }

    /// <summary>
    /// Add a new route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="path">The path to match for this route.</param>
    /// <param name="endpoint">The target endpoint for this route.</param>
    /// <returns></returns>
    [AspireExport("addRouteFromEndpoint", Description = "Adds a YARP route for an endpoint reference.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, string path, EndpointReference endpoint)
    {
        var cluster = builder.AddCluster(endpoint);
        return builder.AddRoute(path, cluster);
    }

    /// <summary>
    /// Add a new route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="path">The path to match for this route.</param>
    /// <param name="resource">The target endpoint for this route.</param>
    /// <returns></returns>
    [AspireExport("addRouteFromResource", Description = "Adds a YARP route for a resource that supports service discovery.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, string path, IResourceBuilder<IResourceWithServiceDiscovery> resource)
    {
        var cluster = builder.AddCluster(resource);
        return builder.AddRoute(path, cluster);
    }

    /// <summary>
    /// Add a new route to YARP that will target the external service in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="path">The path to match for this route.</param>
    /// <param name="externalService">The target external service for this route.</param>
    /// <returns></returns>
    [AspireExport("addRouteFromExternalService", Description = "Adds a YARP route for an external service resource.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, string path, IResourceBuilder<ExternalServiceResource> externalService)
    {
        var cluster = builder.AddCluster(externalService);
        return builder.AddRoute(path, cluster);
    }

    /// <summary>
    /// Add a new catch all route to YARP that will target the cluster in parameter.
    /// </summary>
    /// <param name="builder">The builder instance.</param>
    /// <param name="externalService">The target external service for this route.</param>
    /// <returns></returns>
    [AspireExport("addCatchAllRouteFromExternalService", Description = "Adds a YARP catch-all route for an external service resource.")]
    public static YarpRoute AddRoute(this IYarpConfigurationBuilder builder, IResourceBuilder<ExternalServiceResource> externalService)
    {
        return builder.AddRoute(CatchAllPath, externalService);
    }
}
