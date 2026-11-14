// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// A TCP or UDP route resolved by <see cref="TraefikRoutingSubscriber"/>.
/// </summary>
/// <param name="Traefik">The Traefik resource that receives the connections.</param>
/// <param name="Resource">The routed resource.</param>
/// <param name="RouterName">The name of the router and of its service.</param>
/// <param name="Endpoint">The endpoint of the routed resource that receives the connections.</param>
/// <param name="EntryPoint">The entry point the router listens on.</param>
/// <param name="Tcp">The options of a TCP route, or <see langword="null"/> for a UDP route.</param>
internal sealed record TraefikTransportRoute(
    TraefikResource Traefik,
    IResourceWithEndpoints Resource,
    string RouterName,
    EndpointReference Endpoint,
    string EntryPoint,
    TraefikTcpRouteOptions? Tcp)
{
    /// <summary>
    /// The configuration section and label namespace of TCP routes.
    /// </summary>
    public const string TcpProtocol = "tcp";

    /// <summary>
    /// The configuration section and label namespace of UDP routes.
    /// </summary>
    public const string UdpProtocol = "udp";

    /// <summary>
    /// Gets the configuration section of the route: <c>tcp</c> or <c>udp</c>.
    /// </summary>
    public string Protocol => GetProtocol(Tcp);

    /// <summary>
    /// Gets the protocol of a route from its TCP options, which only TCP routes have.
    /// </summary>
    /// <param name="tcp">The TCP options of the route, or <see langword="null"/> for a UDP route.</param>
    /// <returns><see cref="TcpProtocol"/> or <see cref="UdpProtocol"/>.</returns>
    public static string GetProtocol(TraefikTcpRouteOptions? tcp) => tcp is null ? UdpProtocol : TcpProtocol;
}
