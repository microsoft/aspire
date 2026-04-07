// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Yarp.Transforms;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Transforms;

namespace Aspire.Hosting.Yarp.Transforms;

/// <summary>
/// Extensions for adding path transforms.
/// </summary>
public static class PathTransformExtensions
{
    /// <summary>
    /// Adds the transform which sets the request path with the given value.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the string-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "PathString is not ATS-compatible. Use the string-based overload instead.")]
    public static YarpRoute WithTransformPathSet(this YarpRoute route, PathString path)
    {
        if (path.Value is null)
        {
            throw new ArgumentNullException(nameof(path));
        }

        route.Configure(r => r.WithTransformPathSet(path));

        return route;
    }

    /// <summary>
    /// Adds the transform which sets the request path with the given value.
    /// </summary>
    /// <param name="route">The route to configure.</param>
    /// <param name="path">The path value to set.</param>
    /// <returns>The configured <see cref="YarpRoute"/>.</returns>
    [AspireExport(Description = "Adds the transform which sets the request path with the given value.")]
    internal static YarpRoute WithTransformPathSet(this YarpRoute route, string path)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(path);

        return route.WithTransformPathSet(new PathString(path));
    }

    /// <summary>
    /// Adds the transform which will prefix the request path with the given value.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the string-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "PathString is not ATS-compatible. Use the string-based overload instead.")]
    public static YarpRoute WithTransformPathPrefix(this YarpRoute route, PathString prefix)
    {
        if (prefix.Value is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        route.Configure(r => r.WithTransformPathPrefix(prefix));

        return route;
    }

    /// <summary>
    /// Adds the transform which will prefix the request path with the given value.
    /// </summary>
    /// <param name="route">The route to configure.</param>
    /// <param name="prefix">The path prefix to add.</param>
    /// <returns>The configured <see cref="YarpRoute"/>.</returns>
    [AspireExport(Description = "Adds the transform which will prefix the request path with the given value.")]
    internal static YarpRoute WithTransformPathPrefix(this YarpRoute route, string prefix)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(prefix);

        return route.WithTransformPathPrefix(new PathString(prefix));
    }

    /// <summary>
    /// Adds the transform which will remove the matching prefix from the request path.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the string-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "PathString is not ATS-compatible. Use the string-based overload instead.")]
    public static YarpRoute WithTransformPathRemovePrefix(this YarpRoute route, PathString prefix)
    {
        if (prefix.Value is null)
        {
            throw new ArgumentNullException(nameof(prefix));
        }

        route.Configure(r => r.WithTransformPathRemovePrefix(prefix));

        return route;
    }

    /// <summary>
    /// Adds the transform which will remove the matching prefix from the request path.
    /// </summary>
    /// <param name="route">The route to configure.</param>
    /// <param name="prefix">The matching prefix to remove.</param>
    /// <returns>The configured <see cref="YarpRoute"/>.</returns>
    [AspireExport(Description = "Adds the transform which will remove the matching prefix from the request path.")]
    internal static YarpRoute WithTransformPathRemovePrefix(this YarpRoute route, string prefix)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(prefix);

        return route.WithTransformPathRemovePrefix(new PathString(prefix));
    }

    /// <summary>
    /// Adds the transform which will set the request path with the given value.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the string-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "PathString is not ATS-compatible. Use the string-based overload instead.")]
    public static YarpRoute WithTransformPathRouteValues(this YarpRoute route, [StringSyntax("Route")] PathString pattern)
    {
        if (pattern.Value is null)
        {
            throw new ArgumentNullException(nameof(pattern));
        }

        route.Configure(r => r.WithTransformPathRouteValues(pattern));

        return route;
    }

    /// <summary>
    /// Adds the transform which will set the request path with route values.
    /// </summary>
    /// <param name="route">The route to configure.</param>
    /// <param name="pattern">The route pattern to apply.</param>
    /// <returns>The configured <see cref="YarpRoute"/>.</returns>
    [AspireExport(Description = "Adds the transform which will set the request path with route values.")]
    internal static YarpRoute WithTransformPathRouteValues(this YarpRoute route, [StringSyntax("Route")] string pattern)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(pattern);

        return route.WithTransformPathRouteValues(new PathString(pattern));
    }
}
