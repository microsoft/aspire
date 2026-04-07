// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Yarp.ReverseProxy.Configuration;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents a route for YARP
/// </summary>
[AspireExport]
public class YarpRoute
{
    // Testing only
    internal YarpRoute(RouteConfig routeConfig)
    {
        RouteConfig = routeConfig;
    }

    internal YarpRoute(YarpCluster cluster, string routeId)
    {
        RouteConfig = new RouteConfig
        {
            RouteId = routeId,
            ClusterId = cluster.ClusterConfig.ClusterId,
            Match = new RouteMatch(),
        };
    }

    internal RouteConfig RouteConfig { get; private set; }

    internal void Configure(Func<RouteConfig, RouteConfig> configure)
    {
        RouteConfig = configure(RouteConfig);
    }
}

/// <summary>
/// Provides extension methods for configuring a YARP destination
/// </summary>
public static class YarpRouteExtensions
{
    /// <summary>
    /// Set the parameters used to match requests.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload or the specific match helper methods instead.</remarks>
    [AspireExportIgnore(Reason = "RouteMatch is not ATS-compatible. Use the DTO-based overload or the specific match helper methods instead.")]
    public static YarpRoute WithMatch(this YarpRoute route, RouteMatch match)
    {
        route.Configure(r => r with { Match = match });
        return route;
    }

    /// <summary>
    /// Set the parameters used to match requests.
    /// </summary>
    [AspireExport(Description = "Sets the route match criteria.")]
    internal static YarpRoute WithMatch(this YarpRoute route, YarpRouteMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        route.Configure(r => r with { Match = ToRouteMatch(match) });
        return route;
    }

    #region RouteMatch helpers

    private static YarpRoute ConfigureMatch(this YarpRoute route, Func<RouteMatch, RouteMatch> match)
    {
        route.Configure(r => r with { Match = match(r.Match) });
        return route;
    }

    /// <summary>
    /// Only match requests with the given Path pattern.
    /// </summary>
    [AspireExport(Description = "Matches requests with the specified path pattern.")]
    public static YarpRoute WithMatchPath(this YarpRoute route, string path)
    {
        route.ConfigureMatch(match => match with { Path = path });
        return route;
    }

    /// <summary>
    /// Only match requests that use these optional HTTP methods. E.g. GET, POST.
    /// </summary>
    [AspireExport(Description = "Matches requests that use the specified HTTP methods.")]
    public static YarpRoute WithMatchMethods(this YarpRoute route, params string[] methods)
    {
        route.ConfigureMatch(match => match with { Methods = methods });
        return route;
    }

    /// <summary>
    /// Only match requests that contain all of these headers.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "RouteHeader is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpRoute WithMatchHeaders(this YarpRoute route, params RouteHeader[] headers)
    {
        route.ConfigureMatch(match => match with { Headers = headers.ToList() });
        return route;
    }

    /// <summary>
    /// Only match requests that contain all of these headers.
    /// </summary>
    [AspireExport(Description = "Matches requests that contain the specified headers.")]
    internal static YarpRoute WithMatchHeaders(this YarpRoute route, params YarpRouteHeaderMatch[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        route.ConfigureMatch(match => match with { Headers = headers.Select(ToRouteHeader).ToList() });
        return route;
    }

    /// <summary>
    ///  Only match requests with the given Host header. Supports wildcards and ports.
    ///  For unicode host names, do not use punycode.
    /// </summary>
    [AspireExport(Description = "Matches requests that contain the specified host headers.")]
    public static YarpRoute WithMatchHosts(this YarpRoute route, params string[] hosts)
    {
        route.ConfigureMatch(match => match with { Hosts = hosts.ToList() });
        return route;
    }

    /// <summary>
    ///  Only match requests that contain all of these query parameters.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "RouteQueryParameter is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpRoute WithMatchRouteQueryParameter(this YarpRoute route, params RouteQueryParameter[] queryParameters)
    {
        route.ConfigureMatch(match => match with { QueryParameters = queryParameters.ToList() });
        return route;
    }

    /// <summary>
    ///  Only match requests that contain all of these query parameters.
    /// </summary>
    [AspireExport(Description = "Matches requests that contain the specified query parameters.")]
    internal static YarpRoute WithMatchRouteQueryParameter(this YarpRoute route, params YarpRouteQueryParameterMatch[] queryParameters)
    {
        ArgumentNullException.ThrowIfNull(queryParameters);

        route.ConfigureMatch(match => match with { QueryParameters = queryParameters.Select(ToRouteQueryParameter).ToList() });
        return route;
    }

    #endregion

    /// <summary>
    /// Set the order for the destination
    /// </summary>
    [AspireExport(Description = "Sets the route order.")]
    public static YarpRoute WithOrder(this YarpRoute route, int? order)
    {
        route.Configure(r => r with { Order = order });
        return route;
    }

    /// <summary>
    /// Set the MaxRequestBodySize for the destination
    /// </summary>
    [AspireExport(Description = "Sets the maximum request body size for the route.")]
    public static YarpRoute WithMaxRequestBodySize(this YarpRoute route, long maxRequestBodySize)
    {
        route.Configure(r => r with { MaxRequestBodySize = maxRequestBodySize });
        return route;
    }

    /// <summary>
    /// Set the Metadata of the destination
    /// </summary>
    [AspireExport("withRouteMetadata", MethodName = "withMetadata", Description = "Sets metadata for the route.")]
    public static YarpRoute WithMetadata(this YarpRoute route, IReadOnlyDictionary<string, string>? metadata)
    {
        route.Configure(r => r with { Metadata = metadata });
        return route;
    }

    /// <summary>
    /// Set the Transforms of the destination
    /// </summary>
    [AspireExport(Description = "Sets the transforms for the route.")]
    public static YarpRoute WithTransforms(this YarpRoute route, IReadOnlyList<IReadOnlyDictionary<string, string>>? transforms)
    {
        route.Configure(r => r with { Transforms = transforms });
        return route;
    }

    /// <summary>
    /// Add a new transform to the destination
    /// </summary>
    /// <remarks>This method is not available in polyglot app hosts. Use <see cref="WithTransforms"/> or the transform-specific helpers instead.</remarks>
    [AspireExportIgnore(Reason = "Action<IDictionary<string, string>> callbacks are not ATS-compatible.")]
    public static YarpRoute WithTransform(this YarpRoute route, Action<IDictionary<string, string>> createTransform)
    {
        ArgumentNullException.ThrowIfNull(createTransform);

        route.Configure(r =>
        {
            var transform = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            createTransform(transform);
            return AppendTransform(r, transform);
        });

        return route;
    }

    /// <summary>
    /// Add a new transform to the destination.
    /// </summary>
    [AspireExport(Description = "Adds a transform to the route.")]
    internal static YarpRoute WithTransform(this YarpRoute route, IReadOnlyDictionary<string, string> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        route.Configure(r =>
        {
            // Polyglot callbacks receive serialized values rather than a live mutable dictionary,
            // so ATS uses this value-based overload instead of the callback-based .NET API.
            var copiedTransform = new Dictionary<string, string>(transform, StringComparer.OrdinalIgnoreCase);
            return AppendTransform(r, copiedTransform);
        });

        return route;
    }

    private static RouteHeader ToRouteHeader(YarpRouteHeaderMatch header)
    {
        ArgumentNullException.ThrowIfNull(header);

        return new RouteHeader
        {
            Name = header.Name,
            Values = header.Values,
            IsCaseSensitive = header.IsCaseSensitive,
            Mode = header.Mode,
        };
    }

    private static RouteMatch ToRouteMatch(YarpRouteMatch match)
    {
        ArgumentNullException.ThrowIfNull(match);

        return new RouteMatch
        {
            Path = match.Path,
            Methods = match.Methods,
            Hosts = match.Hosts,
            Headers = match.Headers?.Select(ToRouteHeader).ToList(),
            QueryParameters = match.QueryParameters?.Select(ToRouteQueryParameter).ToList(),
        };
    }

    private static RouteQueryParameter ToRouteQueryParameter(YarpRouteQueryParameterMatch queryParameter)
    {
        ArgumentNullException.ThrowIfNull(queryParameter);

        return new RouteQueryParameter
        {
            Name = queryParameter.Name,
            Values = queryParameter.Values,
            IsCaseSensitive = queryParameter.IsCaseSensitive,
            Mode = queryParameter.Mode,
        };
    }

    private static RouteConfig AppendTransform(RouteConfig routeConfig, IReadOnlyDictionary<string, string> transform)
    {
        List<IReadOnlyDictionary<string, string>> transforms;
        if (routeConfig.Transforms is null)
        {
            transforms = new List<IReadOnlyDictionary<string, string>>();
        }
        else
        {
            transforms = new List<IReadOnlyDictionary<string, string>>(routeConfig.Transforms.Count + 1);
            transforms.AddRange(routeConfig.Transforms);
        }

        transforms.Add(transform);

        return routeConfig with { Transforms = transforms };
    }
}
