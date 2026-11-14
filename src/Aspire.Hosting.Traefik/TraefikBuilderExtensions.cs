// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECERTIFICATES001 // WithHttpsCertificateConfiguration is experimental.

using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Traefik Proxy to the application model.
/// </summary>
public static partial class TraefikBuilderExtensions
{
    /// <summary>
    /// The endpoint of the socket proxy container that Traefik's Docker provider connects to.
    /// </summary>
    internal const string SocketProxyEndpointName = "docker";

    // The prefix of the variables that reference each backend from Traefik's environment in run mode.
    private const string BackendVariablePrefix = "ASPIRE_TRAEFIK_BACKEND_";

    // The port docker-socket-proxy serves the filtered Docker API on.
    private const int SocketProxyPort = 2375;

    private const int MaxPort = 65535;

    /// <summary>
    /// Adds a Traefik Proxy container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The resource has a <c>web</c> entry point for HTTP traffic (container port 80, on a random host port in run
    /// mode and on port 80 at publish time) and a <c>traefik</c> entry point (container port 8080) for the ping
    /// health check and, with <see cref="WithDashboard(IResourceBuilder{TraefikResource})"/>, the dashboard.
    /// </para>
    /// <para>
    /// Route traffic to other resources with <c>WithTraefikRoute</c> on those resources. In run mode the routes are
    /// written to a file-provider configuration inside the container, and Traefik's traces and metrics are sent to
    /// the Aspire dashboard. At publish time Traefik reads the routes from Docker labels on the routed services,
    /// through the Docker socket, which is mounted read-only.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithDashboard();
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, "api.dev.localhost");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> AddTraefik(this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var resource = new TraefikResource(name);
        var isPublishMode = builder.ExecutionContext.IsPublishMode;

        builder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        var traefik = builder.AddResource(resource)
            .WithImage(TraefikContainerImageTags.Image, TraefikContainerImageTags.Tag)
            .WithImageRegistry(TraefikContainerImageTags.Registry)
            .WithIconName("ArrowRouting")
            // An internet-facing proxy listens on the standard port at publish time. In run mode the host port is
            // random, like any other Aspire endpoint, unless WithHostPort pins it.
            .WithHttpEndpoint(name: TraefikResource.WebEntryPointName, port: isPublishMode ? TraefikResource.WebTargetPort : null, targetPort: TraefikResource.WebTargetPort)
            .WithEndpoint(TraefikResource.WebEntryPointName, endpoint => endpoint.IsExternal = true)
            .WithHttpEndpoint(name: TraefikResource.TraefikEntryPointName, targetPort: TraefikResource.TraefikTargetPort)
            .WithHttpHealthCheck("/ping", endpointName: TraefikResource.TraefikEntryPointName)
            .WithArgs(context => TraefikStaticConfiguration.AddArguments(resource, context));

        if (isPublishMode)
        {
            // The Docker provider discovers routed services through the Docker API. Read-only limits what the
            // proxy can do with the socket, but access to it is still powerful; see the README for alternatives.
            traefik.WithBindMount(resource.DockerSocketHostPath, TraefikResource.DockerSocketPath, isReadOnly: true);

            traefik.WithAnnotation(new DockerComposeServiceCustomizationAnnotation((_, service) => SetUdpPortProtocols(resource, service)));

            // DNS provider credentials of DNS-01 resolvers. Only at publish time, because run mode doesn't request
            // certificates, so `aspire run` doesn't ask for the secrets. lego reads them from the environment.
            // https://go-acme.github.io/lego/dns/
            traefik.WithEnvironment(context =>
            {
                foreach (var dns in resource.AcmeResolvers.Select(r => r.DnsChallenge).OfType<TraefikDnsChallenge>())
                {
                    foreach (var (name, parameter) in dns.Credentials)
                    {
                        context.EnvironmentVariables[name] = parameter;
                    }
                }
            });

            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                // Send telemetry to the dashboard the Docker Compose environment deploys (on by default). Traefik
                // ignores the OTEL_* variables the environment sets, so the endpoint becomes Traefik's own flags (see
                // TraefikStaticConfiguration). Environments are usually added after Traefik, so they are looked up
                // here, and without an explicit compute environment only a single one is unambiguous.
                var environment = resource.GetComputeEnvironment() as DockerComposeEnvironmentResource
                    ?? (@event.Model.Resources.OfType<DockerComposeEnvironmentResource>().ToArray() is [var single] ? single : null);

                if (environment is { DashboardEnabled: true })
                {
                    // The dashboard resource is only reachable through WithDashboard's callback. It keeps the
                    // dashboard enabled, which it already is.
                    builder.CreateResourceBuilder(environment)
                        .WithDashboard(dashboard => resource.PublishOtlpEndpoint = dashboard.Resource.OtlpGrpcEndpoint);
                }

                // Traefik's own dynamic configuration (entry-point middlewares, the dashboard router) is generated with
                // its compose service (see TraefikRoutingSubscriber), but configs are top-level entries of the compose
                // file, which only the environment's ConfigureComposeFile can add.
                foreach (var composeEnvironment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
                {
                    builder.CreateResourceBuilder(composeEnvironment).ConfigureComposeFile(file =>
                    {
                        if (resource.PublishFileConfiguration is { } configuration && configuration.Environment == composeEnvironment)
                        {
                            file.AddConfig(new Config { Name = configuration.Name, Content = configuration.Content });
                        }
                    });
                }

                return Task.CompletedTask;
            });
        }
        else
        {
            var executionContext = builder.ExecutionContext;

            traefik
                .WithOtlpExporter()
                .WithEnvironment(context =>
                {
                    // Reference every backend endpoint from Traefik's environment. Aspire only makes a host
                    // process (project, executable) reachable from the container network when a container
                    // references it in its environment or arguments, and the dynamic configuration file isn't
                    // one of those. The values also show the resolved backend addresses in the dashboard.
                    foreach (var route in resource.Routes)
                    {
                        context.EnvironmentVariables[$"{BackendVariablePrefix}{TraefikNames.ToEnvironmentName(route.RouterName)}"] = route.Endpoint;
                    }

                    // TCP and UDP routers have their own namespaces, so their names can repeat an HTTP router's.
                    foreach (var route in resource.TransportRoutes)
                    {
                        context.EnvironmentVariables[$"{BackendVariablePrefix}{route.Protocol.ToUpperInvariant()}_{TraefikNames.ToEnvironmentName(route.RouterName)}"] =
                            route.Endpoint.Property(EndpointProperty.HostAndPort);
                    }

                    // The same applies to middleware options that point at another resource, such as a forward
                    // authentication address.
                    foreach (var middleware in resource.Middlewares)
                    {
                        var index = 0;
                        foreach (var expression in middleware.Options.GetEndpointExpressions())
                        {
                            context.EnvironmentVariables[TraefikDockerLabels.GetMiddlewareValueVariableName(middleware, ++index)] = expression;
                        }
                    }
                })
                // Serve the Aspire developer certificate (or one set with WithHttpsCertificate) instead of Traefik's
                // self-signed default, so browsers trust https://*.dev.localhost. Aspire only invokes this callback
                // when a certificate is available, and WithoutHttpsCertificate turns it off. The file paths contain the
                // certificate's thumbprint and are only known here, so they go through the environment into
                // TraefikDynamicConfiguration.TlsFileContent instead of the generated routes file.
                .WithHttpsCertificateConfiguration(context =>
                {
                    // Traefik reads PEM files and can't decrypt a password-protected key, so a certificate with a
                    // password keeps Traefik's default certificate.
                    if (context.Password is null)
                    {
                        context.EnvironmentVariables[TraefikDynamicConfiguration.TlsCertificateVariable] = context.CertificatePath;
                        context.EnvironmentVariables[TraefikDynamicConfiguration.TlsKeyVariable] = context.KeyPath;
                    }

                    return Task.CompletedTask;
                })
                .WithContainerFiles(TraefikResource.DynamicConfigurationDirectory, async (_, cancellationToken) =>
                [
                    new ContainerFile
                    {
                        Name = TraefikDynamicConfiguration.FileName,
                        Contents = await TraefikDynamicConfiguration.GenerateAsync(resource, executionContext, cancellationToken).ConfigureAwait(false),
                    },
                    new ContainerFile
                    {
                        Name = TraefikDynamicConfiguration.TlsFileName,
                        Contents = TraefikDynamicConfiguration.TlsFileContent,
                    },
                ]);
        }

        return traefik;
    }

    /// <summary>
    /// Sets the host port of the <c>web</c> entry point instead of using a randomly assigned port in run mode or port 80
    /// at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="port">The host port, or <see langword="null"/> to use the default.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// A fixed port keeps public URLs stable between runs, which helps with sign-in redirect URIs. URLs built with
    /// <c>GetTraefikUrl</c> include the port either way.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHostPort(this IResourceBuilder<TraefikResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint(TraefikResource.WebEntryPointName, endpoint =>
        {
            endpoint.Port = port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? TraefikResource.WebTargetPort : null);
        });
    }

    /// <summary>
    /// Sets the host port of the <c>websecure</c> entry point instead of using a randomly assigned port in run mode or
    /// port 443 at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="port">The host port, or <see langword="null"/> to use the default.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The <c>websecure</c> entry point exists once TLS is configured, for example with <c>WithLetsEncrypt</c>. The port
    /// applies whether this method is called before or after that.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHostHttpsPort(this IResourceBuilder<TraefikResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HostHttpsPort = port;

        return builder.WithEndpoint(TraefikResource.WebSecureEntryPointName, endpoint =>
        {
            endpoint.Port = port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? TraefikResource.WebSecureTargetPort : null);
        }, createIfNotExists: false);
    }

    /// <summary>
    /// Adds a custom entry point (a port Traefik listens on), for example for TCP or UDP routes.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the entry point. The resource gets an endpoint with the same name.</param>
    /// <param name="targetPort">The port Traefik listens on inside the container.</param>
    /// <param name="port">The host port. When <see langword="null"/>, a random port is used in run mode and <paramref name="targetPort"/> at publish time.</param>
    /// <param name="scheme">
    /// The scheme of the endpoint: <c>tcp</c>, <c>http</c>, <c>https</c> for an entry point that terminates TLS, or
    /// <c>udp</c> for an entry point that receives UDP datagrams.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="targetPort"/> or <paramref name="port"/> isn't between 1 and 65535.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is reserved, invalid, or already used by an endpoint of the resource.</exception>
    /// <remarks>
    /// <para>
    /// A <c>udp</c> entry point only takes UDP routes (<c>WithTraefikUdpRoute</c>). In run mode its endpoint isn't
    /// proxied by Aspire, which proxies TCP only, so the host port goes straight to Traefik.
    /// </para>
    /// <para>
    /// An <c>https</c> entry point terminates TLS for every HTTP route on it, like <c>websecure</c>, and its routes get
    /// <c>https</c> URLs. In run mode it serves the Aspire developer certificate. At publish time it uses the default
    /// certificate resolver of <c>WithLetsEncrypt</c>, or Traefik's self-signed default certificate when there is none.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/"/>.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithEntryPoint(this IResourceBuilder<TraefikResource> builder, string name, int targetPort, int? port = null, string scheme = "tcp")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetPort, MaxPort);

        if (port is { } hostPort)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(hostPort, 1, nameof(port));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(hostPort, MaxPort, nameof(port));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        if (name is TraefikResource.WebEntryPointName or TraefikResource.WebSecureEntryPointName or TraefikResource.TraefikEntryPointName)
        {
            throw new ArgumentException($"The entry point name '{name}' is reserved by the Traefik integration.", nameof(name));
        }

        TraefikNames.ThrowIfInvalidName(name, "entry point name", nameof(name));

        // WithEndpoint would silently update an existing endpoint of that name, while Traefik would get a second
        // address flag for the entry point, so the two could disagree.
        if (builder.Resource.Annotations.OfType<EndpointAnnotation>().Any(e => StringComparers.EndpointAnnotationName.Equals(e.Name, name)))
        {
            throw new ArgumentException($"Traefik resource '{builder.Resource.Name}' already has an entry point or endpoint named '{name}'.", nameof(name));
        }

        if (scheme is not ("tcp" or "http" or "https" or "udp"))
        {
            throw new ArgumentException($"The scheme '{scheme}' is not supported. Use 'tcp', 'http', 'https', or 'udp'.", nameof(scheme));
        }

        var isUdp = scheme == "udp";
        builder.Resource.CustomEntryPoints.Add(new TraefikEntryPoint(name, targetPort, isUdp, IsTls: scheme == "https"));

        return builder.WithEndpoint(
            port: port ?? (builder.ApplicationBuilder.ExecutionContext.IsPublishMode ? targetPort : null),
            targetPort: targetPort,
            scheme: scheme,
            name: name,
            isExternal: true,
            isProxied: isUdp ? false : null,
            protocol: isUdp ? ProtocolType.Udp : null);
    }

    /// <summary>
    /// Applies middlewares to every router on an entry point, including routers that don't come from Aspire routes.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="entryPoint">The entry point, for example <c>websecure</c>, or one added with <see cref="WithEntryPoint"/>.</param>
    /// <param name="middlewares">The middlewares, in the order Traefik runs them.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Entry-point middlewares run before the middlewares of a route, which suits defaults for the whole proxy, such as
    /// security headers or a rate limit. Calling the method again for the same entry point appends to its middlewares.
    /// </para>
    /// <para>
    /// Traefik reads entry-point middlewares from its static configuration, so they are defined in Traefik's own file
    /// provider in both modes; at publish time that file is a Docker Compose config of the Traefik service. That isn't
    /// possible for a Traefik used with <c>PublishAsExternal</c>, which throws at publish time: configure the middlewares
    /// on the external Traefik instead.
    /// </para>
    /// <para>
    /// oauth2-proxy middlewares can't be used, because they would also intercept the sign-in callback. The <c>traefik</c>
    /// entry point can't be used either, because it serves the health check.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-middlewares"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com");
    ///
    /// traefik.WithEntryPointMiddleware(TraefikResource.WebSecureEntryPointName, traefik.AddSecurityHeaders("security-headers"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithEntryPointMiddleware(this IResourceBuilder<TraefikResource> builder, string entryPoint, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentNullException.ThrowIfNull(middlewares);

        if (entryPoint == TraefikResource.TraefikEntryPointName)
        {
            throw new ArgumentException($"The '{TraefikResource.TraefikEntryPointName}' entry point serves the health check and the run-mode dashboard, so it can't have middlewares.", nameof(entryPoint));
        }

        var resources = GetOwnMiddlewares(builder, middlewares, nameof(middlewares));

        if (resources.SelectMany(m => m.WithReferences()).FirstOrDefault(m => m.OAuth2Proxy is not null) is { } signIn)
        {
            throw new ArgumentException($"The oauth2-proxy middleware '{signIn.Name}' can't be an entry-point middleware, because it would also intercept the sign-in callback. Apply it to the routes with WithTraefikMiddleware.", nameof(middlewares));
        }

        if (!builder.Resource.EntryPointMiddlewares.TryGetValue(entryPoint, out var list))
        {
            list = [];
            builder.Resource.EntryPointMiddlewares[entryPoint] = list;
        }

        list.AddRange(resources.Where(m => !list.Contains(m)));
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder;
    }

    /// <summary>
    /// Sets the level of Traefik's own logs, which show up as the resource's console logs.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="level">The minimum level that is logged.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Traefik logs errors only by default. <see cref="TraefikLogLevel.Debug"/> shows the configuration Traefik receives
    /// from each provider, which helps when a route doesn't match. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithLogLevel(this IResourceBuilder<TraefikResource> builder, TraefikLogLevel level)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.LogLevel = level;

        return builder;
    }

    /// <summary>
    /// Enables Traefik's access logs, one line per request, which show up as the resource's console logs.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="configure">Sets the format and the filters of the access logs.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Request headers are dropped unless listed in <see cref="TraefikAccessLogOptions.KeepHeaders"/>, because they can
    /// carry credentials or personal data. Traces in the Aspire dashboard already show each request; access logs are
    /// mostly useful when published. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/#accesslogs"/>.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// traefik.WithAccessLogs(logs =>
    /// {
    ///     logs.Format = TraefikAccessLogFormat.Json;
    ///     logs.StatusCodes.Add("400-599");
    /// });
    /// </code>
    /// </example>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikResource> WithAccessLogs(this IResourceBuilder<TraefikResource> builder, Action<TraefikAccessLogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new TraefikAccessLogOptions();
        configure?.Invoke(options);

        foreach (var statusCode in options.StatusCodes)
        {
            if (statusCode is null || !StatusCodeRangeRegex().IsMatch(statusCode))
            {
                throw new ArgumentException($"The status code filter '{statusCode}' is invalid. Use a code or a range, for example '404' or '500-599'.", nameof(configure));
            }
        }

        foreach (var header in options.KeepHeaders)
        {
            // The header name is a segment of the option path, so it can't contain '.' or '='.
            if (header is null || !HeaderNameRegex().IsMatch(header))
            {
                throw new ArgumentException($"The header name '{header}' is invalid.", nameof(configure));
            }
        }

        if (options.MinDuration is { } minDuration)
        {
            TraefikNames.ToDuration(minDuration, nameof(configure));
        }

        builder.Resource.AccessLogs = options;

        return builder;
    }

    /// <summary>
    /// Reads the Docker API through a socket proxy at publish time, instead of mounting the Docker socket into Traefik.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="configure">Customizes the proxy container, for example to use an image from your own registry. Only called when publishing.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Access to the Docker socket is access to the host, even read-only, so an internet-facing proxy is better off
    /// without it. This adds a <see href="https://github.com/Tecnativa/docker-socket-proxy">docker-socket-proxy</see>
    /// service named <c>&lt;name&gt;-socket-proxy</c> that mounts the socket and allows only the endpoints the Docker
    /// provider uses (listing and inspecting containers, and events), and points Traefik's Docker provider at it.
    /// </para>
    /// <para>
    /// The image is a third-party one, pinned by digest so that it can't change without an update of this package. To
    /// use a mirror or another proxy with the same environment variables, such as
    /// <see href="https://github.com/linuxserver/docker-socket-proxy">linuxserver/socket-proxy</see>, replace the image
    /// in <paramref name="configure"/> with <c>WithImage</c> and <c>WithImageRegistry</c>. The digest only applies while
    /// the image is the default one; call <c>WithImageSHA256</c> to pin a replacement.
    /// </para>
    /// <para>
    /// The proxy listens without authentication on the application's network, where other services could read the
    /// container list too. It has no effect in run mode, where Traefik reads a file instead, or with
    /// <c>PublishAsExternal</c>, where Traefik isn't deployed.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// traefik.WithDockerSocketProxy(proxy => proxy
    ///     .WithImageRegistry("registry.example.com")
    ///     .WithImage("mirror/docker-socket-proxy", "v0.5.0"));
    /// </code>
    /// </example>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikResource> WithDockerSocketProxy(this IResourceBuilder<TraefikResource> builder, Action<IResourceBuilder<ContainerResource>>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        var resource = builder.Resource;
        if (resource.DockerSocketProxy is { } existing)
        {
            if (configure is not null)
            {
                configure(builder.ApplicationBuilder.CreateResourceBuilder(existing));
                PinDefaultSocketProxyImage(existing);
            }

            return builder;
        }

        var proxy = builder.ApplicationBuilder.AddContainer($"{resource.Name}-socket-proxy", TraefikContainerImageTags.SocketProxyImage, TraefikContainerImageTags.SocketProxyTag)
            .WithImageRegistry(TraefikContainerImageTags.SocketProxyRegistry)
            .WithBindMount(resource.DockerSocketHostPath, TraefikResource.DockerSocketPath, isReadOnly: true)
            // Only the container endpoints; EVENTS, PING, and VERSION are allowed by default, and POST requests are not.
            // https://github.com/Tecnativa/docker-socket-proxy#grant-or-revoke-access-to-certain-api-sections
            .WithEnvironment("CONTAINERS", "1")
            .WithEndpoint(targetPort: SocketProxyPort, scheme: "tcp", name: SocketProxyEndpointName);

        resource.DockerSocketProxy = proxy.Resource;
        configure?.Invoke(proxy);
        PinDefaultSocketProxyImage(proxy.Resource);

        foreach (var mount in resource.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Target == TraefikResource.DockerSocketPath).ToList())
        {
            resource.Annotations.Remove(mount);
        }

        builder.WaitFor(proxy);

        builder.ApplicationBuilder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            // PublishAsExternal may be called after this method. Without the Traefik service, the proxy has no client.
            if (resource.PublishExternal is not null)
            {
                proxy.ExcludeFromManifest();
            }

            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// Sets the path of the Docker socket on the deployment host, which Traefik's Docker provider (or the Docker socket
    /// proxy) mounts at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="hostPath">The path of the socket on the host, for example <c>/run/user/1000/docker.sock</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The default, <c>/var/run/docker.sock</c>, is the socket of a rootful Docker Engine. Rootless Docker listens on
    /// <c>$XDG_RUNTIME_DIR/docker.sock</c>, and Podman on <c>/run/podman/podman.sock</c> (rootful) or
    /// <c>/run/user/&lt;uid&gt;/podman/podman.sock</c> (rootless). The socket is always mounted at
    /// <c>/var/run/docker.sock</c> inside the container, where Traefik and the socket proxy expect it.
    /// </para>
    /// <para>
    /// It has no effect in run mode, where Traefik reads a file instead, or with <c>PublishAsExternal</c>, where Traefik
    /// isn't deployed.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithDockerSocketPath(this IResourceBuilder<TraefikResource> builder, string hostPath)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var resource = builder.Resource;
        resource.DockerSocketHostPath = hostPath;

        // AddTraefik mounts the socket into Traefik, and WithDockerSocketProxy moves it to the proxy. Both may run
        // before this method, so the existing mount is replaced.
        IResource container = (IResource?)resource.DockerSocketProxy ?? resource;
        foreach (var mount in container.Annotations.OfType<ContainerMountAnnotation>().Where(m => m.Target == TraefikResource.DockerSocketPath).ToList())
        {
            container.Annotations.Remove(mount);
            container.Annotations.Add(new ContainerMountAnnotation(hostPath, TraefikResource.DockerSocketPath, ContainerMountType.BindMount, isReadOnly: true));
        }

        return builder;
    }

    /// <summary>
    /// Uses a Traefik that already runs on the deployment target instead of deploying this container at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="network">The Docker network the external Traefik is attached to. Routed services join it, and it is declared as an external network.</param>
    /// <param name="entryPoint">The entry point of the external Traefik that routes use.</param>
    /// <param name="certResolver">The certificate resolver defined on the external Traefik that routes use, if any.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Run mode is unchanged: Aspire still runs the Traefik container. At publish time the container is left out, and
    /// the routed services get labels for the external Traefik, which must use the Docker provider and be attached to
    /// <paramref name="network"/>.
    /// </para>
    /// <para>
    /// Public URLs use HTTPS unless <paramref name="entryPoint"/> is the literal <c>web</c> and no resolver is set; a
    /// parameterized entry point is assumed to be HTTPS. Describe entry points that don't follow that convention, or
    /// that listen on another port than 80 or 443, with <c>WithExternalEntryPoint</c>. When <paramref name="certResolver"/>
    /// is not set, the entry point is expected to configure TLS itself (<c>entrypoints.&lt;name&gt;.http.tls</c>).
    /// </para>
    /// <para>
    /// The entry point and resolver only appear in <c>traefik.*</c> label values, so they can be parameters, which
    /// become <c>${VAR}</c> placeholders in the generated <c>.env</c> file; one AppHost can then target servers whose
    /// Traefik uses different names. The network name can't be a parameter: Docker Compose doesn't interpolate the YAML
    /// keys it is written to.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .PublishAsExternal(network: "traefik", entryPoint: "websecure", certResolver: "letsencrypt");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal publishAsExternal export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> PublishAsExternal(this IResourceBuilder<TraefikResource> builder, string network, string entryPoint = TraefikResource.WebSecureEntryPointName, string? certResolver = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        TraefikNames.ThrowIfInvalidName(entryPoint, "entry point name", nameof(entryPoint));

        if (certResolver is not null)
        {
            TraefikNames.ThrowIfInvalidName(certResolver, "certificate resolver name", nameof(certResolver));
        }

        return builder.PublishAsExternal(
            network,
            ReferenceExpression.Create($"{entryPoint}"),
            certResolver is null ? null : ReferenceExpression.Create($"{certResolver}"));
    }

    /// <inheritdoc cref="PublishAsExternal(IResourceBuilder{TraefikResource}, string, string, string?)"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="network">The Docker network the external Traefik is attached to. Routed services join it, and it is declared as an external network.</param>
    /// <param name="entryPoint">The entry point of the external Traefik that routes use. It can reference parameters.</param>
    /// <param name="certResolver">The certificate resolver defined on the external Traefik that routes use, if any. It can reference parameters.</param>
    /// <example>
    /// <code lang="csharp">
    /// var entryPoint = builder.AddParameter("traefik-entrypoint");
    /// var certResolver = builder.AddParameter("traefik-certresolver");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .PublishAsExternal(
    ///         network: "traefik",
    ///         entryPoint: ReferenceExpression.Create($"{entryPoint}"),
    ///         certResolver: ReferenceExpression.Create($"{certResolver}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal publishAsExternal export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> PublishAsExternal(this IResourceBuilder<TraefikResource> builder, string network, ReferenceExpression entryPoint, ReferenceExpression? certResolver = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(network);
        ArgumentNullException.ThrowIfNull(entryPoint);

        var alreadyExternal = builder.Resource.PublishExternal is not null;
        builder.Resource.PublishExternal = new TraefikExternalSettings(network, entryPoint, certResolver);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode || alreadyExternal)
        {
            return builder;
        }

        builder.ExcludeFromManifest();

        var applicationBuilder = builder.ApplicationBuilder;
        var resource = builder.Resource;
        applicationBuilder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            // Compose environments are usually added after Traefik, so they are looked up once the model is
            // complete. The network is read when the compose file is written, so the last PublishAsExternal wins.
            foreach (var environment in @event.Model.Resources.OfType<DockerComposeEnvironmentResource>())
            {
                applicationBuilder.CreateResourceBuilder(environment).ConfigureComposeFile(file =>
                {
                    if (resource.PublishExternal is { } external)
                    {
                        file.AddNetwork(new Network { Name = external.Network, External = true });
                    }
                });
            }

            return Task.CompletedTask;
        });

        return builder;
    }

    /// <summary>
    /// Describes an entry point of the external Traefik used with <c>PublishAsExternal</c>, so that the public URLs and
    /// the oauth2-proxy cookie of routes on it use the right scheme and port.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the entry point on the external Traefik.</param>
    /// <param name="isTls">Whether clients reach the entry point over TLS, which makes the URLs of its routes <c>https</c>.</param>
    /// <param name="port">The public port clients connect to, or <see langword="null"/> for the standard port of the scheme (80 or 443).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't between 1 and 65535.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> isn't a valid entry point name.</exception>
    /// <remarks>
    /// <para>
    /// The external Traefik's configuration isn't visible to Aspire. Without a description, an entry point named
    /// <c>web</c> is assumed to be plain HTTP and any other one HTTPS, both on the standard port. Describe the entry
    /// points that don't follow that convention, for example <c>http</c> and <c>https</c>, or one on port 8443.
    /// Describing an entry point again replaces the description.
    /// </para>
    /// <para>
    /// The description only applies at publish time while the resource is published as external. It doesn't
    /// configure the external Traefik, and it has no effect in run mode, where Aspire runs its own Traefik.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .PublishAsExternal(network: "proxy", entryPoint: "https")
    ///     .WithExternalEntryPoint("http", isTls: false)
    ///     .WithExternalEntryPoint("https", isTls: true, port: 8443);
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithExternalEntryPoint(this IResourceBuilder<TraefikResource> builder, string name, bool isTls, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TraefikNames.ThrowIfInvalidName(name, "entry point name", nameof(name));

        if (port is { } publicPort)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(publicPort, 1, nameof(port));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(publicPort, MaxPort, nameof(port));
        }

        var entryPoints = builder.Resource.ExternalEntryPoints;
        entryPoints.RemoveAll(e => e.Name == name);
        entryPoints.Add(new TraefikExternalEntryPoint(name, isTls, port));

        return builder;
    }

    /// <summary>
    /// Gets the resources of middlewares passed to a method of <paramref name="builder"/>, which must belong to it.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="middlewares">The middlewares.</param>
    /// <param name="parameterName">The parameter the middlewares were passed in, for the exceptions.</param>
    /// <returns>The middleware resources, in the given order.</returns>
    internal static List<TraefikMiddlewareResource> GetOwnMiddlewares(IResourceBuilder<TraefikResource> builder, IResourceBuilder<TraefikMiddlewareResource>[] middlewares, string parameterName)
    {
        var resources = new List<TraefikMiddlewareResource>(middlewares.Length);
        foreach (var middleware in middlewares)
        {
            ArgumentNullException.ThrowIfNull(middleware, parameterName);

            if (middleware.Resource.Parent != builder.Resource)
            {
                throw new ArgumentException($"The Traefik middleware '{middleware.Resource.Name}' belongs to '{middleware.Resource.Parent.Name}', not '{builder.Resource.Name}'.", parameterName);
            }

            resources.Add(middleware.Resource);
        }

        return resources;
    }

    /// <summary>
    /// Uses a Traefik that already runs on the deployment target instead of deploying this container at publish time.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="network">The Docker network the external Traefik is attached to. Routed services join it, and it is declared as an external network.</param>
    /// <param name="entryPoint">The entry point of the external Traefik that routes use, as a string or a reference expression. Defaults to <c>websecure</c>.</param>
    /// <param name="certResolver">The certificate resolver defined on the external Traefik that routes use, if any, as a string or a reference expression.</param>
    /// <returns>A reference to the Traefik resource builder.</returns>
    /// <remarks>
    /// Run mode is unchanged: Aspire still runs the Traefik container. At publish time the container is left out, and the
    /// routed services get labels for the external Traefik, which must use the Docker provider and be attached to the network.
    /// </remarks>
    [AspireExport("publishAsExternal")]
    internal static IResourceBuilder<TraefikResource> PublishAsExternalPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        string network,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object? entryPoint = null,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object? certResolver = null)
    {
        // A union parameter can't have a string default, so null stands for the `websecure` default of the C# API.
        return builder.PublishAsExternal(
            network,
            ToExpression(entryPoint, nameof(entryPoint)) ?? ReferenceExpression.Create($"{TraefikResource.WebSecureEntryPointName}"),
            ToExpression(certResolver, nameof(certResolver)));

        static ReferenceExpression? ToExpression(object? value, string parameterName) => value switch
        {
            null => null,
            string text => ReferenceExpression.Create($"{text}"),
            ReferenceExpression expression => expression,
            _ => throw new ArgumentException($"Unsupported type '{value.GetType().Name}'. Expected a string or a reference expression.", parameterName),
        };
    }

    /// <summary>
    /// Marks the port mappings of UDP entry points as UDP in the Docker Compose service of Traefik.
    /// </summary>
    /// <remarks>
    /// The Docker Compose publisher writes every mapping without a protocol, such as <c>5353:5353</c>, which Docker reads
    /// as TCP, so a UDP entry point would never receive a datagram. The UDP form is <c>5353:5353/udp</c>.
    /// See <see href="https://docs.docker.com/reference/compose-file/services/#ports"/>.
    /// </remarks>
    private static void SetUdpPortProtocols(TraefikResource traefik, Service service)
    {
        foreach (var entryPoint in traefik.CustomEntryPoints.Where(e => e.IsUdp))
        {
            var target = entryPoint.TargetPort.ToString(CultureInfo.InvariantCulture);

            // The publisher writes one string for equal mappings, so a TCP entry point on the same container port shares
            // it. Such a mapping is kept for TCP and the UDP one is added next to it.
            var sharedWithTcp = entryPoint.TargetPort is TraefikResource.WebTargetPort or TraefikResource.WebSecureTargetPort or TraefikResource.TraefikTargetPort
                || traefik.CustomEntryPoints.Any(e => !e.IsUdp && e.TargetPort == entryPoint.TargetPort);

            for (var i = 0; i < service.Ports.Count; i++)
            {
                var mapping = service.Ports[i];
                if (mapping != target && !mapping.EndsWith($":{target}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (sharedWithTcp)
                {
                    service.Ports.Insert(++i, $"{mapping}/udp");
                }
                else
                {
                    service.Ports[i] = $"{mapping}/udp";
                }
            }
        }
    }

    private static void PinDefaultSocketProxyImage(ContainerResource proxy)
    {
        // Pin only the image this package chose. WithImage keeps an existing digest, so pinning before `configure` would
        // pin a mirror or replacement image to the digest of a different image.
        if (proxy.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault() is { } image &&
            image.Registry == TraefikContainerImageTags.SocketProxyRegistry &&
            image.Image == TraefikContainerImageTags.SocketProxyImage &&
            image.Tag == TraefikContainerImageTags.SocketProxyTag)
        {
            image.SHA256 ??= TraefikContainerImageTags.SocketProxySha256;
        }
    }

    // A status code or a range, for example `404` or `500-599`, as Traefik's access log filter reads them.
    [GeneratedRegex("^[1-5][0-9]{2}(-[1-5][0-9]{2})?$")]
    private static partial Regex StatusCodeRangeRegex();

    // HTTP header field names are tokens (RFC 9110, section 5.1), without '.', which separates option path segments.
    [GeneratedRegex("^[A-Za-z0-9!#$%&'*+^_`|~-]+$")]
    private static partial Regex HeaderNameRegex();
}
