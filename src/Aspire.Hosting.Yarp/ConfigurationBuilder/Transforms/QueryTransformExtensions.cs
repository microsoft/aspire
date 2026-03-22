// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Yarp.Transforms;
using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for adding query transforms.
/// </summary>
public static class QueryTransformExtensions
{
    /// <summary>
    /// Adds the transform that will append or set the query parameter from the given value.
    /// </summary>
    [AspireExport("withTransformQueryValue", Description = "Adds the transform that will append or set the query parameter from the given value.")]
    public static YarpRoute WithTransformQueryValue(this YarpRoute route, string queryKey, string value, bool append = true)
    {
        route.Configure(r => r.WithTransformQueryValue(queryKey, value, append));
        return route;
    }

    /// <summary>
    /// Adds the transform that will append or set the query parameter from a route value.
    /// </summary>
    [AspireExport("withTransformQueryRouteValue", Description = "Adds the transform that will append or set the query parameter from a route value.")]
    public static YarpRoute WithTransformQueryRouteValue(this YarpRoute route, string queryKey, string routeValueKey, bool append = true)
    {
        route.Configure(r => r.WithTransformQueryRouteValue(queryKey, routeValueKey, append));
        return route;
    }

    /// <summary>
    /// Adds the transform that will remove the given query key.
    /// </summary>
    [AspireExport("withTransformQueryRemoveKey", Description = "Adds the transform that will remove the given query key.")]
    public static YarpRoute WithTransformQueryRemoveKey(this YarpRoute route, string queryKey)
    {
        route.Configure(r => r.WithTransformQueryRemoveKey(queryKey));
        return route;
    }
}
