// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

public static partial class TraefikBuilderExtensions
{
    /// <summary>
    /// Enables the Traefik dashboard.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// In run mode the dashboard is served without authentication on the <c>traefik</c> endpoint, and the resource
    /// shows a link to it. At publish time the dashboard is enabled but not exposed, because an unauthenticated
    /// dashboard must not be reachable from the internet. To expose it behind authentication, use the overload that
    /// takes a host and middlewares.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The routed overload calls this too, so the URL callback must only be added once; each callback appends the
        // path again, which turned the link into /dashboard/dashboard/.
        if (builder.Resource.DashboardEnabled)
        {
            return builder;
        }

        builder.Resource.DashboardEnabled = true;

        return builder.WithUrlForEndpoint(TraefikResource.TraefikEntryPointName, url =>
        {
            url.Url = $"{url.Url.TrimEnd('/')}/dashboard/";
            url.DisplayText = "Dashboard";
        });
    }

    /// <summary>
    /// Enables the Traefik dashboard and routes it on a host, behind middlewares such as authentication.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="host">The host the dashboard is served on, for example <c>traefik.example.com</c>.</param>
    /// <param name="middlewares">The middlewares that protect the dashboard, in the order Traefik runs them. At least one is required.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="middlewares"/> is empty, or a middleware belongs to another Traefik.</exception>
    /// <remarks>
    /// <para>
    /// The dashboard router uses the default entry point of the routes, with TLS when <c>WithLetsEncrypt</c> is used, and
    /// is served at <c>/dashboard/</c> on the host. It is written to Traefik's own dynamic configuration in both modes,
    /// so it is published as a Docker Compose config of the Traefik service. In run mode the unauthenticated dashboard
    /// on the <c>traefik</c> endpoint stays available too.
    /// </para>
    /// <para>
    /// The middlewares must belong to this Traefik. Single sign-on with <c>AddOAuth2ProxyForwardAuth</c> works: the host
    /// gets a sign-in route like any protected route. With <c>PublishAsExternal</c>, the dashboard of the external Traefik
    /// is its own, so nothing is published for it.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/api-dashboard/"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var htpasswd = builder.AddParameter("dashboard-htpasswd", secret: true);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com");
    ///
    /// var auth = traefik.AddBasicAuth("dashboard-auth", htpasswd);
    /// traefik.WithDashboard("traefik.example.com", auth);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withDashboardRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder, string host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return builder.WithDashboard(ReferenceExpression.Create($"{host}"), middlewares);
    }

    /// <inheritdoc cref="WithDashboard(IResourceBuilder{TraefikResource}, string, IResourceBuilder{TraefikMiddlewareResource}[])"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="host">
    /// The host the dashboard is served on. It can reference parameters, for example
    /// <c>ReferenceExpression.Create($"traefik.{domain}")</c>, which stay placeholders at publish time.
    /// </param>
    /// <param name="middlewares">The middlewares that protect the dashboard, in the order Traefik runs them. At least one is required.</param>
    /// <exception cref="ArgumentException"><paramref name="middlewares"/> is empty, or a middleware belongs to another Traefik.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withDashboardRoute export, which accepts a string or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithDashboard(this IResourceBuilder<TraefikResource> builder, ReferenceExpression host, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(middlewares);

        if (middlewares.Length == 0)
        {
            // A dashboard on a public host without authentication exposes the whole routing configuration.
            throw new ArgumentException("A routed dashboard needs at least one middleware, such as authentication.", nameof(middlewares));
        }

        var resources = GetOwnMiddlewares(builder, middlewares, nameof(middlewares));
        var isFirstCall = builder.Resource.DashboardHost is null;

        builder.Resource.DashboardHost = host;
        builder.Resource.DashboardMiddlewares.Clear();
        builder.Resource.DashboardMiddlewares.AddRange(resources);
        builder.ApplicationBuilder.Services.TryAddEventingSubscriber<TraefikRoutingSubscriber>();
        builder.WithDashboard();

        if (isFirstCall && !builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            var resource = builder.Resource;
            builder.WithUrls(async context =>
            {
                if (resource.DashboardHost is not { } dashboardHost)
                {
                    return;
                }

                var url = TraefikUrls.Build(resource, new TraefikRouteOptions { Host = dashboardHost, PathPrefix = "/dashboard/" }, isPublishMode: false);
                if (await url.GetValueAsync(context.CancellationToken).ConfigureAwait(false) is { Length: > 0 } value)
                {
                    context.Urls.Add(new ResourceUrlAnnotation { Url = value, DisplayText = "Dashboard (routed)" });
                }
            });
        }

        return builder;
    }

    // Polyglot exports keep their own docs: the ATS capability scanner reads the XML documentation file as written,
    // without resolving <inheritdoc>.
    /// <summary>
    /// Enables the Traefik dashboard and routes it on a host, behind middlewares such as authentication.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="host">The host the dashboard is served on, for example <c>traefik.example.com</c>. It can reference parameters.</param>
    /// <param name="middlewares">The middlewares that protect the dashboard, in the order Traefik runs them. At least one is required.</param>
    /// <returns>A reference to the Traefik resource builder.</returns>
    /// <remarks>
    /// The dashboard is served at <c>/dashboard/</c> on the host, on the default entry point of the routes.
    /// </remarks>
    [AspireExport("withDashboardRoute")]
    internal static IResourceBuilder<TraefikResource> WithDashboardPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object host,
        IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        return host switch
        {
            string text => builder.WithDashboard(text, middlewares),
            ReferenceExpression expression => builder.WithDashboard(expression, middlewares),
            _ => throw new ArgumentException($"Unsupported type '{host?.GetType().Name}'. Expected a string or a reference expression.", nameof(host)),
        };
    }
}
