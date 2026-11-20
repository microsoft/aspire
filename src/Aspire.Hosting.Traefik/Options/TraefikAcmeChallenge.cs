// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// The ACME challenge Traefik uses to prove control of a domain.
/// </summary>
/// <remarks>
/// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
/// </remarks>
public enum TraefikAcmeChallenge
{
    /// <summary>
    /// The TLS-ALPN-01 challenge, answered on the <c>websecure</c> entry point (port 443).
    /// </summary>
    TlsAlpn,

    /// <summary>
    /// The HTTP-01 challenge, answered on the <c>web</c> entry point (port 80).
    /// </summary>
    Http,
}
