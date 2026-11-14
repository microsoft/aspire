// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// The entry point and TLS settings applied to a route that doesn't override them.
/// </summary>
/// <param name="EntryPoint">
/// The entry point the router listens on. Only an external Traefik at publish time can use a parameterized name;
/// for the managed Traefik it is always a literal (see <see cref="TraefikRouteDefaults.EntryPointName"/>).
/// </param>
/// <param name="Tls">Whether the router enables TLS itself (<c>tls: {}</c> / <c>tls=true</c>).</param>
/// <param name="CertResolver">The certificate resolver the router uses, if any.</param>
/// <param name="Network">The shared Docker network routed services must join (external Traefik only).</param>
/// <param name="IsSecure">Whether clients reach the entry point over HTTPS. Used for public URLs.</param>
internal sealed record TraefikRouteDefaults(ReferenceExpression EntryPoint, bool Tls, ReferenceExpression? CertResolver, string? Network, bool IsSecure)
{
    /// <summary>
    /// Gets the entry point name of a route on the managed Traefik, whose entry points are always literal names.
    /// </summary>
    public string EntryPointName => TraefikNames.GetLiteral(EntryPoint)
        ?? throw new InvalidOperationException("The entry point name is parameterized, which is only supported for an external Traefik at publish time.");
}

/// <summary>
/// A route resolved by <see cref="TraefikRoutingSubscriber"/>: the annotation plus its final router name, backend
/// endpoint, and middlewares in the order Traefik runs them.
/// </summary>
internal sealed record TraefikRoute(
    TraefikResource Traefik,
    IResourceWithEndpoints Resource,
    string RouterName,
    EndpointReference Endpoint,
    TraefikRouteOptions Options,
    IReadOnlyList<TraefikMiddlewareResource> Middlewares);
