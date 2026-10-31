// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// The shape of Traefik's file-provider (dynamic) configuration that <see cref="TraefikDynamicConfiguration"/> writes.
/// </summary>
/// <remarks>
/// <para>
/// Only the sections Aspire generates are modeled, keyed as in the file provider. Properties left <see langword="null"/>
/// are omitted (see <see cref="TraefikDynamicConfiguration"/>), so Traefik's defaults apply, and properties are written in
/// declaration order. Middleware options stay untyped: each middleware type has its own options, which
/// <see cref="TraefikOptionMap"/> holds.
/// </para>
/// <para>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/router/"/>,
/// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/"/> and
/// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/router/"/>.
/// </para>
/// </remarks>
internal static class TraefikFileModel
{
    public sealed class Document
    {
        [YamlMember(Alias = "http")]
        public HttpConfiguration? Http { get; set; }

        [YamlMember(Alias = "tcp")]
        public TransportConfiguration? Tcp { get; set; }

        [YamlMember(Alias = "udp")]
        public TransportConfiguration? Udp { get; set; }
    }

    public sealed class HttpConfiguration
    {
        [YamlMember(Alias = "routers", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
        public Dictionary<string, HttpRouter> Routers { get; } = [];

        [YamlMember(Alias = "services", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
        public Dictionary<string, HttpService> Services { get; } = [];

        /// <summary>
        /// The middlewares by name, each with a single entry from its type, such as <c>basicAuth</c>, to its options.
        /// </summary>
        [YamlMember(Alias = "middlewares", DefaultValuesHandling = DefaultValuesHandling.OmitEmptyCollections)]
        public Dictionary<string, Dictionary<string, Dictionary<string, object>>> Middlewares { get; } = [];

        [YamlIgnore]
        public bool IsEmpty => Routers.Count == 0 && Services.Count == 0 && Middlewares.Count == 0;
    }

    public sealed class HttpRouter
    {
        [YamlMember(Alias = "rule")]
        public required string Rule { get; set; }

        [YamlMember(Alias = "entryPoints")]
        public required string[] EntryPoints { get; set; }

        [YamlMember(Alias = "service")]
        public required string Service { get; set; }

        [YamlMember(Alias = "priority")]
        public int? Priority { get; set; }

        /// <summary>
        /// The middlewares of the router. Those defined in the same file need no <c>@file</c> provider suffix.
        /// </summary>
        [YamlMember(Alias = "middlewares")]
        public string[]? Middlewares { get; set; }

        [YamlMember(Alias = "tls")]
        public RouterTls? Tls { get; set; }
    }

    /// <summary>
    /// The <c>tls</c> section of a router. Empty, it enables TLS with the default certificate.
    /// </summary>
    public sealed class RouterTls
    {
        [YamlMember(Alias = "certResolver")]
        public string? CertResolver { get; set; }

        /// <summary>
        /// Whether a TCP router passes the TLS connection to the backend as is.
        /// </summary>
        [YamlMember(Alias = "passthrough")]
        public bool? Passthrough { get; set; }
    }

    public sealed class HttpService
    {
        [YamlMember(Alias = "loadBalancer")]
        public required HttpLoadBalancer LoadBalancer { get; set; }
    }

    public sealed class HttpLoadBalancer
    {
        /// <summary>
        /// The backends. Docker labels pin the server port and scheme instead (see <see cref="TraefikDockerLabels"/>), so
        /// they leave it <see langword="null"/>.
        /// </summary>
        [YamlMember(Alias = "servers")]
        public HttpServer[]? Servers { get; set; }

        [YamlMember(Alias = "passHostHeader")]
        public bool? PassHostHeader { get; set; }

        [YamlMember(Alias = "sticky")]
        public Sticky? Sticky { get; set; }

        [YamlMember(Alias = "healthCheck")]
        public HealthCheck? HealthCheck { get; set; }
    }

    public sealed class HttpServer
    {
        /// <summary>
        /// The URL of the backend. Its scheme picks the protocol, such as <c>h2c://</c> for HTTP/2 without TLS.
        /// </summary>
        [YamlMember(Alias = "url")]
        public required string Url { get; set; }
    }

    public sealed class Sticky
    {
        [YamlMember(Alias = "cookie")]
        public required StickyCookie Cookie { get; set; }
    }

    public sealed class StickyCookie
    {
        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "secure")]
        public bool Secure { get; set; }

        [YamlMember(Alias = "httpOnly")]
        public bool HttpOnly { get; set; }

        /// <summary>
        /// <c>none</c>, <c>lax</c> or <c>strict</c>.
        /// </summary>
        [YamlMember(Alias = "sameSite")]
        public string? SameSite { get; set; }

        [YamlMember(Alias = "maxAge")]
        public int? MaxAge { get; set; }
    }

    public sealed class HealthCheck
    {
        [YamlMember(Alias = "path")]
        public required string Path { get; set; }

        /// <summary>
        /// A Go duration, such as <c>10s</c>.
        /// </summary>
        [YamlMember(Alias = "interval")]
        public string? Interval { get; set; }

        /// <summary>
        /// A Go duration, such as <c>2s</c>.
        /// </summary>
        [YamlMember(Alias = "timeout")]
        public string? Timeout { get; set; }

        [YamlMember(Alias = "status")]
        public int? Status { get; set; }

        [YamlMember(Alias = "method")]
        public string? Method { get; set; }

        [YamlMember(Alias = "hostname")]
        public string? Hostname { get; set; }
    }

    /// <summary>
    /// The <c>tcp</c> or <c>udp</c> section, which has the shape of <c>http</c> without middlewares.
    /// </summary>
    public sealed class TransportConfiguration
    {
        [YamlMember(Alias = "routers")]
        public Dictionary<string, TransportRouter> Routers { get; } = [];

        [YamlMember(Alias = "services")]
        public Dictionary<string, TransportService> Services { get; } = [];
    }

    /// <summary>
    /// A TCP or UDP router. UDP routers have no rule, priority or TLS: a UDP entry point forwards everything to one
    /// service.
    /// </summary>
    public sealed class TransportRouter
    {
        [YamlMember(Alias = "entryPoints")]
        public required string[] EntryPoints { get; set; }

        [YamlMember(Alias = "service")]
        public required string Service { get; set; }

        [YamlMember(Alias = "rule")]
        public string? Rule { get; set; }

        [YamlMember(Alias = "priority")]
        public int? Priority { get; set; }

        [YamlMember(Alias = "tls")]
        public RouterTls? Tls { get; set; }
    }

    public sealed class TransportService
    {
        [YamlMember(Alias = "loadBalancer")]
        public required TransportLoadBalancer LoadBalancer { get; set; }
    }

    public sealed class TransportLoadBalancer
    {
        [YamlMember(Alias = "servers")]
        public required TransportServer[] Servers { get; set; }
    }

    public sealed class TransportServer
    {
        /// <summary>
        /// The <c>host:port</c> of the backend. TCP and UDP servers are addresses, not URLs.
        /// </summary>
        [YamlMember(Alias = "address")]
        public required string Address { get; set; }
    }
}
