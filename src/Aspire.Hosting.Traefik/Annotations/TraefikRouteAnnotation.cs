// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Records an HTTP route declared on a routed resource with <c>WithTraefikRoute</c>.
/// </summary>
internal sealed class TraefikRouteAnnotation(TraefikResource traefik, TraefikRouteOptions options, bool isSignInRoute) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Traefik resource that receives the requests.
    /// </summary>
    public TraefikResource Traefik { get; } = traefik;

    /// <summary>
    /// Gets the route options.
    /// </summary>
    public TraefikRouteOptions Options { get; } = options;

    /// <summary>
    /// Gets a value indicating whether the route was added by <see cref="TraefikRoutingSubscriber"/> to send
    /// <c>/oauth2/*</c> on a protected host to oauth2-proxy. Such routes are recreated whenever the routes are applied.
    /// </summary>
    public bool IsSignInRoute { get; } = isSignInRoute;
}
