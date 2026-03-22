// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for adding response header and trailer transforms.
/// </summary>
public static class ResponseTransformExtensions
{
    /// <summary>
    /// Adds the transform which will enable or suppress copying response headers to the client response.
    /// </summary>
    [AspireExport("withTransformCopyResponseHeaders", Description = "Adds the transform which will enable or suppress copying response headers to the client response.")]
    public static YarpRoute WithTransformCopyResponseHeaders(this YarpRoute route, bool copy = true)
    {
        route.Configure(r => r.WithTransformCopyResponseHeaders(copy));
        return route;
    }

    /// <summary>
    /// Adds the transform which will enable or suppress copying response trailers to the client response.
    /// </summary>
    [AspireExport("withTransformCopyResponseTrailers", Description = "Adds the transform which will enable or suppress copying response trailers to the client response.")]
    public static YarpRoute WithTransformCopyResponseTrailers(this YarpRoute route, bool copy = true)
    {
        route.Configure(r => r.WithTransformCopyResponseTrailers(copy));
        return route;
    }

    /// <summary>
    /// Adds the transform which will append or set the response header.
    /// </summary>
    [AspireExport("withTransformResponseHeader", Description = "Adds the transform which will append or set the response header.")]
    public static YarpRoute WithTransformResponseHeader(this YarpRoute route, string headerName, string value, bool append = true, ResponseCondition condition = ResponseCondition.Success)
    {
        route.Configure(r => r.WithTransformResponseHeader(headerName, value, append, condition));
        return route;
    }

    /// <summary>
    /// Adds the transform which will remove the response header.
    /// </summary>
    [AspireExport("withTransformResponseHeaderRemove", Description = "Adds the transform which will remove the response header.")]
    public static YarpRoute WithTransformResponseHeaderRemove(this YarpRoute route, string headerName, ResponseCondition condition = ResponseCondition.Success)
    {
        route.Configure(r => r.WithTransformResponseHeaderRemove(headerName, condition));
        return route;
    }

    /// <summary>
    /// Adds the transform which will only copy the allowed response headers. Other transforms
    /// that modify or append to existing headers may be affected if not included in the allow list.
    /// </summary>
    [AspireExport("withTransformResponseHeadersAllowed", Description = "Adds the transform which will only copy the allowed response headers. Other transforms")]
    public static YarpRoute WithTransformResponseHeadersAllowed(this YarpRoute route, params string[] allowedHeaders)
    {
        route.Configure(r => r.WithTransformResponseHeadersAllowed(allowedHeaders));
        return route;
    }

    /// <summary>
    /// Adds the transform which will append or set the response trailer.
    /// </summary>
    [AspireExport("withTransformResponseTrailer", Description = "Adds the transform which will append or set the response trailer.")]
    public static YarpRoute WithTransformResponseTrailer(this YarpRoute route, string headerName, string value, bool append = true, ResponseCondition condition = ResponseCondition.Success)
    {
        route.Configure(r => r.WithTransformResponseTrailer(headerName, value, append, condition));
        return route;
    }

    /// <summary>
    /// Adds the transform which will remove the response trailer.
    /// </summary>
    [AspireExport("withTransformResponseTrailerRemove", Description = "Adds the transform which will remove the response trailer.")]
    public static YarpRoute WithTransformResponseTrailerRemove(this YarpRoute route, string headerName, ResponseCondition condition = ResponseCondition.Success)
    {
        route.Configure(r => r.WithTransformResponseTrailerRemove(headerName, condition));
        return route;
    }

    /// <summary>
    /// Adds the transform which will only copy the allowed response trailers. Other transforms
    /// that modify or append to existing trailers may be affected if not included in the allow list.
    /// </summary>
    [AspireExport("withTransformResponseTrailersAllowed", Description = "Adds the transform which will only copy the allowed response trailers. Other transforms")]
    public static YarpRoute WithTransformResponseTrailersAllowed(this YarpRoute route, params string[] allowedHeaders)
    {
        route.Configure(r => r.WithTransformResponseTrailersAllowed(allowedHeaders));
        return route;
    }
}
