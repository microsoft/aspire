// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for adding request header transforms.
/// </summary>
public static class RequestHeadersTransformExtensions
{
    /// <summary>
    /// Adds the transform which will enable or suppress copying request headers to the proxy request.
    /// </summary>
    [AspireExport("withTransformCopyRequestHeaders", Description = "Adds the transform which will enable or suppress copying request headers to the proxy request.")]
    public static YarpRoute WithTransformCopyRequestHeaders(this YarpRoute route, bool copy = true)
    {
        route.Configure(r => r.WithTransformCopyRequestHeaders(copy));
        return route;
    }

    /// <summary>
    /// Adds the transform which will copy the incoming request Host header to the proxy request.
    /// </summary>
    [AspireExport("withTransformUseOriginalHostHeader", Description = "Adds the transform which will copy the incoming request Host header to the proxy request.")]
    public static YarpRoute WithTransformUseOriginalHostHeader(this YarpRoute route, bool useOriginal = true)
    {
        route.Configure(r => r.WithTransformUseOriginalHostHeader(useOriginal));
        return route;
    }

    /// <summary>
    /// Adds the transform which will append or set the request header.
    /// </summary>
    [AspireExport("withTransformRequestHeader", Description = "Adds the transform which will append or set the request header.")]
    public static YarpRoute WithTransformRequestHeader(this YarpRoute route, string headerName, string value, bool append = true)
    {
        route.Configure(r => r.WithTransformRequestHeader(headerName, value, append));
        return route;
    }

    /// <summary>
    /// Adds the transform which will append or set the request header from a route value.
    /// </summary>
    [AspireExport("withTransformRequestHeaderRouteValue", Description = "Adds the transform which will append or set the request header from a route value.")]
    public static YarpRoute WithTransformRequestHeaderRouteValue(this YarpRoute route, string headerName, string routeValueKey, bool append = true)
    {
        route.Configure(r => r.WithTransformRequestHeaderRouteValue(headerName, routeValueKey, append));
        return route;
    }

    /// <summary>
    /// Adds the transform which will remove the request header.
    /// </summary>
    [AspireExport("withTransformRequestHeaderRemove", Description = "Adds the transform which will remove the request header.")]
    public static YarpRoute WithTransformRequestHeaderRemove(this YarpRoute route, string headerName)
    {
        route.Configure(r => r.WithTransformRequestHeaderRemove(headerName));
        return route;
    }

    /// <summary>
    /// Adds the transform which will only copy the allowed request headers. Other transforms
    /// that modify or append to existing headers may be affected if not included in the allow list.
    /// </summary>
    [AspireExport("withTransformRequestHeadersAllowed", Description = "Adds the transform which will only copy the allowed request headers. Other transforms")]
    public static YarpRoute WithTransformRequestHeadersAllowed(this YarpRoute route, params string[] allowedHeaders)
    {
        route.Configure(r => r.WithTransformRequestHeadersAllowed(allowedHeaders));
        return route;
    }
}
