// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Builds the options of the load balancer behind an HTTP route, apart from its servers.
/// </summary>
/// <remarks>
/// The load balancer is built once, as the file provider's model, and written either as YAML (run mode) or flattened
/// into lowercase labels (publish):
/// <code>
/// loadBalancer:                          traefik.http.services.api.loadbalancer.passhostheader: "false"
///   passHostHeader: false                traefik.http.services.api.loadbalancer.sticky.cookie.name: affinity
///   sticky:                              traefik.http.services.api.loadbalancer.healthcheck.path: /health
///     cookie:                            traefik.http.services.api.loadbalancer.healthcheck.interval: 5s
///       name: affinity
///   healthCheck:
///     path: /health
///     interval: 5s
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/"/>.
/// </remarks>
internal static class TraefikLoadBalancer
{
    /// <summary>
    /// Builds the load balancer of a route without its servers. Options the route doesn't set are left out, so
    /// Traefik's defaults apply.
    /// </summary>
    /// <param name="route">The HTTP route.</param>
    /// <returns>The load balancer of the route's service, without its servers.</returns>
    public static TraefikFileModel.HttpLoadBalancer Build(TraefikRoute route) =>
        new()
        {
            PassHostHeader = route.Options.PassHostHeader,
            Sticky = route.Options.StickySession is { } sticky
                ? new TraefikFileModel.Sticky
                {
                    Cookie = new TraefikFileModel.StickyCookie
                    {
                        Name = sticky.CookieName,
                        Secure = sticky.Secure,
                        HttpOnly = sticky.HttpOnly,
                        SameSite = sticky.SameSite?.ToString().ToLowerInvariant(),
                        MaxAge = sticky.MaxAgeSeconds,
                    },
                }
                : null,
            HealthCheck = GetHealthCheck(route) is { } healthCheck
                ? new TraefikFileModel.HealthCheck
                {
                    Path = healthCheck.Path,
                    Interval = healthCheck.IntervalSeconds is int interval ? TraefikNames.ToDuration(TimeSpan.FromSeconds(interval), nameof(healthCheck.IntervalSeconds)) : null,
                    Timeout = healthCheck.TimeoutSeconds is int timeout ? TraefikNames.ToDuration(TimeSpan.FromSeconds(timeout), nameof(healthCheck.TimeoutSeconds)) : null,
                    Status = healthCheck.Status,
                    Method = healthCheck.Method,
                    Hostname = healthCheck.Hostname,
                }
                : null,
        };

    /// <summary>
    /// Gets the health check of a route: its own, or the one taken from an HTTP probe of the routed endpoint.
    /// </summary>
    /// <param name="route">The HTTP route.</param>
    /// <returns>The route's own health check, one built from the resource's readiness or liveness probe, or <see langword="null"/>.</returns>
    /// <remarks>
    /// A readiness probe says whether the resource can take requests, which is what a load balancer needs to know, so it
    /// wins over a liveness probe. A startup probe only matters until the resource has started. A probe of another
    /// endpoint is skipped: Traefik checks the servers of the route, which are addresses of the routed endpoint.
    /// </remarks>
    public static TraefikHealthCheckOptions? GetHealthCheck(TraefikRoute route)
    {
        if (route.Options.HealthCheck is { } healthCheck)
        {
            return healthCheck;
        }

        if (!route.Options.UseResourceProbe)
        {
            return null;
        }

        var probe = route.Resource.Annotations.OfType<EndpointProbeAnnotation>()
            .Where(p => p.Type is ProbeType.Readiness or ProbeType.Liveness)
            .Where(p => p.EndpointReference.Resource == route.Resource && string.Equals(p.EndpointReference.EndpointName, route.Endpoint.EndpointName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Type == ProbeType.Readiness ? 0 : 1)
            .FirstOrDefault();

        if (probe is null)
        {
            return null;
        }

        return new TraefikHealthCheckOptions
        {
            Path = probe.Path,
            // Zero means unset on a probe annotation built by hand; Traefik's defaults apply then.
            IntervalSeconds = probe.PeriodSeconds > 0 ? probe.PeriodSeconds : null,
            TimeoutSeconds = probe.TimeoutSeconds > 0 ? probe.TimeoutSeconds : null,
        };
    }

    /// <summary>
    /// Gets a value indicating whether Traefik talks to the endpoint over HTTP/2 without TLS (h2c), which an endpoint
    /// that only speaks HTTP/2, such as a gRPC service, needs. Traefik uses HTTP/1.1 for <c>http</c> backends otherwise.
    /// </summary>
    /// <param name="endpoint">The backend endpoint.</param>
    /// <returns><see langword="true"/> when the endpoint is plain HTTP and its transport is HTTP/2; otherwise, <see langword="false"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/user-guides/grpc/"/>.</remarks>
    public static bool UsesH2c(EndpointReference endpoint) =>
        string.Equals(endpoint.EndpointAnnotation.UriScheme, "http", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(endpoint.EndpointAnnotation.Transport, "http2", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes a load balancer built by <see cref="Build"/> as labels under <paramref name="prefix"/>, for example
    /// <c>traefik.http.services.api.loadbalancer</c>. The Docker provider reads label keys case-insensitively, and
    /// Traefik's own examples use lowercase. The servers aren't written: the labels pin the server port instead.
    /// </summary>
    /// <param name="labels">The labels of the routed service.</param>
    /// <param name="prefix">The label key of the load balancer, for example <c>traefik.http.services.api.loadbalancer</c>.</param>
    /// <param name="loadBalancer">The load balancer, from <see cref="Build"/>.</param>
    public static void AddLabels(IDictionary<string, string> labels, string prefix, TraefikFileModel.HttpLoadBalancer loadBalancer)
    {
        if (loadBalancer.PassHostHeader is bool passHostHeader)
        {
            labels[$"{prefix}.passhostheader"] = TraefikNames.ToLiteral(passHostHeader);
        }

        if (loadBalancer.Sticky?.Cookie is { } cookie)
        {
            var path = $"{prefix}.sticky.cookie";

            // The cookie section is enabled by its own key, even when all its options are set.
            labels[path] = "true";

            if (cookie.Name is { } name)
            {
                labels[$"{path}.name"] = TraefikDockerLabels.EscapeLiteral(name);
            }

            labels[$"{path}.secure"] = TraefikNames.ToLiteral(cookie.Secure);
            labels[$"{path}.httponly"] = TraefikNames.ToLiteral(cookie.HttpOnly);

            if (cookie.SameSite is { } sameSite)
            {
                labels[$"{path}.samesite"] = sameSite;
            }

            if (cookie.MaxAge is int maxAge)
            {
                labels[$"{path}.maxage"] = maxAge.ToString(CultureInfo.InvariantCulture);
            }
        }

        if (loadBalancer.HealthCheck is { } healthCheck)
        {
            var path = $"{prefix}.healthcheck";
            labels[$"{path}.path"] = TraefikDockerLabels.EscapeLiteral(healthCheck.Path);

            if (healthCheck.Interval is { } interval)
            {
                labels[$"{path}.interval"] = interval;
            }

            if (healthCheck.Timeout is { } timeout)
            {
                labels[$"{path}.timeout"] = timeout;
            }

            if (healthCheck.Status is int status)
            {
                labels[$"{path}.status"] = status.ToString(CultureInfo.InvariantCulture);
            }

            if (healthCheck.Method is { } method)
            {
                labels[$"{path}.method"] = TraefikDockerLabels.EscapeLiteral(method);
            }

            if (healthCheck.Hostname is { } hostname)
            {
                labels[$"{path}.hostname"] = TraefikDockerLabels.EscapeLiteral(hostname);
            }
        }
    }
}
