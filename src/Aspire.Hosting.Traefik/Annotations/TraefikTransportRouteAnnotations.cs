// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Records a TCP route declared on a routed resource with <c>WithTraefikTcpRoute</c>.
/// </summary>
internal sealed class TraefikTcpRouteAnnotation(TraefikResource traefik, TraefikTcpRouteOptions options) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Traefik resource that receives the connections.
    /// </summary>
    public TraefikResource Traefik { get; } = traefik;

    /// <summary>
    /// Gets the route options.
    /// </summary>
    public TraefikTcpRouteOptions Options { get; } = options;
}

/// <summary>
/// Records a UDP route declared on a routed resource with <c>WithTraefikUdpRoute</c>. UDP routers have no rule: every
/// datagram on the entry point goes to the route's service.
/// </summary>
internal sealed class TraefikUdpRouteAnnotation(TraefikResource traefik, string entryPoint, string? endpointName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the Traefik resource that receives the datagrams.
    /// </summary>
    public TraefikResource Traefik { get; } = traefik;

    /// <summary>
    /// Gets the UDP entry point the route takes.
    /// </summary>
    public string EntryPoint { get; } = entryPoint;

    /// <summary>
    /// Gets the endpoint that receives the datagrams, or <see langword="null"/> to pick it by protocol.
    /// </summary>
    public string? EndpointName { get; } = endpointName;
}
