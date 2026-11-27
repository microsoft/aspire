// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using YamlDotNet.Serialization;
using static Aspire.Hosting.Traefik.TraefikFileModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Generates the file-provider (dynamic) configuration used by the managed Traefik in run mode.
/// </summary>
/// <remarks>
/// The output is YAML because the file provider reads YAML or TOML, not JSON:
/// <code>
/// http:
///   routers:
///     api:
///       rule: Host(`api.dev.localhost`)
///       entryPoints:
///       - web
///       service: api
///   services:
///     api:
///       loadBalancer:
///         servers:
///         - url: http://host.docker.internal:5123
/// </code>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/routing/router/"/>.
/// <para>
/// At publish time routes are Docker labels instead, but entry-point middlewares and the dashboard router belong to
/// Traefik itself, so a managed Traefik gets a smaller file with only those (see <see cref="GeneratePublish"/>).
/// </para>
/// </remarks>
internal static class TraefikDynamicConfiguration
{
    /// <summary>
    /// The name of the generated routes file in Traefik's dynamic configuration directory.
    /// </summary>
    public const string FileName = "aspire.yml";

    /// <summary>
    /// The name of the run-mode file that sets Traefik's default certificate (see <see cref="TlsFileContent"/>).
    /// </summary>
    public const string TlsFileName = "aspire-tls.yml";

    /// <summary>
    /// The environment variable that holds the container path of the HTTPS certificate from Aspire, which
    /// <see cref="TlsFileContent"/> reads.
    /// </summary>
    public const string TlsCertificateVariable = "ASPIRE_TRAEFIK_TLS_CERTIFICATE";

    /// <summary>
    /// The environment variable that holds the container path of the certificate's private key, which
    /// <see cref="TlsFileContent"/> reads.
    /// </summary>
    public const string TlsKeyVariable = "ASPIRE_TRAEFIK_TLS_KEY";

    /// <summary>
    /// The run-mode file that makes the HTTPS certificate from Aspire Traefik's default certificate, which every TLS router
    /// without a certificate of its own presents.
    /// </summary>
    /// <remarks>
    /// The certificate paths are only known when the container is created (see <c>AddTraefik</c>), so the file reads them
    /// from the environment with the file provider's Go templating. Without a certificate the variables are unset, the
    /// file renders empty, and Traefik keeps its self-signed default certificate.
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/providers/others/file/#go-templating"/>
    /// and <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/tls/tls-certificates/#default-certificate"/>.
    /// </remarks>
    public const string TlsFileContent =
        $$$"""
        {{- if env "{{{TlsCertificateVariable}}}" }}
        tls:
          stores:
            default:
              defaultCertificate:
                certFile: {{ env "{{{TlsCertificateVariable}}}" | quote }}
                keyFile: {{ env "{{{TlsKeyVariable}}}" | quote }}
        {{- end }}

        """;

    // The service Traefik creates for its API and dashboard. It exists once `api.dashboard` is enabled.
    // https://doc.traefik.io/traefik/reference/install-configuration/api-dashboard/
    private const string DashboardService = "api@internal";

    /// <summary>
    /// Generates the run-mode file with the routes, middlewares, and dashboard router of a managed Traefik.
    /// </summary>
    /// <param name="traefik">The managed Traefik.</param>
    /// <param name="executionContext">The execution context of the run, which endpoints are resolved in.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The YAML content of <see cref="FileName"/>.</returns>
    public static async Task<string> GenerateAsync(TraefikResource traefik, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // Backend URLs and hosts are resolved from Traefik's point of view: containers by their name on the
        // Aspire container network, host processes through the container host (host.docker.internal, or the
        // Aspire container tunnel), so nothing about Docker Desktop or the tunnel is hardcoded here.
        var context = new ValueProviderContext
        {
            ExecutionContext = executionContext,
            Caller = traefik,
            Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
        };

        var defaults = traefik.GetRouteDefaults(isPublishMode: false);
        var http = new HttpConfiguration();

        foreach (var route in traefik.Routes)
        {
            http.Routers[route.RouterName] = new HttpRouter
            {
                Rule = await TraefikRouteRule.Build(route.Options).GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty,
                EntryPoints = [route.Options.EntryPoint ?? defaults.EntryPointName],
                Service = route.RouterName,
                Priority = route.Options.Priority,
                Middlewares = route.Middlewares.Count > 0 ? [.. route.Middlewares.Select(m => m.Name)] : null,
                // An empty `tls` section enables TLS with the default certificate. No resolver in run mode: see
                // TraefikResource.GetRouteDefaults.
                Tls = defaults.Tls && route.Options.EntryPoint is null ? new RouterTls() : null,
            };

            var url = await route.Endpoint.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The endpoint '{route.Endpoint.EndpointName}' of resource '{route.Resource.Name}' has no URL.");

            if (TraefikLoadBalancer.UsesH2c(route.Endpoint))
            {
                // The file provider takes the backend scheme from the server URL; `h2c://` is HTTP/2 without TLS.
                url = $"h2c{url["http".Length..]}";
            }

            var loadBalancer = TraefikLoadBalancer.Build(route);
            loadBalancer.Servers = [new HttpServer { Url = url }];
            http.Services[route.RouterName] = new HttpService { LoadBalancer = loadBalancer };
        }

        var document = new Document();
        foreach (var route in traefik.TransportRoutes)
        {
            var transport = route.Protocol == TraefikTransportRoute.UdpProtocol
                ? document.Udp ??= new TransportConfiguration()
                : document.Tcp ??= new TransportConfiguration();

            var router = new TransportRouter
            {
                EntryPoints = [route.EntryPoint],
                Service = route.RouterName,
            };

            if (route.Tcp is { } tcp)
            {
                router.Rule = await TraefikRouteRule.Build(tcp).GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty;
                router.Priority = tcp.Priority;

                if (tcp.TlsPassthrough)
                {
                    router.Tls = new RouterTls { Passthrough = true };
                }
                else if (tcp.Tls)
                {
                    // Traefik's default certificate, as for HTTP routes in run mode.
                    router.Tls = new RouterTls();
                }
            }

            transport.Routers[route.RouterName] = router;

            var address = await route.Endpoint.Property(EndpointProperty.HostAndPort).GetValueAsync(context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The endpoint '{route.Endpoint.EndpointName}' of resource '{route.Resource.Name}' has no address.");

            transport.Services[route.RouterName] = new TransportService
            {
                LoadBalancer = new TransportLoadBalancer { Servers = [new TransportServer { Address = address }] },
            };
        }

        if (traefik.DashboardHost is { } dashboardHost)
        {
            var rule = await BuildHostRule(dashboardHost).GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty;
            http.Routers[traefik.DashboardRouterName] = BuildDashboardRouter(traefik, rule, defaults.EntryPointName, defaults.Tls ? new RouterTls() : null);
        }

        // Every middleware of this Traefik is written, including unused ones, which Traefik accepts. Routes can also
        // carry middlewares that aren't resources, such as the one behind TraefikRouteOptions.StripPathPrefix.
        var middlewareResources = traefik.Middlewares.Concat(traefik.Routes.SelectMany(r => r.Middlewares)).Distinct().ToList();

        // The options are rendered synchronously (see TraefikOptionMap.ToFileConfiguration), so their expressions are
        // resolved first. Options hold the expression objects themselves, so they are matched by reference.
        var values = new Dictionary<ReferenceExpression, string>(ReferenceEqualityComparer.Instance);
        foreach (var expression in middlewareResources.SelectMany(m => m.Options.GetExpressions()))
        {
            values[expression] = await expression.GetValueAsync(context, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        }

        foreach (var middleware in middlewareResources)
        {
            http.Middlewares[middleware.Name] = new()
            {
                [middleware.Type] = middleware.Options.ToFileConfiguration(expression => values[expression], literal => literal),
            };
        }

        document.Http = http.IsEmpty ? null : http;
        return Serialize(document);
    }

    /// <summary>
    /// Generates the dynamic configuration of a managed Traefik at publish time: its entry-point middlewares, the
    /// dashboard router, and the middlewares they use.
    /// </summary>
    /// <param name="traefik">The managed Traefik.</param>
    /// <param name="composeService">The Docker Compose service resource of Traefik, which owns the placeholders.</param>
    /// <param name="service">The Docker Compose service of Traefik, whose environment holds the resolved endpoint values.</param>
    /// <returns>The YAML content of the Docker Compose config.</returns>
    /// <remarks>
    /// The file becomes a Docker Compose config, whose content Docker Compose interpolates like the rest of the file:
    /// <code>
    /// http:
    ///   routers:
    ///     traefik-dashboard:
    ///       rule: Host(`traefik.${DOMAIN}`)
    ///       entryPoints:
    ///       - websecure
    ///       service: api@internal
    ///       middlewares:
    ///       - dashboard-auth
    ///       tls:
    ///         certResolver: letsencrypt
    ///   middlewares:
    ///     dashboard-auth:
    ///       basicAuth:
    ///         users:
    ///         - ${DASHBOARD_HTPASSWD}
    /// </code>
    /// So parameters become <c>${VAR}</c> placeholders and a literal <c>$</c> is written as <c>$$</c>, as in labels.
    /// See <see href="https://docs.docker.com/reference/compose-file/configs/"/>.
    /// </remarks>
    public static string GeneratePublish(TraefikResource traefik, DockerComposeServiceResource composeService, Service service)
    {
        var defaults = traefik.GetRouteDefaults(isPublishMode: true);
        var http = new HttpConfiguration();

        if (traefik.DashboardHost is { } dashboardHost)
        {
            var tls = defaults.Tls
                ? new RouterTls
                {
                    CertResolver = defaults.CertResolver is { } certResolver ? TraefikDockerLabels.ToComposeExpression(composeService, certResolver) : null,
                }
                : null;

            http.Routers[traefik.DashboardRouterName] = BuildDashboardRouter(
                traefik,
                TraefikDockerLabels.ToComposeExpression(composeService, BuildHostRule(dashboardHost)),
                TraefikDockerLabels.ToComposeExpression(composeService, defaults.EntryPoint),
                tls);
        }

        // Unlike run mode, only the middlewares Traefik itself uses: the routes' middlewares are labels on the routed
        // services. A chain refers to its members by name, so their definitions go along with it.
        foreach (var middleware in GetPublishMiddlewares(traefik))
        {
            var probes = TraefikDockerLabels.GetMiddlewareValueProbes(middleware);
            var options = middleware.Options.ToFileConfiguration(
                expression => probes.TryGetValue(expression, out var variable) && service.Environment.TryGetValue(variable, out var resolved)
                    ? resolved
                    : TraefikDockerLabels.ToComposeExpression(composeService, expression),
                TraefikDockerLabels.EscapeLiteral);

            if (middleware.ErrorPageResource is not null && options.TryGetValue("service", out var errorService))
            {
                // The service of the error-page route is defined by labels, so a file-provider middleware has to name
                // the Docker provider.
                // https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/errorpages/
                options["service"] = $"{errorService}@docker";
            }

            http.Middlewares[middleware.Name] = new() { [middleware.Type] = options };
        }

        return Serialize(new Document { Http = http.IsEmpty ? null : http });
    }

    /// <summary>
    /// Gets the middlewares that a managed Traefik's own file has to define at publish time: the entry-point and
    /// dashboard middlewares, and the members of the chains among them.
    /// </summary>
    /// <param name="traefik">The managed Traefik.</param>
    /// <returns>The middlewares, including the members of chains, each once.</returns>
    public static List<TraefikMiddlewareResource> GetPublishMiddlewares(TraefikResource traefik) =>
        traefik.EntryPointMiddlewares.Values.SelectMany(m => m)
            .Concat(traefik.DashboardMiddlewares)
            .SelectMany(m => m.WithReferences())
            .Distinct()
            .ToList();

    private static ReferenceExpression BuildHostRule(ReferenceExpression host) =>
        TraefikRouteRule.Build(new TraefikRouteOptions { Host = host });

    private static HttpRouter BuildDashboardRouter(TraefikResource traefik, string rule, string entryPoint, RouterTls? tls) =>
        new()
        {
            Rule = rule,
            EntryPoints = [entryPoint],
            Service = DashboardService,
            Middlewares = [.. traefik.DashboardMiddlewares.Select(m => m.Name)],
            Tls = tls,
        };

    private static string Serialize(Document document)
    {
        var serializer = new SerializerBuilder()
            .DisableAliases()
            // Unset options are left out, so Traefik's defaults apply.
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            // The file is read by Traefik on Linux, and at publish time it is embedded in the compose file, so it
            // shouldn't depend on the line endings of the machine that generates it.
            .WithNewLine("\n")
            // Quote strings that YAML would read as another type, such as the string `port: "8443"` of redirectScheme
            // or a header value of `true`; Traefik's decoder doesn't convert an int into a string field.
            .WithQuotingNecessaryStrings()
            .Build();

        return serializer.Serialize(document);
    }
}
