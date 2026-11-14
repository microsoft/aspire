// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Yarp;

internal static class YarpStaticFileFallback
{
    private const string FallbackName = "aspireSpaFallback";

    public static void EnableForStaticFiles<T>(IResourceBuilder<T> builder)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        GetOrAddAnnotation(builder).StaticFilesEnabled = true;
    }

    public static void SetEnabled<T>(IResourceBuilder<T> builder, bool enabled)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        GetOrAddAnnotation(builder).Enabled = enabled;
    }

    private static FallbackAnnotation GetOrAddAnnotation<T>(IResourceBuilder<T> builder)
        where T : IResourceWithEnvironment, IResourceWithEndpoints
    {
        if (builder.Resource.TryGetLastAnnotation<FallbackAnnotation>(out var annotation))
        {
            return annotation;
        }

        annotation = new FallbackAnnotation();
        builder.WithAnnotation(annotation);
        builder.WithEnvironment(context =>
        {
            var environment = context.EnvironmentVariables;

            // Leave image defaults unchanged when no fallback was explicitly configured.
            if (annotation.Enabled is null)
            {
                return;
            }

            // Newer YARP images can add a native fallback when static files are enabled.
            // Disable that implicit route while Aspire controls fallback and its opt-out.
            environment["YARP_DISABLE_SPA_FALLBACK"] = "true";
        });

        builder.WithAnnotation(new EnvironmentCallbackAnnotation((EnvironmentCallbackContext context) =>
        {
            if (annotation.Enabled is not true || !annotation.StaticFilesEnabled)
            {
                return;
            }

            var environment = context.EnvironmentVariables;
            var name = FindAvailableName(environment);
            var route = $"REVERSEPROXY__ROUTES__{name}";
            var cluster = $"REVERSEPROXY__CLUSTERS__{name}";

            // Until the published YARP image contains https://github.com/dotnet/yarp/pull/3006,
            // proxy to index.html on the same container. The :nonfile constraint excludes that
            // destination, preventing a loop. The order matches ASP.NET Core's MapFallbackToFile.
            environment[$"{route}__CLUSTERID"] = name;
            environment[$"{route}__ORDER"] = int.MaxValue.ToString(CultureInfo.InvariantCulture);
            environment[$"{route}__MATCH__PATH"] = "/{**path:nonfile}";
            environment[$"{route}__MATCH__METHODS__0"] = "GET";
            environment[$"{route}__MATCH__METHODS__1"] = "HEAD";
            // A wildcard matches all hosts but gives this route precedence over an explicitly
            // configured native fallback, avoiding an ambiguous endpoint match.
            // https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/header-routing#precedence
            environment[$"{route}__MATCH__HOSTS__0"] = "*";
            environment[$"{route}__TRANSFORMS__0__PATHSET"] = "/index.html";
            // Host filtering is applied again by the loopback request.
            environment[$"{route}__TRANSFORMS__1__REQUESTHEADERORIGINALHOST"] = "true";
            environment[$"{cluster}__DESTINATIONS__self__ADDRESS"] = ReferenceExpression.Create(
                $"http://127.0.0.1:{builder.Resource.GetEndpoint("http").Property(EndpointProperty.TargetPort)}");
        })
        {
            ExecuteAfterOtherCallbacks = true
        });

        return annotation;
    }

    private static string FindAvailableName(Dictionary<string, object> environment)
    {
        // Environment keys such as ReverseProxy__Clusters__name__... and
        // ReverseProxy:Clusters:name:... map to the same .NET configuration path.
        var keys = environment.Keys.Select(static key => key.Replace("__", ":", StringComparison.Ordinal)).ToArray();
        var index = 0;
        while (true)
        {
            var name = index == 0 ? FallbackName : FallbackName + index.ToString(CultureInfo.InvariantCulture);
            var routePrefix = $"REVERSEPROXY:ROUTES:{name}:";
            var clusterPrefix = $"REVERSEPROXY:CLUSTERS:{name}:";
            if (!keys.Any(key => key.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase) ||
                                 key.StartsWith(clusterPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }

            index++;
        }
    }

    private sealed class FallbackAnnotation : IResourceAnnotation
    {
        public bool? Enabled { get; set; }

        public bool StaticFilesEnabled { get; set; }
    }
}
