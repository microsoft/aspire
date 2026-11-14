// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Traefik;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a Traefik Proxy instance used as the ingress of the application.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <remarks>
/// <para>
/// When the application runs locally, Traefik is a container managed by Aspire. Routes declared with
/// <c>WithTraefikRoute</c> on other resources are written to a generated file-provider configuration.
/// </para>
/// <para>
/// When the application is published to Docker Compose, the routes become <c>traefik.*</c> labels on the
/// routed services, and Traefik discovers them through its Docker provider. A Traefik that already runs on
/// the target host can be used instead with <c>PublishAsExternal</c>.
/// </para>
/// </remarks>
[AspireExport]
public class TraefikResource(string name) : ContainerResource(name), IResourceWithServiceDiscovery
{
    /// <summary>
    /// The name of the plain HTTP entry point, which every managed Traefik has. Use it where an entry point is named,
    /// such as <see cref="TraefikRouteOptions.EntryPoint"/> or <c>WithEntryPointMiddleware</c>.
    /// </summary>
    public const string WebEntryPointName = "web";

    /// <summary>
    /// The name of the HTTPS entry point, which a managed Traefik has once <c>WithLetsEncrypt</c> is called. Use it
    /// where an entry point is named, such as <see cref="TraefikRouteOptions.EntryPoint"/> or
    /// <c>WithEntryPointMiddleware</c>.
    /// </summary>
    public const string WebSecureEntryPointName = "websecure";

    /// <summary>
    /// The entry point of Traefik's API, dashboard, and ping endpoint. It isn't meant for routes, so it stays internal.
    /// </summary>
    internal const string TraefikEntryPointName = "traefik";

    /// <summary>
    /// The container port of the <c>web</c> entry point.
    /// </summary>
    internal const int WebTargetPort = 80;

    /// <summary>
    /// The container port of the <c>websecure</c> entry point.
    /// </summary>
    internal const int WebSecureTargetPort = 443;

    /// <summary>
    /// The container port of the <c>traefik</c> entry point.
    /// </summary>
    internal const int TraefikTargetPort = 8080;

    /// <summary>
    /// The directory inside the container that Traefik's file provider reads.
    /// </summary>
    internal const string DynamicConfigurationDirectory = "/etc/traefik/dynamic";

    /// <summary>
    /// The directory inside the container that holds the ACME certificate storage (<c>acme.json</c>), a volume at
    /// publish time.
    /// </summary>
    internal const string AcmeStorageDirectory = "/letsencrypt";

    /// <summary>
    /// The path of the Docker socket inside the Traefik and socket-proxy containers, and its default path on the host.
    /// </summary>
    internal const string DockerSocketPath = "/var/run/docker.sock";

    private EndpointReference? _webEndpoint;

    /// <summary>
    /// Gets the endpoint of the <c>web</c> entry point (plain HTTP).
    /// </summary>
    public EndpointReference WebEndpoint => _webEndpoint ??= new(this, WebEntryPointName);

    /// <summary>
    /// Gets the custom entry points added with <c>WithEntryPoint</c>.
    /// </summary>
    internal List<TraefikEntryPoint> CustomEntryPoints { get; } = [];

    /// <summary>
    /// Gets or sets the HTTPS host port requested with <c>WithHostHttpsPort</c>, applied when the <c>websecure</c>
    /// entry point is created.
    /// </summary>
    internal int? HostHttpsPort { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether requests to the <c>web</c> entry point are redirected to <c>websecure</c>.
    /// </summary>
    internal bool HttpsRedirectEnabled { get; set; }

    /// <summary>
    /// Gets the middlewares that apply to every router on an entry point, by entry point name, added with
    /// <c>WithEntryPointMiddleware</c>.
    /// </summary>
    internal OrderedDictionary<string, List<TraefikMiddlewareResource>> EntryPointMiddlewares { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the ACME (Let's Encrypt) certificate resolvers, in the order they were added.
    /// </summary>
    internal List<TraefikAcmeResolver> AcmeResolvers { get; } = [];

    /// <summary>
    /// Gets the resolver that routes and the <c>websecure</c> entry point use unless a route names another: the first
    /// one added.
    /// </summary>
    internal TraefikAcmeResolver? DefaultAcmeResolver => AcmeResolvers.Count > 0 ? AcmeResolvers[0] : null;

    /// <summary>
    /// Gets a value indicating whether the <c>websecure</c> entry point exists on the managed container.
    /// </summary>
    internal bool HasWebSecureEntryPoint => AcmeResolvers.Count > 0;

    /// <summary>
    /// Gets the certificate domains of the <c>websecure</c> entry point, added with <c>WithTlsDomain</c>.
    /// </summary>
    internal List<TraefikTlsDomain> TlsDomains { get; } = [];

    /// <summary>
    /// Gets or sets the ACME directory URL set with <c>WithAcmeCaServer</c>, if any.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="AcmeResolvers"/> so it doesn't depend on the order of the calls. It applies to every
    /// resolver.
    /// </remarks>
    internal ReferenceExpression? AcmeCaServer { get; set; }

    /// <summary>
    /// Gets the middlewares added with <c>AddMiddleware</c> and the other middleware methods.
    /// </summary>
    internal List<TraefikMiddlewareResource> Middlewares { get; } = [];

    /// <summary>
    /// Gets the routes that target this Traefik. Populated by <see cref="TraefikRoutingSubscriber"/> before the
    /// application starts or publishes.
    /// </summary>
    internal List<TraefikRoute> Routes { get; } = [];

    /// <summary>
    /// Gets the TCP and UDP routes that target this Traefik. Populated by <see cref="TraefikRoutingSubscriber"/>, like
    /// <see cref="Routes"/>.
    /// </summary>
    internal List<TraefikTransportRoute> TransportRoutes { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the Traefik dashboard and API are enabled.
    /// </summary>
    internal bool DashboardEnabled { get; set; }

    /// <summary>
    /// Gets or sets the host the dashboard is routed on with <c>WithDashboard(host, middlewares)</c>, if any.
    /// </summary>
    internal ReferenceExpression? DashboardHost { get; set; }

    /// <summary>
    /// Gets the middlewares that protect the dashboard router.
    /// </summary>
    internal List<TraefikMiddlewareResource> DashboardMiddlewares { get; } = [];

    /// <summary>
    /// Gets the name of the router that serves the dashboard on <see cref="DashboardHost"/>.
    /// </summary>
    internal string DashboardRouterName => $"{Name}-dashboard";

    /// <summary>
    /// Gets or sets the level of Traefik's logs set with <c>WithLogLevel</c>, if any.
    /// </summary>
    internal TraefikLogLevel? LogLevel { get; set; }

    /// <summary>
    /// Gets or sets the access log options set with <c>WithAccessLogs</c>, if access logs are enabled.
    /// </summary>
    internal TraefikAccessLogOptions? AccessLogs { get; set; }

    /// <summary>
    /// Gets or sets the Docker socket proxy that the Docker provider reads through at publish time, added with
    /// <c>WithDockerSocketProxy</c>.
    /// </summary>
    internal ContainerResource? DockerSocketProxy { get; set; }

    /// <summary>
    /// Gets or sets the path of the Docker socket on the deployment host, which the Docker provider reads at publish time.
    /// </summary>
    internal string DockerSocketHostPath { get; set; } = DockerSocketPath;

    /// <summary>
    /// Gets or sets the settings of the external Traefik used at publish time, if one is configured.
    /// </summary>
    internal TraefikExternalSettings? PublishExternal { get; set; }

    /// <summary>
    /// Gets the entry points of the external Traefik described with <c>WithExternalEntryPoint</c>, used at publish time
    /// with <c>PublishAsExternal</c>.
    /// </summary>
    internal List<TraefikExternalEntryPoint> ExternalEntryPoints { get; } = [];

    /// <summary>
    /// Gets or sets the OTLP gRPC endpoint of the Docker Compose environment's dashboard that Traefik sends telemetry to at
    /// publish time, if that dashboard is enabled.
    /// </summary>
    internal EndpointReference? PublishOtlpEndpoint { get; set; }

    /// <summary>
    /// Gets a value indicating whether the managed container reads a dynamic configuration file at publish time. Routes
    /// are labels then, but entry-point middlewares and the dashboard router belong to Traefik itself.
    /// </summary>
    internal bool HasPublishFileConfiguration => PublishExternal is null && (EntryPointMiddlewares.Count > 0 || DashboardHost is not null);

    /// <summary>
    /// Gets or sets the dynamic configuration written for the managed container at publish time, set while the Docker Compose
    /// file is generated. See <see cref="TraefikDynamicConfiguration.GeneratePublish"/>.
    /// </summary>
    internal TraefikPublishFileConfiguration? PublishFileConfiguration { get; set; }

    /// <summary>
    /// Gets the entry point and TLS settings of the routes that don't set their own.
    /// </summary>
    /// <param name="isPublishMode">Whether the routes are for the published application.</param>
    /// <returns>The defaults.</returns>
    internal TraefikRouteDefaults GetRouteDefaults(bool isPublishMode)
    {
        if (isPublishMode && PublishExternal is { } external)
        {
            // The external Traefik owns its entry points and certificates. A router on a TLS entry point that
            // has no resolver of its own still gets TLS from the entry point's `http.tls` settings, so only
            // name the resolver when one was given. A parameterized entry point is assumed to be HTTPS, because its
            // value is only known when the compose file is deployed.
            var secure = external.CertResolver is not null
                || TraefikNames.GetLiteral(external.EntryPoint) is not { } entryPoint
                || IsTlsEntryPoint(entryPoint, isPublishMode);
            return new(external.EntryPoint, Tls: external.CertResolver is not null, external.CertResolver, external.Network, secure);
        }

        // The `websecure` entry point exists exactly when a resolver does (see HasWebSecureEntryPoint).
        if (DefaultAcmeResolver is { } defaultResolver)
        {
            // Let's Encrypt can't issue certificates for a local `aspire run`: the ACME server must reach the
            // machine on public ports 80/443 under a public DNS name. Run mode therefore still terminates TLS on
            // `websecure`, but with the Aspire HTTPS certificate (the developer certificate) as Traefik's default
            // certificate and no resolver; see TraefikDynamicConfiguration.TlsFileContent.
            var resolver = isPublishMode ? ReferenceExpression.Create($"{defaultResolver.Name}") : null;
            return new(ReferenceExpression.Create($"{WebSecureEntryPointName}"), Tls: true, resolver, Network: null, IsSecure: true);
        }

        return new(ReferenceExpression.Create($"{WebEntryPointName}"), Tls: false, CertResolver: null, Network: null, IsSecure: false);
    }

    /// <summary>
    /// Gets the public port of an entry point at publish time, as clients connect to it.
    /// </summary>
    /// <param name="entryPoint">The entry point name.</param>
    /// <returns>The port, or <see langword="null"/> when it isn't known, in which case the standard port is assumed.</returns>
    internal int? GetPublishPort(string entryPoint)
    {
        if (PublishExternal is not null)
        {
            return ExternalEntryPoints.FirstOrDefault(e => e.Name == entryPoint)?.Port;
        }

        // Every entry point of the managed container has an endpoint of the same name, whose host port is the public
        // port at publish time: the target port unless WithHostPort, WithHostHttpsPort, or WithEntryPoint set another.
        return Annotations.OfType<EndpointAnnotation>().FirstOrDefault(e => e.Name == entryPoint)?.Port;
    }

    /// <summary>
    /// Determines whether clients reach an entry point over TLS.
    /// </summary>
    /// <param name="entryPoint">The entry point name.</param>
    /// <param name="isPublishMode">Whether the entry point is for the published application.</param>
    /// <returns><see langword="true"/> when the entry point terminates TLS; otherwise, <see langword="false"/>.</returns>
    internal bool IsTlsEntryPoint(string entryPoint, bool isPublishMode)
    {
        if (isPublishMode && PublishExternal is not null)
        {
            // An external Traefik's entry points are only known when described with WithExternalEntryPoint. Otherwise
            // the usual convention applies: only `web` is plain HTTP.
            return ExternalEntryPoints.FirstOrDefault(e => e.Name == entryPoint) is { } external
                ? external.IsTls
                : entryPoint != WebEntryPointName;
        }

        return entryPoint == WebSecureEntryPointName
            || CustomEntryPoints.Any(e => e.Name == entryPoint && e.IsTls);
    }

    /// <summary>
    /// Finds a certificate resolver added with <c>WithLetsEncrypt</c> by name.
    /// </summary>
    /// <param name="name">The resolver name.</param>
    /// <returns>The resolver, or <see langword="null"/> when there is none with that name.</returns>
    internal TraefikAcmeResolver? FindAcmeResolver(string name) =>
        // Resolver names are keys of Traefik's static configuration, which it matches case-insensitively.
        AcmeResolvers.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Gets a value indicating whether the managed container has an entry point for routes: <c>web</c>, <c>websecure</c>
    /// once TLS is configured, or one added with <c>WithEntryPoint</c>.
    /// </summary>
    /// <param name="name">The entry point name.</param>
    /// <returns><see langword="true"/> when the entry point exists; otherwise, <see langword="false"/>.</returns>
    internal bool HasEntryPoint(string name) => name switch
    {
        WebEntryPointName => true,
        WebSecureEntryPointName => HasWebSecureEntryPoint,
        _ => CustomEntryPoints.Any(e => e.Name == name),
    };
}
