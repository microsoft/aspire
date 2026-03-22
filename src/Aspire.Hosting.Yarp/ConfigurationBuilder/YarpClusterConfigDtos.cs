// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Http;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents forwarder request configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpForwarderRequestConfig
{
    /// <summary>
    /// Gets or sets the activity timeout.
    /// </summary>
    public TimeSpan? ActivityTimeout { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether response buffering is allowed.
    /// </summary>
    public bool? AllowResponseBuffering { get; set; }

    /// <summary>
    /// Gets or sets the HTTP version string.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the HTTP version policy.
    /// </summary>
    public HttpVersionPolicy? VersionPolicy { get; set; }
}

/// <summary>
/// Represents HTTP client configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpHttpClientConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether to accept any server certificate.
    /// </summary>
    public bool? DangerousAcceptAnyServerCertificate { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether multiple HTTP/2 connections are enabled.
    /// </summary>
    public bool? EnableMultipleHttp2Connections { get; set; }

    /// <summary>
    /// Gets or sets the maximum connections per server.
    /// </summary>
    public int? MaxConnectionsPerServer { get; set; }

    /// <summary>
    /// Gets or sets the request header encoding.
    /// </summary>
    public string? RequestHeaderEncoding { get; set; }

    /// <summary>
    /// Gets or sets the response header encoding.
    /// </summary>
    public string? ResponseHeaderEncoding { get; set; }

    /// <summary>
    /// Gets or sets the SSL protocols to enable.
    /// </summary>
    public YarpSslProtocol[]? SslProtocols { get; set; }

    /// <summary>
    /// Gets or sets the web proxy configuration.
    /// </summary>
    public YarpWebProxyConfig? WebProxy { get; set; }
}

/// <summary>
/// Represents web proxy configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpWebProxyConfig
{
    /// <summary>
    /// Gets or sets the proxy address.
    /// </summary>
    public Uri? Address { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether local addresses bypass the proxy.
    /// </summary>
    public bool? BypassOnLocal { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the default credentials are used.
    /// </summary>
    public bool? UseDefaultCredentials { get; set; }
}

/// <summary>
/// Represents session affinity configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpSessionAffinityConfig
{
    /// <summary>
    /// Gets or sets the affinity key name.
    /// </summary>
    public string? AffinityKeyName { get; set; }

    /// <summary>
    /// Gets or sets the cookie configuration.
    /// </summary>
    public YarpSessionAffinityCookieConfig? Cookie { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether session affinity is enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the failure policy.
    /// </summary>
    public string? FailurePolicy { get; set; }

    /// <summary>
    /// Gets or sets the session affinity policy.
    /// </summary>
    public string? Policy { get; set; }
}

/// <summary>
/// Represents session affinity cookie configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpSessionAffinityCookieConfig
{
    /// <summary>
    /// Gets or sets the cookie domain.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// Gets or sets the cookie expiration.
    /// </summary>
    public TimeSpan? Expiration { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cookie is HTTP only.
    /// </summary>
    public bool? HttpOnly { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cookie is essential.
    /// </summary>
    public bool? IsEssential { get; set; }

    /// <summary>
    /// Gets or sets the cookie max age.
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Gets or sets the cookie path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the same-site mode.
    /// </summary>
    public SameSiteMode? SameSite { get; set; }

    /// <summary>
    /// Gets or sets the secure policy.
    /// </summary>
    public CookieSecurePolicy? SecurePolicy { get; set; }
}

/// <summary>
/// Represents health check configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpHealthCheckConfig
{
    /// <summary>
    /// Gets or sets the active health check configuration.
    /// </summary>
    public YarpActiveHealthCheckConfig? Active { get; set; }

    /// <summary>
    /// Gets or sets the available destinations policy.
    /// </summary>
    public string? AvailableDestinationsPolicy { get; set; }

    /// <summary>
    /// Gets or sets the passive health check configuration.
    /// </summary>
    public YarpPassiveHealthCheckConfig? Passive { get; set; }
}

/// <summary>
/// Represents active health check configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpActiveHealthCheckConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether active health checks are enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the health check interval.
    /// </summary>
    public TimeSpan? Interval { get; set; }

    /// <summary>
    /// Gets or sets the health check path.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the health check policy.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets the health check query string.
    /// </summary>
    public string? Query { get; set; }

    /// <summary>
    /// Gets or sets the health check timeout.
    /// </summary>
    public TimeSpan? Timeout { get; set; }
}

/// <summary>
/// Represents passive health check configuration for a YARP cluster.
/// </summary>
[AspireDto]
internal sealed class YarpPassiveHealthCheckConfig
{
    /// <summary>
    /// Gets or sets a value indicating whether passive health checks are enabled.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>
    /// Gets or sets the health check policy.
    /// </summary>
    public string? Policy { get; set; }

    /// <summary>
    /// Gets or sets the reactivation period.
    /// </summary>
    public TimeSpan? ReactivationPeriod { get; set; }
}

/// <summary>
/// Specifies the SSL protocols to enable for a YARP cluster.
/// This enum exists because <see cref="System.Security.Authentication.SslProtocols"/> includes obsolete members and values that YARP does not accept.
/// </summary>
internal enum YarpSslProtocol
{
    /// <summary>
    /// No SSL protocol.
    /// </summary>
    None,

    /// <summary>
    /// TLS 1.2.
    /// </summary>
    Tls12,

    /// <summary>
    /// TLS 1.3.
    /// </summary>
    Tls13,
}
