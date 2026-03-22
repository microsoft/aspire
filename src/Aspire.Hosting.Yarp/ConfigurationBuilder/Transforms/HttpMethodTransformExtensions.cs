// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Yarp.Transforms;
using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for modifying the request method.
/// </summary>
public static class HttpMethodTransformExtensions
{
    /// <summary>
    /// Adds the transform that will replace the HTTP method if it matches.
    /// </summary>
    [AspireExport("withTransformHttpMethodChange", Description = "Adds the transform that will replace the HTTP method if it matches.")]
    public static YarpRoute WithTransformHttpMethodChange(this YarpRoute route, string fromHttpMethod, string toHttpMethod)
    {
        route.Configure(r => r.WithTransformHttpMethodChange(fromHttpMethod, toHttpMethod));
        return route;
    }
}
