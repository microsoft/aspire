// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for routing traffic from Traefik to resources.
/// </summary>
public static class TraefikRoutingExtensions
{
    /// <summary>
    /// Routes HTTP requests for <paramref name="host"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="host">The host name to match, for example <c>api.dev.localhost</c>.</param>
    /// <param name="middlewares">Middlewares that apply to this route only, in the order Traefik runs them.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Requests go to the resource's <c>http</c> endpoint. Use the overload that takes a configuration callback to
    /// choose another endpoint, match a path prefix, or name the router. A resource can have several routes.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik");
    ///
    /// builder.AddContainer("whoami", "traefik/whoami")
    ///     .WithHttpEndpoint(targetPort: 80)
    ///     .WithTraefikRoute(traefik, "whoami.dev.localhost");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTraefikRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, string host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return builder.WithTraefikRoute(traefik, ReferenceExpression.Create($"{host}"), middlewares);
    }

    /// <inheritdoc cref="WithTraefikRoute{T}(IResourceBuilder{T}, IResourceBuilder{TraefikResource}, string, IResourceBuilder{TraefikMiddlewareResource}[])"/>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="host">The host name to match. It can reference parameters, which stay placeholders at publish time.</param>
    /// <param name="middlewares">Middlewares that apply to this route only, in the order Traefik runs them.</param>
    /// <example>
    /// <code lang="csharp">
    /// var domain = builder.AddParameter("domain");
    /// var traefik = builder.AddTraefik("traefik");
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, ReferenceExpression.Create($"api.{domain}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTraefikRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, ReferenceExpression host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(middlewares);

        foreach (var middleware in middlewares)
        {
            ArgumentNullException.ThrowIfNull(middleware, nameof(middlewares));
        }

        return builder.WithTraefikRoute(traefik, options =>
        {
            options.Host = host;

            foreach (var middleware in middlewares)
            {
                options.Middlewares.Add(middleware.Resource);
            }
        });
    }

    /// <summary>
    /// Routes HTTP requests through <paramref name="traefik"/> to this resource, with the route described by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="configure">Configures the route. At least one matcher, such as a host or a path prefix, is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithTraefikRoute(traefik, route =>
    ///     {
    ///         route.Host = ReferenceExpression.Create($"app.dev.localhost");
    ///         route.PathPrefix = "/api";
    ///         route.RouterName = "app-api";
    ///     });
    /// </code>
    /// </example>
    [AspireExport("withTraefikRouteOptions", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithTraefikRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, Action<TraefikRouteOptions> configure)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TraefikRouteOptions();
        configure(options);

        if (options.StripPathPrefix && string.IsNullOrEmpty(options.PathPrefix))
        {
            throw new ArgumentException("StripPathPrefix needs a path prefix to strip.", nameof(configure));
        }

        ValidateNames(options.EntryPoint, options.CertResolver, nameof(configure));

        ValidateServiceOptions(options, nameof(configure));
        ValidateRule(() => TraefikRouteRule.Build(options), nameof(configure));

        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder.WithAnnotation(new TraefikRouteAnnotation(traefik.Resource, options, isSignInRoute: false), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Routes every TCP connection on <paramref name="entryPoint"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the connections.</param>
    /// <param name="entryPoint">The entry point, usually one added for this route with <c>WithEntryPoint</c>.</param>
    /// <param name="endpointName">The endpoint that receives the connections. Defaults to <c>tcp</c>, or to the resource's only endpoint.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Plain TCP carries no host name, so the route takes the whole entry point. Use the overload that takes a
    /// configuration callback to route by server name (SNI) over TLS.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithEntryPoint("postgres", targetPort: 5432);
    ///
    /// builder.AddPostgres("db")
    ///     .WithTraefikTcpRoute(traefik, "postgres");
    /// </code>
    /// </example>
    [AspireExport("withTraefikTcpRouteOnEntryPoint")]
    public static IResourceBuilder<T> WithTraefikTcpRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, string entryPoint, string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);

        return builder.WithTraefikTcpRoute(traefik, options =>
        {
            options.EntryPoint = entryPoint;
            options.EndpointName = endpointName;
        });
    }

    /// <summary>
    /// Routes TCP connections through <paramref name="traefik"/> to this resource, with the route described by
    /// <paramref name="configure"/>.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the connections.</param>
    /// <param name="configure">Configures the route. An entry point is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <example>
    /// Terminate TLS for <c>amqps://amqp.example.com:5671</c> and forward plain AMQP:
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com")
    ///     .WithEntryPoint("amqps", targetPort: 5671);
    ///
    /// builder.AddRabbitMQ("messaging")
    ///     .WithTraefikTcpRoute(traefik, route =>
    ///     {
    ///         route.EntryPoint = "amqps";
    ///         route.HostSni = ReferenceExpression.Create($"amqp.example.com");
    ///         route.Tls = true;
    ///     });
    /// </code>
    /// </example>
    [AspireExport("withTraefikTcpRouteOptions", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<T> WithTraefikTcpRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, Action<TraefikTcpRouteOptions> configure)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TraefikTcpRouteOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.EntryPoint))
        {
            throw new ArgumentException("A Traefik TCP route needs an entry point.", nameof(configure));
        }

        ValidateNames(options.EntryPoint, options.CertResolver, nameof(configure));

        if (options.Tls && options.TlsPassthrough)
        {
            throw new ArgumentException($"A Traefik TCP route either terminates TLS ({nameof(TraefikTcpRouteOptions.Tls)}) or passes it through ({nameof(TraefikTcpRouteOptions.TlsPassthrough)}), not both.", nameof(configure));
        }

        if (options.HostSni is not null && !options.Tls && !options.TlsPassthrough)
        {
            // Traefik itself rejects such a router: without TLS there is no server name to match.
            // https://doc.traefik.io/traefik/reference/routing-configuration/tcp/routing/rules-and-priority/
            throw new ArgumentException($"A Traefik TCP route can only match a server name over TLS. Set {nameof(TraefikTcpRouteOptions.Tls)} or {nameof(TraefikTcpRouteOptions.TlsPassthrough)}, or remove {nameof(TraefikTcpRouteOptions.HostSni)}.", nameof(configure));
        }

        if (options.CertResolver is not null && !options.Tls)
        {
            throw new ArgumentException($"A certificate resolver needs {nameof(TraefikTcpRouteOptions.Tls)}: with TLS passthrough or plain TCP, Traefik presents no certificate.", nameof(configure));
        }

        ValidateRule(() => TraefikRouteRule.Build(options), nameof(configure));

        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder.WithAnnotation(new TraefikTcpRouteAnnotation(traefik.Resource, options), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Routes every UDP datagram on <paramref name="entryPoint"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the datagrams.</param>
    /// <param name="entryPoint">A UDP entry point, added with <c>WithEntryPoint(name, targetPort, scheme: "udp")</c>.</param>
    /// <param name="endpointName">The UDP endpoint that receives the datagrams. Defaults to <c>udp</c>, or to the resource's only endpoint.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// UDP routers have no rule, so the route takes the whole entry point. The resource's endpoint must use the UDP
    /// protocol. See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/udp/routing/router/"/>.
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithEntryPoint("dns", targetPort: 53, scheme: "udp");
    ///
    /// builder.AddContainer("dns", "coredns/coredns")
    ///     .WithEndpoint(targetPort: 53, name: "udp", protocol: ProtocolType.Udp)
    ///     .WithTraefikUdpRoute(traefik, "dns");
    /// </code>
    /// </example>
    [AspireExport("withTraefikUdpRouteOnEntryPoint")]
    public static IResourceBuilder<T> WithTraefikUdpRoute<T>(this IResourceBuilder<T> builder, IResourceBuilder<TraefikResource> traefik, string entryPoint, string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(traefik);
        TraefikNames.ThrowIfInvalidName(entryPoint, "entry point name", nameof(entryPoint));

        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();

        return builder.WithAnnotation(new TraefikUdpRouteAnnotation(traefik.Resource, entryPoint, endpointName), ResourceAnnotationMutationBehavior.Append);
    }

    /// <summary>
    /// Gets the public URL of this resource through Traefik, from its first Traefik route.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <returns>
    /// An expression such as <c>http://api.dev.localhost:52314</c> in run mode, which includes the entry point's host
    /// port, or <c>https://api.example.com</c> at publish time.
    /// </returns>
    /// <exception cref="InvalidOperationException">The resource has no Traefik route, or its first route has no host.</exception>
    /// <remarks>
    /// <para>
    /// Use the URL wherever the public address is needed, for example as an OAuth redirect URI or a public base URL
    /// in another resource's environment.
    /// </para>
    /// <para>
    /// The scheme and the run-mode port follow Traefik's final configuration, such as <c>WithLetsEncrypt</c> and
    /// <c>PublishAsExternal</c>, whether those are called before or after this method.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static ReferenceExpression GetTraefikUrl<T>(this IResourceBuilder<T> builder)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        var annotation = builder.Resource.Annotations.OfType<TraefikRouteAnnotation>().FirstOrDefault()
            ?? throw new InvalidOperationException($"Resource '{builder.Resource.Name}' has no Traefik route. Call WithTraefikRoute first.");

        return TraefikUrls.Build(annotation.Traefik, annotation.Options, builder.ApplicationBuilder.ExecutionContext.IsPublishMode);
    }

    /// <summary>
    /// Gets the public URL of this resource through Traefik, from the route with the given router name.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="routerName">The <see cref="TraefikRouteOptions.RouterName"/> the route was given.</param>
    /// <returns>
    /// An expression such as <c>https://secure.dev.localhost:8443</c> in run mode, or <c>https://secure.example.com</c>
    /// at publish time. The route's path, if any, is appended.
    /// </returns>
    /// <exception cref="InvalidOperationException">The resource has no route with that router name, or the route has no host.</exception>
    /// <remarks>
    /// Use this overload when a resource has several routes, for example on different hosts or entry points, and each
    /// needs its own public address, such as one OAuth redirect URI per protected host. Only router names set with
    /// <see cref="TraefikRouteOptions.RouterName"/> are matched: default names are only assigned once the model is
    /// complete.
    /// </remarks>
    [AspireExport("getTraefikUrlForRouter")]
    public static ReferenceExpression GetTraefikUrl<T>(this IResourceBuilder<T> builder, string routerName)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(routerName);

        var annotation = builder.Resource.Annotations.OfType<TraefikRouteAnnotation>()
            .FirstOrDefault(a => string.Equals(a.Options.RouterName, routerName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Resource '{builder.Resource.Name}' has no Traefik route with the router name '{routerName}'. Set {nameof(TraefikRouteOptions)}.{nameof(TraefikRouteOptions.RouterName)} on the route.");

        return TraefikUrls.Build(annotation.Traefik, annotation.Options, builder.ApplicationBuilder.ExecutionContext.IsPublishMode);
    }

    /// <summary>
    /// Routes HTTP requests for <paramref name="host"/> through <paramref name="traefik"/> to this resource.
    /// </summary>
    /// <typeparam name="T">The type of the routed resource.</typeparam>
    /// <param name="builder">The builder of the routed resource.</param>
    /// <param name="traefik">The Traefik resource that receives the requests.</param>
    /// <param name="host">The host name to match, for example <c>api.dev.localhost</c>. It can reference parameters.</param>
    /// <param name="middlewares">Middlewares that apply to this route only, in the order Traefik runs them.</param>
    /// <returns>A reference to the routed resource builder.</returns>
    /// <remarks>
    /// Requests go to the resource's <c>http</c> endpoint. Use <c>withTraefikRouteOptions</c> to choose another endpoint,
    /// match a path prefix, or name the router. A resource can have several routes.
    /// </remarks>
    [AspireExport("withTraefikRoute")]
    internal static IResourceBuilder<T> WithTraefikRoutePolyglot<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<TraefikResource> traefik,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object host,
        IResourceBuilder<TraefikMiddlewareResource>[]? middlewares = null)
        where T : IResourceWithEndpoints
    {
        middlewares ??= [];

        return host switch
        {
            string value => builder.WithTraefikRoute(traefik, value, middlewares),
            ReferenceExpression expression => builder.WithTraefikRoute(traefik, expression, middlewares),
            _ => throw new ArgumentException($"Unsupported type '{host?.GetType().Name}'. Expected a string or a reference expression.", nameof(host)),
        };
    }

    private static void ValidateNames(string? entryPoint, string? certResolver, string parameterName)
    {
        // Both are written into labels and configuration files as they are, so they must be plain names: a `$` would
        // be interpolated by Docker Compose, and Traefik's own names are letters, digits, '-' and '_'. Checking here also
        // covers an external Traefik, whose entry points and resolvers are otherwise never checked.
        if (entryPoint is not null)
        {
            TraefikNames.ThrowIfInvalidName(entryPoint, "entry point name", parameterName);
        }

        if (certResolver is not null)
        {
            TraefikNames.ThrowIfInvalidName(certResolver, "certificate resolver name", parameterName);
        }
    }

    private static void ValidateServiceOptions(TraefikRouteOptions options, string parameterName)
    {
        if (options.HealthCheck is { } healthCheck)
        {
            TraefikNames.ThrowIfInvalidPath(healthCheck.Path, "health check path", parameterName);

            if (healthCheck.IntervalSeconds <= 0 || healthCheck.TimeoutSeconds <= 0)
            {
                throw new ArgumentException("The health check interval and timeout must be positive.", parameterName);
            }

            if (healthCheck.Status is < 100 or > 599)
            {
                throw new ArgumentException($"The health check status {healthCheck.Status} isn't an HTTP status code.", parameterName);
            }
        }

        if (options.StickySession is { } sticky)
        {
            // A cookie name is an RFC 6265 token: no spaces, separators, or `=`.
            // https://www.rfc-editor.org/rfc/rfc6265#section-4.1.1
            if (sticky.CookieName is { } name && (name.Length == 0 || name.Any(c => c <= ' ' || c >= 127 || "()<>@,;:\\\"/[]?={}".Contains(c))))
            {
                throw new ArgumentException($"The sticky session cookie name '{name}' is invalid.", parameterName);
            }

            if (sticky.MaxAgeSeconds <= 0)
            {
                throw new ArgumentException("The sticky session cookie max age must be positive.", parameterName);
            }
        }
    }

    private static void ValidateRule(Func<ReferenceExpression> build, string parameterName)
    {
        try
        {
            // Build the rule once now so invalid values fail where the route is declared, not at startup.
            build();
        }
        catch (ArgumentException ex)
        {
            // The matchers name their own arguments, which the caller didn't pass; the options came from the callback.
            // ArgumentException.Message ends with " (Parameter '<name>')" when ParamName is set, so that is cut off
            // before the callback's parameter is added.
            var message = ex.ParamName is null ? ex.Message : ex.Message.Replace($" (Parameter '{ex.ParamName}')", "", StringComparison.Ordinal);
            throw new ArgumentException(message, parameterName, ex);
        }
    }
}
