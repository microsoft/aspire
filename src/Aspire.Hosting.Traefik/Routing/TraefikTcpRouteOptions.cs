// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for a TCP route from Traefik to a resource, for protocols other than HTTP, such as AMQP or a database
/// protocol.
/// </summary>
/// <remarks>
/// <para>
/// Without TLS, Traefik can't tell connections apart, so a plain TCP route takes every connection to its entry point
/// (<c>HostSNI(`*`)</c>) and usually has an entry point of its own, added with <c>WithEntryPoint</c>.
/// </para>
/// <para>
/// With TLS, Traefik reads the server name the client asks for (SNI), so several TCP routes, and HTTPS routes, can
/// share an entry point. Traefik either terminates TLS (<see cref="Tls"/>) and forwards plain TCP, or forwards the
/// encrypted connection as is (<see cref="TlsPassthrough"/>).
/// </para>
/// <para>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/router/"/>.
/// </para>
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikTcpRouteOptions
{
    /// <summary>
    /// Gets or sets the entry point the router listens on, for example one added with <c>WithEntryPoint</c>. Required.
    /// </summary>
    public string? EntryPoint { get; set; }

    /// <summary>
    /// Gets or sets the server name (SNI) the route matches, for example <c>amqp.example.com</c>. It needs
    /// <see cref="Tls"/> or <see cref="TlsPassthrough"/>, because a client only sends a server name during the TLS
    /// handshake. When <see langword="null"/>, the route takes every connection.
    /// </summary>
    public ReferenceExpression? HostSni { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Traefik terminates TLS and forwards plain TCP to the resource.
    /// </summary>
    /// <remarks>
    /// At publish time the certificate comes from <see cref="CertResolver"/> or the default resolver, as for HTTP
    /// routes. In run mode Traefik uses its default certificate, the HTTPS certificate from Aspire.
    /// </remarks>
    public bool Tls { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Traefik forwards the TLS connection without decrypting it, so the
    /// resource terminates TLS with its own certificate.
    /// </summary>
    public bool TlsPassthrough { get; set; }

    /// <summary>
    /// Gets or sets the certificate resolver of the route when published, instead of the default one. It needs
    /// <see cref="Tls"/>.
    /// </summary>
    public string? CertResolver { get; set; }

    /// <summary>
    /// Gets or sets the router priority. When unset, Traefik uses the length of the rule.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Gets or sets the name of the endpoint on the routed resource that receives the connections. Defaults to
    /// <c>tcp</c>, or to the resource's only endpoint.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Gets or sets the name of the Traefik router and service. Defaults to the resource name, with a numeric suffix
    /// for the second and later TCP routes of the same resource.
    /// </summary>
    public string? RouterName { get; set; }
}
