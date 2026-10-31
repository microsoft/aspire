// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for protecting Traefik routes with forward authentication.
/// </summary>
public static class TraefikForwardAuthExtensions
{
    /// <summary>
    /// The priority of the sign-in routes: above the default (the rule length) of any realistic rule, so
    /// <c>/oauth2/*</c> (or another <see cref="TraefikOAuth2ProxyOptions.ProxyPrefix"/>) on a protected host always
    /// reaches oauth2-proxy rather than the protected backend.
    /// </summary>
    internal const int SignInRoutePriority = 1_000_000;

    private const int OAuth2ProxyPort = 4180;

    private const string OAuth2ProxyEndpointName = "http";

    /// <summary>
    /// Adds a <c>forwardAuth</c> middleware that asks an authentication server whether a request may pass.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="endpoint">The endpoint of an authentication server running in the application.</param>
    /// <param name="path">The path of the authentication request on that endpoint, for example <c>/verify</c>.</param>
    /// <param name="configure">Sets more options, such as the response headers copied to the forwarded request.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> doesn't start with <c>/</c>.</exception>
    /// <remarks>
    /// <para>
    /// Traefik sends a request to the authentication server for every request on the routes that use the middleware,
    /// with the original method, URL, and host in <c>X-Forwarded-*</c> headers. A <c>2xx</c> response lets the request
    /// through; any other response, for example a redirect to a sign-in page, is returned to the client.
    /// </para>
    /// <para>
    /// The address is resolved from Traefik's point of view: in run mode over the container network, also for projects
    /// running on the host; when published to Docker Compose as the service address. With an external Traefik the
    /// authentication server joins its shared network. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/forwardauth/"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var auth = builder.AddProject&lt;Projects.AuthServer&gt;("auth");
    ///
    /// var forwardAuth = traefik.AddForwardAuth("auth-check", auth.GetEndpoint("http"), "/verify",
    ///     options => options.AuthResponseHeaders.Add("X-User"));
    ///
    /// api.WithTraefikMiddleware(forwardAuth);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addForwardAuth export, which accepts an endpoint, a string, or a reference expression.")]
    public static IResourceBuilder<TraefikMiddlewareResource> AddForwardAuth(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, EndpointReference endpoint, string path = "/", Action<TraefikForwardAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        TraefikNames.ThrowIfInvalidPath(path, "path", nameof(path));

        return builder.AddForwardAuth(name, ReferenceExpression.Create($"{endpoint}{path}"), configure)
            .WithRelationship(endpoint.Resource, "ForwardAuth");
    }

    /// <inheritdoc cref="AddForwardAuth(IResourceBuilder{TraefikResource}, string, EndpointReference, string, Action{TraefikForwardAuthOptions}?)"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="address">
    /// The URL of the authentication request. It can reference parameters and endpoints, for example
    /// <c>ReferenceExpression.Create($"{auth.GetEndpoint("http")}/verify?realm={realm}")</c>.
    /// </param>
    /// <param name="configure">Sets more options, such as the response headers copied to the forwarded request.</param>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addForwardAuth export, which accepts an endpoint, a string, or a reference expression.")]
    public static IResourceBuilder<TraefikMiddlewareResource> AddForwardAuth(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, ReferenceExpression address, Action<TraefikForwardAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(address);

        var options = new TraefikForwardAuthOptions();
        configure?.Invoke(options);

        var middleware = builder.AddMiddleware(name, "forwardAuth")
            .WithOption("address", address);
        options.ApplyTo(middleware.Resource.Options);

        return middleware;
    }

    /// <inheritdoc cref="AddForwardAuth(IResourceBuilder{TraefikResource}, string, EndpointReference, string, Action{TraefikForwardAuthOptions}?)"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="address">
    /// The absolute URL of an authentication server outside the application, for example <c>https://auth.example.com/verify</c>.
    /// </param>
    /// <param name="configure">Sets more options, such as the response headers copied to the forwarded request.</param>
    /// <exception cref="ArgumentException"><paramref name="address"/> isn't an absolute HTTP or HTTPS URL.</exception>
    /// <example>
    /// <code lang="csharp">
    /// var forwardAuth = traefik.AddForwardAuth("auth-check", "https://auth.example.com/verify");
    ///
    /// api.WithTraefikMiddleware(forwardAuth);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal addForwardAuth export, which accepts an endpoint, a string, or a reference expression.")]
    public static IResourceBuilder<TraefikMiddlewareResource> AddForwardAuth(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string address, Action<TraefikForwardAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"The address '{address}' must be an absolute HTTP or HTTPS URL.", nameof(address));
        }

        return builder.AddForwardAuth(name, ReferenceExpression.Create($"{address}"), configure);
    }

    /// <summary>
    /// Adds an oauth2-proxy container and a <c>forwardAuth</c> middleware that signs users in with an OpenID Connect
    /// provider before their requests reach the routes that use it.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware. The container is named <c>&lt;name&gt;-proxy</c>.</param>
    /// <param name="issuerUrl">The issuer URL of the provider, for example <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>.</param>
    /// <param name="clientId">The client ID registered with the provider.</param>
    /// <param name="clientSecret">The client secret registered with the provider.</param>
    /// <param name="configure">Sets more options, such as the allowed email domains or groups.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// A request without a session is redirected to the provider. After signing in, the provider redirects the browser
    /// to <c>/oauth2/callback</c> on the protected host, so every host that uses the middleware also gets a route for
    /// <c>/oauth2/*</c> to oauth2-proxy. Register <c>&lt;public URL of each protected host&gt;/oauth2/callback</c> as a redirect
    /// URI with the provider. <see cref="TraefikOAuth2ProxyOptions.ProxyPrefix"/> changes the <c>/oauth2</c> path.
    /// </para>
    /// <para>
    /// The signed-in user is passed to the backend in the <c>X-Auth-Request-User</c>, <c>X-Auth-Request-Email</c>,
    /// <c>X-Auth-Request-Preferred-Username</c>, and <c>X-Auth-Request-Groups</c> headers. The session cookie is
    /// encrypted with a generated secret, and it is marked secure when the routes use HTTPS. Routes that use the middleware
    /// need a host. See <see href="https://oauth2-proxy.github.io/oauth2-proxy/configuration/integrations/traefik"/>.
    /// </para>
    /// </remarks>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikMiddlewareResource> AddOAuth2ProxyForwardAuth(
        this IResourceBuilder<TraefikResource> builder,
        [ResourceName] string name,
        ReferenceExpression issuerUrl,
        string clientId,
        IResourceBuilder<ParameterResource> clientSecret,
        Action<TraefikOAuth2ProxyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        TraefikNames.ThrowIfInvalidName(name, "middleware name", nameof(name));
        ArgumentNullException.ThrowIfNull(issuerUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(clientSecret);

        var options = new TraefikOAuth2ProxyOptions();
        configure?.Invoke(options);

        // The prefix becomes a PathPrefix matcher of the sign-in routes, and oauth2-proxy appends `/callback` and
        // the other pages to it, so a trailing `/` or the root would take over every path of the protected hosts.
        // https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview#command-line-options
        TraefikNames.ThrowIfInvalidPath(options.ProxyPrefix, "proxy prefix", nameof(configure));
        if (options.ProxyPrefix.EndsWith('/') || options.ProxyPrefix.Contains('`'))
        {
            throw new ArgumentException($"The proxy prefix '{options.ProxyPrefix}' can't end with '/' or contain '`'.", nameof(configure));
        }

        var discoveryUrls = (options.LoginUrl, options.RedeemUrl, options.JwksUrl);
        if (discoveryUrls is not (null, null, null) && discoveryUrls is not (not null, not null, not null))
        {
            throw new ArgumentException($"Set {nameof(TraefikOAuth2ProxyOptions.LoginUrl)}, {nameof(TraefikOAuth2ProxyOptions.RedeemUrl)}, and {nameof(TraefikOAuth2ProxyOptions.JwksUrl)} together, or none of them.", nameof(configure));
        }

        var applicationBuilder = builder.ApplicationBuilder;
        var traefik = builder.Resource;
        var proxyName = $"{name}-proxy";

        // The container is added before the middleware, which needs its endpoint, so both names are checked first: a
        // clash found by AddResource would otherwise leave the container in the model without its middleware.
        foreach (var resourceName in (string[])[name, proxyName])
        {
            if (applicationBuilder.Resources.FirstOrDefault(r => StringComparers.ResourceName.Equals(r.Name, resourceName)) is { } existing)
            {
                throw new ArgumentException($"The oauth2-proxy middleware '{name}' needs the resource name '{resourceName}', which resource '{existing.Name}' already has.", nameof(name));
            }
        }

        // The secret encrypts the session cookie; oauth2-proxy needs exactly 16, 24, or 32 bytes.
        var cookieSecret = ParameterResourceBuilderExtensions.CreateGeneratedParameter(
            applicationBuilder, $"{name}-cookie-secret", secret: true, new GenerateParameterDefault { MinLength = 32, Special = false });

        var proxy = applicationBuilder.AddContainer(proxyName, TraefikContainerImageTags.OAuth2ProxyImage, TraefikContainerImageTags.OAuth2ProxyTag)
            .WithImageRegistry(TraefikContainerImageTags.OAuth2ProxyRegistry)
            .WithIconName("PersonLock")
            .WithHttpEndpoint(targetPort: OAuth2ProxyPort, name: OAuth2ProxyEndpointName)
            .WithHttpHealthCheck("/ping")
            // Secrets go into the environment, not the arguments, which the dashboard shows.
            .WithEnvironment("OAUTH2_PROXY_CLIENT_SECRET", clientSecret)
            .WithEnvironment("OAUTH2_PROXY_COOKIE_SECRET", cookieSecret);

        var middleware = builder.AddForwardAuth(name, proxy.GetEndpoint(OAuth2ProxyEndpointName), "/", forwardAuth =>
        {
            // The headers oauth2-proxy sets with --set-xauthrequest.
            forwardAuth.AuthResponseHeaders.Add("X-Auth-Request-User");
            forwardAuth.AuthResponseHeaders.Add("X-Auth-Request-Email");
            forwardAuth.AuthResponseHeaders.Add("X-Auth-Request-Preferred-Username");
            forwardAuth.AuthResponseHeaders.Add("X-Auth-Request-Groups");
        });

        middleware.Resource.OAuth2Proxy = proxy.Resource;
        middleware.Resource.OAuth2ProxyPrefix = options.ProxyPrefix;
        proxy.WithParentRelationship(middleware);

        proxy.WithArgs(context =>
        {
            // https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview#command-line-options
            var args = context.Args;
            var start = args.Count;
            args.Add($"--http-address=0.0.0.0:{OAuth2ProxyPort}");
            args.Add($"--proxy-prefix={options.ProxyPrefix}");
            args.Add($"--provider={options.Provider}");
            args.Add($"--client-id={clientId}");
            args.Add(ReferenceExpression.Create($"--oidc-issuer-url={issuerUrl}"));

            if (options is { LoginUrl: { } loginUrl, RedeemUrl: { } redeemUrl, JwksUrl: { } jwksUrl })
            {
                args.Add("--skip-oidc-discovery=true");
                args.Add(ReferenceExpression.Create($"--login-url={loginUrl}"));
                args.Add(ReferenceExpression.Create($"--redeem-url={redeemUrl}"));
                args.Add(ReferenceExpression.Create($"--oidc-jwks-url={jwksUrl}"));
            }

            // Forward authentication: Traefik calls `/`, which answers 202 for a signed-in user (the static upstream)
            // and redirects to the provider otherwise. Traefik passes that redirect to the browser.
            // https://oauth2-proxy.github.io/oauth2-proxy/configuration/integrations/traefik#forwardauth-with-static-upstreams-configuration
            args.Add("--upstream=static://202");
            args.Add("--reverse-proxy=true");
            args.Add("--set-xauthrequest=true");
            args.Add("--skip-provider-button=true");
            args.Add("--code-challenge-method=S256");

            // One cookie serves every protected host, so it is only Secure when none of them is plain HTTP, for example
            // when HTTPS is Traefik's default but a route sets EntryPoint to `web`.
            var isSecure = options.CookieSecure ?? (!middleware.Resource.ProtectsPlainHttp
                && traefik.GetRouteDefaults(context.ExecutionContext.IsPublishMode).IsSecure);
            args.Add($"--cookie-secure={TraefikNames.ToLiteral(isSecure)}");

            if (options.CookieName is { } cookieName)
            {
                args.Add($"--cookie-name={cookieName}");
            }

            foreach (var cookieDomain in options.CookieDomains)
            {
                args.Add($"--cookie-domain={ToListValue(cookieDomain)}");
            }

            foreach (var domain in options.AllowedEmailDomains.DefaultIfEmpty("*"))
            {
                args.Add($"--email-domain={ToListValue(domain)}");
            }

            foreach (var group in options.AllowedGroups)
            {
                args.Add($"--allowed-group={ToListValue(group)}");
            }

            if (options.Scope is { } scope)
            {
                args.Add($"--scope={scope}");
            }

            if (options.AllowUnverifiedEmail)
            {
                args.Add("--insecure-oidc-allow-unverified-email=true");
            }

            // After signing in, oauth2-proxy only redirects back to allowed domains. A domain without a port only allows
            // the standard ports, so `:*` is needed in run mode, where the hosts are reached on Traefik's random host
            // port, and at publish time when an entry point listens on another port, such as 8443.
            // https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview#command-line-options
            var portSuffix = context.ExecutionContext.IsPublishMode && !middleware.Resource.ProtectsNonStandardPort ? "" : ":*";
            foreach (var host in middleware.Resource.ProtectedHosts)
            {
                args.Add(ReferenceExpression.Create($"--whitelist-domain={host}{portSuffix}"));
            }

            foreach (var argument in options.AdditionalArguments)
            {
                args.Add(argument);
            }

            // Literal values such as a group name reach the compose file's `command`, which Compose interpolates; see
            // TraefikDockerLabels.EscapeArguments. Run mode passes the arguments to the container as they are.
            if (context.ExecutionContext.IsPublishMode)
            {
                TraefikDockerLabels.EscapeArguments(args, start);
            }
        });

        return middleware;
    }

    /// <summary>
    /// Adds an oauth2-proxy container and a <c>forwardAuth</c> middleware that signs users in with a Keycloak realm before
    /// their requests reach the routes that use it.
    /// </summary>
    /// <typeparam name="TKeycloak">The type of the Keycloak resource.</typeparam>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware. The container is named <c>&lt;name&gt;-proxy</c>.</param>
    /// <param name="keycloak">The Keycloak resource. It must have a Traefik route, which is its public URL.</param>
    /// <param name="realm">The realm users sign in to.</param>
    /// <param name="clientId">The client ID of the confidential client registered in the realm.</param>
    /// <param name="clientSecret">The secret of that client.</param>
    /// <param name="configure">Sets more options, such as the allowed groups.</param>
    /// <param name="endpointName">The endpoint oauth2-proxy calls Keycloak on, inside the application.</param>
    /// <param name="configureHostname">
    /// Whether to configure Keycloak's hostname for running behind Traefik: <c>KC_HOSTNAME</c> (the public URL),
    /// <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c>, <c>KC_PROXY_HEADERS</c>, and <c>KC_HTTP_ENABLED</c>.
    /// </param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The browser signs in on Keycloak's public URL, while oauth2-proxy redeems codes and reads signing keys on Keycloak's
    /// internal endpoint. That works because <c>KC_HOSTNAME</c> makes the public URL the token issuer and
    /// <c>KC_HOSTNAME_BACKCHANNEL_DYNAMIC</c> keeps the internal address working for server-to-server calls. See
    /// <see href="https://www.keycloak.org/server/hostname"/>.
    /// </para>
    /// <para>
    /// The client must be confidential, use the standard flow, and allow <c>&lt;public URL of each protected host&gt;/oauth2/callback</c>
    /// as a redirect URI. Its tokens need the client ID in the audience: add an "Audience" mapper to the client's
    /// dedicated scope. Users need a verified email unless <see cref="TraefikOAuth2ProxyOptions.AllowUnverifiedEmail"/>
    /// is set.
    /// </para>
    /// <para>
    /// Any resource with endpoints can be passed, so this package doesn't depend on the Keycloak integration.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var keycloak = builder.AddKeycloak("keycloak")
    ///     .WithRealmImport("./realms")
    ///     .WithTraefikRoute(traefik, ReferenceExpression.Create($"auth.{domain}"));
    ///
    /// var sso = traefik.AddKeycloakForwardAuth("sso", keycloak, realm: "demo", clientId: "oauth2-proxy", clientSecret);
    ///
    /// builder.AddProject&lt;Projects.Admin&gt;("admin")
    ///     .WithTraefikRoute(traefik, ReferenceExpression.Create($"admin.{domain}"))
    ///     .WithTraefikMiddleware(sso);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Generic resource constraints are not supported by ATS yet.")]
    public static IResourceBuilder<TraefikMiddlewareResource> AddKeycloakForwardAuth<TKeycloak>(
        this IResourceBuilder<TraefikResource> builder,
        [ResourceName] string name,
        IResourceBuilder<TKeycloak> keycloak,
        string realm,
        string clientId,
        IResourceBuilder<ParameterResource> clientSecret,
        Action<TraefikOAuth2ProxyOptions>? configure = null,
        string endpointName = "http",
        bool configureHostname = true)
        where TKeycloak : class, IResourceWithEndpoints, IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(keycloak);
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(clientSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        if (!keycloak.Resource.Annotations.OfType<TraefikRouteAnnotation>().Any())
        {
            throw new InvalidOperationException($"Keycloak resource '{keycloak.Resource.Name}' has no Traefik route, so it has no public URL to sign in on. Call WithTraefikRoute on it first.");
        }

        var publicUrl = keycloak.GetTraefikUrl();
        var internalEndpoint = keycloak.Resource.GetEndpoint(endpointName);
        if (!internalEndpoint.Exists)
        {
            throw new InvalidOperationException($"Keycloak resource '{keycloak.Resource.Name}' has no endpoint named '{endpointName}'.");
        }

        var issuer = ReferenceExpression.Create($"{publicUrl}/realms/{realm}");
        var openIdConnect = ReferenceExpression.Create($"{internalEndpoint}/realms/{realm}/protocol/openid-connect");

        var middleware = builder.AddOAuth2ProxyForwardAuth(name, issuer, clientId, clientSecret, options =>
        {
            options.Provider = "keycloak-oidc";
            options.LoginUrl = ReferenceExpression.Create($"{issuer}/protocol/openid-connect/auth");
            options.RedeemUrl = ReferenceExpression.Create($"{openIdConnect}/token");
            options.JwksUrl = ReferenceExpression.Create($"{openIdConnect}/certs");

            configure?.Invoke(options);
        });

        // AddOAuth2ProxyForwardAuth always sets the container.
        builder.ApplicationBuilder.CreateResourceBuilder(middleware.Resource.OAuth2Proxy!)
            .WaitFor(keycloak);

        // Last, so a failure above leaves Keycloak unchanged.
        if (configureHostname)
        {
            // https://www.keycloak.org/server/hostname and https://www.keycloak.org/server/reverseproxy
            keycloak
                .WithEnvironment("KC_HOSTNAME", publicUrl)
                .WithEnvironment("KC_HOSTNAME_BACKCHANNEL_DYNAMIC", "true")
                .WithEnvironment("KC_PROXY_HEADERS", "xforwarded")
                .WithEnvironment("KC_HTTP_ENABLED", "true");
        }

        return middleware;
    }

    /// <summary>
    /// Adds a <c>forwardAuth</c> middleware that asks an authentication server whether a request may pass.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="address">
    /// The authentication server: an endpoint of a resource in the application, an absolute URL, or a reference expression
    /// that builds the URL.
    /// </param>
    /// <param name="path">The path of the authentication request on an endpoint, for example <c>/verify</c>. Only valid with an endpoint.</param>
    /// <param name="configure">Sets more options, such as the response headers copied to the forwarded request.</param>
    /// <returns>A reference to the middleware's resource builder.</returns>
    /// <remarks>
    /// A <c>2xx</c> response lets the request through; any other response, for example a redirect to a sign-in page, is
    /// returned to the client.
    /// </remarks>
    [AspireExport("addForwardAuth", RunSyncOnBackgroundThread = true)]
    internal static IResourceBuilder<TraefikMiddlewareResource> AddForwardAuthPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [ResourceName] string name,
        [AspireUnion(typeof(EndpointReference), typeof(string), typeof(ReferenceExpression))] object address,
        string? path = null,
        Action<TraefikForwardAuthOptions>? configure = null)
    {
        return address switch
        {
            EndpointReference endpoint => builder.AddForwardAuth(name, endpoint, path ?? "/", configure),
            string url when path is null => builder.AddForwardAuth(name, url, configure),
            ReferenceExpression expression when path is null => builder.AddForwardAuth(name, expression, configure),
            string or ReferenceExpression => throw new ArgumentException("A path can only be given with an endpoint address.", nameof(path)),
            _ => throw new ArgumentException($"Unsupported type '{address?.GetType().Name}'. Expected an endpoint, a string, or a reference expression.", nameof(address)),
        };
    }

    // oauth2-proxy's list options (`--allowed-group`, `--email-domain`, `--cookie-domain`) are pflag string slices, which
    // read each value as one CSV record, so `cn=admins,ou=groups` would be two groups. A quoted field keeps its commas,
    // and a quote inside it is doubled:
    //   --allowed-group="cn=admins,ou=groups"   -> [cn=admins,ou=groups]
    //   --allowed-group=say "hi"                -> error: bare quote in a non-quoted field
    // https://github.com/spf13/pflag/blob/master/string_slice.go and https://pkg.go.dev/encoding/csv
    private static string ToListValue(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
}
