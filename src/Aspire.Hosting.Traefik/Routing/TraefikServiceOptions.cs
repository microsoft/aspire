// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for sticky sessions, which send the requests of a client to the same replica of a routed resource.
/// </summary>
/// <remarks>
/// Traefik sets a cookie on the first response, naming the server that answered, and sends later requests with that
/// cookie to the same server for as long as it is healthy.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/#sticky-sessions"/>.
/// </remarks>
[AspireDto]
public sealed class TraefikStickySessionOptions
{
    /// <summary>
    /// Gets or sets the name of the cookie. When <see langword="null"/>, Traefik derives a name from the service.
    /// </summary>
    public string? CookieName { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cookie is only sent over HTTPS.
    /// </summary>
    public bool Secure { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cookie is hidden from scripts in the browser. Defaults to
    /// <see langword="true"/>, because the cookie only matters to Traefik.
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets the <c>SameSite</c> attribute of the cookie. When <see langword="null"/>, the cookie has none.
    /// </summary>
    public TraefikSameSiteMode? SameSite { get; set; }

    /// <summary>
    /// Gets or sets how long the cookie lasts, in seconds. When <see langword="null"/>, it lasts until the browser is
    /// closed.
    /// </summary>
    public int? MaxAgeSeconds { get; set; }
}

/// <summary>
/// The <c>SameSite</c> attribute of a cookie set by Traefik.
/// </summary>
public enum TraefikSameSiteMode
{
    /// <summary>
    /// The cookie is sent with cross-site requests too. Browsers require <see cref="TraefikStickySessionOptions.Secure"/>
    /// with it.
    /// </summary>
    None,

    /// <summary>
    /// The cookie is sent with top-level cross-site navigations, but not with other cross-site requests.
    /// </summary>
    Lax,

    /// <summary>
    /// The cookie is only sent with same-site requests.
    /// </summary>
    Strict,
}

/// <summary>
/// Options for the active health check Traefik runs against the servers of a route. Traefik stops sending requests to
/// a server whose check fails, and starts again once it passes.
/// </summary>
/// <remarks>
/// The check uses the scheme and port of the routed endpoint.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/#health-check"/>.
/// </remarks>
[AspireDto]
public sealed class TraefikHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the path of the health check, for example <c>/health</c>.
    /// </summary>
    public string Path { get; set; } = "/";

    /// <summary>
    /// Gets or sets the number of seconds between checks. When <see langword="null"/>, Traefik's default (30 seconds).
    /// </summary>
    public int? IntervalSeconds { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds after which a check fails. When <see langword="null"/>, Traefik's default
    /// (5 seconds).
    /// </summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// Gets or sets the status code a healthy server answers with. When <see langword="null"/>, any 2xx or 3xx code.
    /// </summary>
    public int? Status { get; set; }

    /// <summary>
    /// Gets or sets the HTTP method of the check. When <see langword="null"/>, <c>GET</c>.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>
    /// Gets or sets the value of the <c>Host</c> header of the check, for a server that only answers on its public name.
    /// </summary>
    public string? Hostname { get; set; }
}
