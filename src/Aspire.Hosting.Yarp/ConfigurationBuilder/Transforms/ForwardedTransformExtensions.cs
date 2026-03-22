// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Yarp.Transforms;
using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for adding forwarded header transforms.
/// </summary>
public static class ForwardedTransformExtensions
{
    /// <summary>
    /// Adds the transform which will add X-Forwarded-* headers.
    /// </summary>
    [AspireExport("withTransformXForwarded", Description = "Adds the transform which will add X-Forwarded-* headers.")]
    public static YarpRoute WithTransformXForwarded(
        this YarpRoute route,
        string headerPrefix = "X-Forwarded-",
        ForwardedTransformActions xDefault = ForwardedTransformActions.Set,
        ForwardedTransformActions? xFor = null,
        ForwardedTransformActions? xHost = null,
        ForwardedTransformActions? xProto = null,
        ForwardedTransformActions? xPrefix = null)
    {
        route.Configure(r => r.WithTransformXForwarded(headerPrefix, xDefault, xFor, xHost, xProto, xPrefix));
        return route;
    }

    /// <summary>
    /// Adds the transform which will add the Forwarded header as defined by [RFC 7239](https://tools.ietf.org/html/rfc7239).
    /// </summary>
    [AspireExport("withTransformForwarded", Description = "Adds the transform which will add the Forwarded header as defined by [RFC 7239](https://tools.ietf.org/html/rfc7239).")]
    public static YarpRoute WithTransformForwarded(this YarpRoute route, bool useHost = true, bool useProto = true,
        NodeFormat forFormat = NodeFormat.Random, NodeFormat byFormat = NodeFormat.Random, ForwardedTransformActions action = ForwardedTransformActions.Set)
    {
        route.Configure(r => r.WithTransformForwarded(useHost, useProto, forFormat, byFormat, action));
        return route;
    }

    /// <summary>
    /// Adds the transform which will set the given header with the Base64 encoded client certificate.
    /// </summary>
    [AspireExport("withTransformClientCertHeader", Description = "Adds the transform which will set the given header with the Base64 encoded client certificate.")]
    public static YarpRoute WithTransformClientCertHeader(this YarpRoute route, string headerName)
    {
        route.Configure(r => r.WithTransformClientCertHeader(headerName));
        return route;
    }
}
