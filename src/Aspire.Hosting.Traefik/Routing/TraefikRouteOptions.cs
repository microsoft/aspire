// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for an HTTP route from Traefik to a resource.
/// </summary>
/// <remarks>
/// The route becomes a Traefik router and a Traefik service with the same name. The router rule combines the matcher
/// options (<see cref="Host"/>, <see cref="PathPrefix"/>, <see cref="Path"/>, <see cref="Methods"/>, <see cref="Headers"/>,
/// <see cref="Query"/>, <see cref="ClientIps"/>, and <see cref="Rule"/>) with <c>&amp;&amp;</c>, for example
/// <c>Host(`api.example.com`) &amp;&amp; PathPrefix(`/v1`)</c>.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/rules-and-priority/"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikRouteOptions
{
    /// <summary>
    /// Gets or sets the host name the route matches, for example <c>api.example.com</c>.
    /// </summary>
    /// <remarks>
    /// The value can reference parameters, for example <c>ReferenceExpression.Create($"api.{domain}")</c>. At publish
    /// time parameter values stay placeholders in the generated files.
    /// </remarks>
    public ReferenceExpression? Host { get; set; }

    /// <summary>
    /// Gets or sets the path prefix the route matches, for example <c>/api</c>. The prefix is forwarded to the
    /// backend unchanged unless <see cref="StripPathPrefix"/> is set.
    /// </summary>
    public string? PathPrefix { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="PathPrefix"/> is removed from the path before the request is
    /// forwarded, so a backend that serves <c>/orders</c> can be published as <c>/api/orders</c>.
    /// </summary>
    /// <remarks>
    /// Adds a <c>stripPrefix</c> middleware named <c>&lt;router name&gt;-strip</c>, which runs after the route's other
    /// middlewares. Traefik sets the removed prefix in the <c>X-Forwarded-Prefix</c> header.
    /// </remarks>
    public bool StripPathPrefix { get; set; }

    /// <summary>
    /// Gets or sets the exact path the route matches, for example <c>/health</c>.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets the HTTP methods the route matches. When several are set, any of them matches.
    /// </summary>
    public IList<string> Methods { get; } = [];

    /// <summary>
    /// Gets the request headers the route requires, by name and exact value.
    /// </summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the query parameters the route requires, by name and exact value.
    /// </summary>
    public IDictionary<string, string> Query { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the client addresses or CIDR ranges the route matches, for example <c>10.0.0.0/8</c>. When several are set,
    /// any of them matches. <c>X-Forwarded-For</c> is ignored.
    /// </summary>
    public IList<string> ClientIps { get; } = [];

    /// <summary>
    /// Gets or sets an additional rule for conditions the other options can't express, such as alternatives,
    /// negations, or regular expressions. It is combined with the other options with <c>&amp;&amp;</c>.
    /// </summary>
    /// <example>
    /// <code lang="csharp">
    /// route.Host = ReferenceExpression.Create($"api.{domain}");
    /// route.Rule = TraefikRule.PathPrefix("/v1") | TraefikRule.Header("X-Api-Version", "1");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "TraefikRule is built with C# operators. Polyglot AppHosts use the matcher properties or RawRule.")]
    public TraefikRule? Rule { get; set; }

    /// <summary>
    /// Gets or sets the whole router rule in Traefik's syntax, for example <c>Host(`a.example.com`) || Host(`b.example.com`)</c>.
    /// It can't be combined with the other matcher options, except <see cref="Host"/>.
    /// </summary>
    /// <remarks>
    /// Prefer the other options: they quote values and keep parameters as placeholders at publish time. With a raw
    /// rule, <see cref="Host"/> isn't added to the rule; it only gives the route its public URL (see
    /// <c>GetTraefikUrl</c>) and, with oauth2-proxy, its sign-in route. Set it to a host the rule matches, because a
    /// rule alone has no single public URL.
    /// </remarks>
    public string? RawRule { get; set; }

    /// <summary>
    /// Gets or sets the certificate resolver of the route when published, instead of the default one: the first resolver
    /// added with <c>WithLetsEncrypt</c>, or the one given to <c>PublishAsExternal</c>.
    /// </summary>
    /// <remarks>
    /// Setting it enables TLS on the route at publish time, also on an entry point set with <see cref="EntryPoint"/>.
    /// The managed Traefik must have a resolver with that name; for an external Traefik, the name isn't checked. In run
    /// mode certificates aren't requested, so it has no effect.
    /// </remarks>
    public string? CertResolver { get; set; }

    /// <summary>
    /// Gets or sets the router priority. When unset, Traefik uses the length of the rule, so more specific rules win.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Gets or sets the name of the endpoint on the routed resource that receives the traffic. Defaults to <c>http</c>,
    /// or to the resource's only HTTP endpoint when it has no endpoint named <c>http</c>.
    /// </summary>
    public string? EndpointName { get; set; }

    /// <summary>
    /// Gets or sets the name of the Traefik router and service. Defaults to the resource name, with a numeric
    /// suffix for the second and later routes of the same resource.
    /// </summary>
    public string? RouterName { get; set; }

    /// <summary>
    /// Gets or sets the entry point the router listens on. Defaults to <c>websecure</c> when TLS is configured on
    /// Traefik and to <c>web</c> otherwise.
    /// </summary>
    public string? EntryPoint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the backend receives the <c>Host</c> header the client sent. When
    /// <see langword="null"/>, Traefik's default (<see langword="true"/>) applies; <see langword="false"/> sends the
    /// backend's own address instead, for a backend that only answers on its own name.
    /// </summary>
    public bool? PassHostHeader { get; set; }

    /// <summary>
    /// Gets or sets the sticky session options, which keep a client on the same replica. When <see langword="null"/>,
    /// requests are spread across replicas.
    /// </summary>
    public TraefikStickySessionOptions? StickySession { get; set; }

    /// <summary>
    /// Gets or sets the active health check Traefik runs against the backend.
    /// </summary>
    /// <remarks>
    /// When <see langword="null"/> and <see cref="UseResourceProbe"/> is set, the health check is taken from an HTTP
    /// readiness or liveness probe of the routed endpoint, added with <c>WithHttpProbe</c>.
    /// </remarks>
    public TraefikHealthCheckOptions? HealthCheck { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an HTTP probe of the routed endpoint (<c>WithHttpProbe</c>) becomes
    /// Traefik's health check when <see cref="HealthCheck"/> isn't set. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// A readiness probe is preferred over a liveness probe, because it says whether the resource can take requests.
    /// Startup probes and probes of other endpoints aren't used.
    /// </remarks>
    public bool UseResourceProbe { get; set; } = true;

    /// <summary>
    /// Gets the middlewares of this route, in the order Traefik runs them. They run after the middlewares added to
    /// the whole resource with <c>WithTraefikMiddleware</c>.
    /// </summary>
    /// <remarks>
    /// The middlewares must belong to the Traefik resource the route goes through.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts pass middlewares to withTraefikRoute or withTraefikMiddleware.")]
    public IList<TraefikMiddlewareResource> Middlewares { get; } = [];
}
