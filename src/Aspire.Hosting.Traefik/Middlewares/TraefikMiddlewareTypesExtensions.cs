// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding the common Traefik HTTP middlewares.
/// </summary>
/// <remarks>
/// Each method adds a <see cref="TraefikMiddlewareResource"/> of one Traefik type and sets its main options. Options
/// without a parameter can still be set with <c>WithOption</c>. The middleware reference is at
/// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/"/>.
/// </remarks>
public static class TraefikMiddlewareTypesExtensions
{
    /// <summary>
    /// The type of the <c>stripPrefix</c> middleware, also used for the middleware behind
    /// <see cref="TraefikRouteOptions.StripPathPrefix"/>.
    /// </summary>
    internal const string StripPrefixType = "stripPrefix";

    private const string BasicAuthType = "basicAuth";

    /// <summary>
    /// Adds a <c>stripPrefix</c> middleware, which removes a path prefix before the request is forwarded.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="prefixes">The prefixes to remove, for example <c>/api</c>. The first one that matches is removed.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// For a route's own path prefix, <see cref="TraefikRouteOptions.StripPathPrefix"/> is shorter. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/stripprefix/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddStripPrefix(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params string[] prefixes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequirePaths(prefixes, nameof(prefixes));

        return builder.AddMiddleware(name, StripPrefixType)
            .WithOption("prefixes", prefixes);
    }

    /// <summary>
    /// Adds a <c>stripPrefixRegex</c> middleware, which removes the part of the path that matches a regular expression.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="regex">The regular expressions, in Go syntax, for example <c>/users/[a-z0-9]+</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/stripprefixregex/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddStripPrefixRegex(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params string[] regex)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequireItems(regex, nameof(regex));

        return builder.AddMiddleware(name, "stripPrefixRegex")
            .WithOption("regex", regex);
    }

    /// <summary>
    /// Adds an <c>addPrefix</c> middleware, which adds a prefix to the path before the request is forwarded.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="prefix">The prefix, for example <c>/api</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/addprefix/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddAddPrefix(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequirePaths([prefix], nameof(prefix));

        return builder.AddMiddleware(name, "addPrefix")
            .WithOption("prefix", prefix);
    }

    /// <summary>
    /// Adds a <c>replacePath</c> middleware, which replaces the whole path. The original path is kept in the
    /// <c>X-Replaced-Path</c> header.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="path">The new path, for example <c>/health</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/replacepath/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddReplacePath(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string path)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequirePaths([path], nameof(path));

        return builder.AddMiddleware(name, "replacePath")
            .WithOption("path", path);
    }

    /// <summary>
    /// Adds a <c>replacePathRegex</c> middleware, which rewrites the path with a regular expression.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="regex">The regular expression, in Go syntax, for example <c>^/v1/(.*)</c>.</param>
    /// <param name="replacement">The replacement, which can refer to groups, for example <c>/$1</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <c>$</c> in the replacement is escaped in the published Docker Compose file, so group references reach Traefik
    /// unchanged. See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/replacepathregex/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddReplacePathRegex(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string regex, string replacement)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(regex);
        ArgumentNullException.ThrowIfNull(replacement);

        return builder.AddMiddleware(name, "replacePathRegex")
            .WithOption("regex", regex)
            .WithOption("replacement", replacement);
    }

    /// <summary>
    /// Adds a <c>redirectScheme</c> middleware, which redirects requests to another scheme, usually HTTPS.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="scheme">The scheme to redirect to.</param>
    /// <param name="permanent">Whether the redirect is permanent (301/308) rather than temporary (302/307).</param>
    /// <param name="port">The port to redirect to. When <see langword="null"/>, the default port of the scheme is used.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// To redirect all HTTP traffic to HTTPS, <c>WithHttpsRedirect</c> on Traefik is simpler. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/redirectscheme/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddRedirectScheme(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string scheme = "https", bool permanent = true, int? port = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);

        var middleware = builder.AddMiddleware(name, "redirectScheme")
            .WithOption("scheme", scheme)
            .WithOption("permanent", permanent);

        // Traefik's `port` option is a string.
        return port is int value ? middleware.WithOption("port", value.ToString(CultureInfo.InvariantCulture)) : middleware;
    }

    /// <summary>
    /// Adds a <c>redirectRegex</c> middleware, which redirects requests whose URL matches a regular expression.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="regex">The regular expression the full URL is matched against, in Go syntax, for example <c>^https://www\.(.*)</c>.</param>
    /// <param name="replacement">The URL to redirect to, which can refer to groups, for example <c>https://$1</c>.</param>
    /// <param name="permanent">Whether the redirect is permanent (301/308) rather than temporary (302/307).</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/redirectregex/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddRedirectRegex(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string regex, string replacement, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(regex);
        ArgumentException.ThrowIfNullOrEmpty(replacement);

        return builder.AddMiddleware(name, "redirectRegex")
            .WithOption("regex", regex)
            .WithOption("replacement", replacement)
            .WithOption("permanent", permanent);
    }

    /// <summary>
    /// Adds a <c>headers</c> middleware, which adds or removes request and response headers, answers CORS requests,
    /// and sets security headers.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="configure">Sets the headers.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/headers/"/>.</remarks>
    /// <example>
    /// <code lang="csharp">
    /// var cors = traefik.AddHeaders("cors", headers =>
    /// {
    ///     headers.AccessControlAllowOriginList.Add("https://app.example.com");
    ///     headers.AccessControlAllowMethods.Add("GET");
    ///     headers.CustomResponseHeaders["Server"] = ""; // removes the header
    /// });
    /// </code>
    /// </example>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikMiddlewareResource> AddHeaders(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, Action<TraefikHeadersOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new TraefikHeadersOptions();
        configure(options);

        var middleware = builder.AddMiddleware(name, "headers");
        options.ApplyTo(middleware.Resource.Options);

        return middleware;
    }

    /// <summary>
    /// Adds a <c>headers</c> middleware with common security headers.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="configure">Changes the defaults or sets more headers.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The defaults are <c>Strict-Transport-Security: max-age=31536000</c> (sent on HTTPS responses only),
    /// <c>X-Frame-Options: DENY</c>, <c>X-Content-Type-Options: nosniff</c>, and
    /// <c>Referrer-Policy: strict-origin-when-cross-origin</c>.
    /// </para>
    /// <para>
    /// <c>includeSubDomains</c> is not set by default, because it would apply HSTS to every subdomain of the host, also
    /// ones not served by this application. Set <see cref="TraefikHeadersOptions.StsIncludeSubdomains"/> to add it.
    /// </para>
    /// </remarks>
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<TraefikMiddlewareResource> AddSecurityHeaders(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, Action<TraefikHeadersOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddHeaders(name, options =>
        {
            options.StsSeconds = 31536000;
            options.FrameDeny = true;
            options.ContentTypeNosniff = true;
            options.ReferrerPolicy = "strict-origin-when-cross-origin";

            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Adds a <c>compress</c> middleware, which compresses responses with gzip, Brotli, or zstd, as the client accepts.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/compress/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddCompress(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddMiddleware(name, "compress");
    }

    /// <summary>
    /// Adds a <c>grpcWeb</c> middleware, which converts gRPC-Web requests from browsers into gRPC requests, so a
    /// browser can call a gRPC service without a gRPC-Web aware backend.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="allowOrigins">The origins allowed to call the service from a browser, for example <c>https://app.example.com</c>, or <c>*</c> for any.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The backend gets HTTP/2. For an endpoint whose transport is <c>http2</c>, such as a .NET gRPC service with
    /// HTTP/2-only Kestrel endpoints, Traefik already talks h2c to it. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/grpcweb/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddGrpcWeb(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params string[] allowOrigins)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(allowOrigins);

        var middleware = builder.AddMiddleware(name, "grpcWeb");

        return allowOrigins.Length > 0 ? middleware.WithOption("allowOrigins", allowOrigins) : middleware;
    }

    /// <summary>
    /// Adds a <c>retry</c> middleware, which resends a request to the backend when the connection fails.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="attempts">The number of attempts, including the first one.</param>
    /// <param name="initialInterval">The wait before the first retry, doubled for every later one. When <see langword="null"/>, retries are immediate.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Requests are only retried when the backend can't be reached or the connection breaks before a response, not on
    /// error status codes. See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/retry/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddRetry(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, int attempts, TimeSpan? initialInterval = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var middleware = builder.AddMiddleware(name, "retry")
            .WithOption("attempts", attempts);

        return initialInterval is { } interval ? middleware.WithOption("initialInterval", TraefikNames.ToDuration(interval, nameof(initialInterval))) : middleware;
    }

    /// <summary>
    /// Adds a <c>circuitBreaker</c> middleware, which stops forwarding requests to a failing backend for a while and
    /// answers <c>503</c> instead.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="expression">When the circuit opens, for example <c>NetworkErrorRatio() &gt; 0.30</c> or <c>ResponseCodeRatio(500, 600, 0, 600) &gt; 0.25</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <c>checkPeriod</c>, <c>fallbackDuration</c>, <c>recoveryDuration</c>, and <c>responseCode</c> can be set with
    /// <c>WithOption</c>. See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/circuitbreaker/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddCircuitBreaker(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string expression)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);

        return builder.AddMiddleware(name, "circuitBreaker")
            .WithOption("expression", expression);
    }

    /// <summary>
    /// Adds a <c>buffering</c> middleware, which reads whole requests and responses into memory, for example to limit
    /// upload sizes.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="maxRequestBodyBytes">The largest accepted request body; larger requests get <c>413</c>. When <see langword="null"/>, there is no limit.</param>
    /// <param name="maxResponseBodyBytes">The largest accepted response body; larger responses become <c>500</c>. When <see langword="null"/>, there is no limit.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/buffering/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddBuffering(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, long? maxRequestBodyBytes = null, long? maxResponseBodyBytes = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (maxRequestBodyBytes is { } request)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request, nameof(maxRequestBodyBytes));
        }

        if (maxResponseBodyBytes is { } response)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(response, nameof(maxResponseBodyBytes));
        }

        var middleware = builder.AddMiddleware(name, "buffering");

        if (maxRequestBodyBytes is { } maxRequest)
        {
            middleware.WithOption("maxRequestBodyBytes", maxRequest);
        }

        if (maxResponseBodyBytes is { } maxResponse)
        {
            middleware.WithOption("maxResponseBodyBytes", maxResponse);
        }

        return middleware;
    }

    /// <summary>
    /// Adds an <c>inFlightReq</c> middleware, which limits the number of requests handled at the same time per client
    /// IP; requests over the limit get <c>429</c>.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="amount">The number of simultaneous requests.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/inflightreq/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddInFlightRequestLimit(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, long amount)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);

        return builder.AddMiddleware(name, "inFlightReq")
            .WithOption("amount", amount);
    }

    /// <summary>
    /// Adds a <c>rateLimit</c> middleware, which limits requests per client IP; requests over the limit get <c>429</c>.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="average">The allowed number of requests per <paramref name="period"/>.</param>
    /// <param name="burst">The number of requests allowed above the average in a short burst.</param>
    /// <param name="period">The period of <paramref name="average"/>. When <see langword="null"/>, one second.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// The limit is kept in memory by each Traefik instance. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/ratelimit/"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddRateLimit(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, long average, long burst = 1, TimeSpan? period = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(average);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(burst);

        var middleware = builder.AddMiddleware(name, "rateLimit")
            .WithOption("average", average)
            .WithOption("burst", burst);

        return period is { } value ? middleware.WithOption("period", TraefikNames.ToDuration(value, nameof(period))) : middleware;
    }

    /// <summary>
    /// Adds an <c>ipAllowList</c> middleware, which only lets requests from the given IP ranges through; others get
    /// <c>403</c>.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="sourceRange">The allowed addresses or CIDR ranges, for example <c>10.0.0.0/8</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The client address is the peer address by default. Behind another proxy or load balancer, set
    /// <c>ipStrategy.depth</c> or <c>ipStrategy.excludedIPs</c> with <c>WithOption</c> to read it from
    /// <c>X-Forwarded-For</c>.
    /// </para>
    /// <para>
    /// In run mode requests reach the Traefik container from the container runtime's gateway, not from the client's
    /// address, so the list is mostly useful when published. See
    /// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/ipallowlist/"/>.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddIpAllowList(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params string[] sourceRange)
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequireItems(sourceRange, nameof(sourceRange));

        return builder.AddMiddleware(name, "ipAllowList")
            .WithOption("sourceRange", sourceRange);
    }

    /// <summary>
    /// Adds a <c>chain</c> middleware, which combines other middlewares into one that can be applied as a unit.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="middlewares">The middlewares of the chain, in the order they run. They must belong to the same Traefik.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/chain/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddChain(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params IResourceBuilder<TraefikMiddlewareResource>[] middlewares)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(middlewares);

        if (middlewares.Length == 0)
        {
            throw new ArgumentException("A chain needs at least one middleware.", nameof(middlewares));
        }

        var members = TraefikBuilderExtensions.GetOwnMiddlewares(builder, middlewares, nameof(middlewares));

        var chain = builder.AddMiddleware(name, "chain")
            .WithOption("middlewares", members.Select(m => m.Name));
        chain.Resource.References.AddRange(members);

        return chain;
    }

    /// <summary>
    /// Adds an <c>errors</c> middleware, which replaces error responses of the backend with pages served by another
    /// resource.
    /// </summary>
    /// <typeparam name="T">The type of the resource that serves the error pages.</typeparam>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="status">The status codes or ranges to replace, for example <c>500-599</c> or <c>404</c>.</param>
    /// <param name="errorPages">The resource that serves the pages. It needs a route through the same Traefik, whose service is used.</param>
    /// <param name="query">The path requested from <paramref name="errorPages"/>, where <c>{status}</c> is the status code, for example <c>/{status}.html</c>.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/errorpages/"/>.</remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddErrorPages<T>(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string[] status, IResourceBuilder<T> errorPages, string query = "/{status}.html")
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        RequireItems(status, nameof(status));
        ArgumentNullException.ThrowIfNull(errorPages);
        TraefikNames.ThrowIfInvalidPath(query, "query", nameof(query));

        var middleware = builder.AddMiddleware(name, "errors")
            .WithOption("status", status)
            .WithOption("query", query);
        middleware.Resource.ErrorPageResource = errorPages.Resource;

        return middleware;
    }

    /// <summary>
    /// Adds a <c>basicAuth</c> middleware that accepts the users of <c>htpasswd</c> entries.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="users">One parameter per user, each holding an <c>htpasswd</c> entry such as <c>admin:$2y$05$...</c> (bcrypt, MD5 (apr1), or SHA1).</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Create the entries with <c>htpasswd -nbB &lt;user&gt; &lt;password&gt;</c>. The same entries work in run mode and when
    /// published, and the plain password never reaches the AppHost or the deployment.
    /// </para>
    /// <para>
    /// At publish time each entry becomes a <c>${VAR}</c> placeholder. Docker Compose interpolates <c>$</c> in unquoted
    /// <c>.env</c> values, which hashes contain, so write the value in single quotes (<c>USER='admin:$2y$05$...'</c>) or
    /// double every <c>$</c>.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/basicauth/"/>.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikMiddlewareResource> AddBasicAuth(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, params IResourceBuilder<ParameterResource>[] users)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(users);

        if (users.Length == 0)
        {
            throw new ArgumentException("Basic authentication needs at least one user.", nameof(users));
        }

        if (users.Any(user => user is null))
        {
            throw new ArgumentException("A user is null.", nameof(users));
        }

        return builder.AddMiddleware(name, BasicAuthType)
            .WithOption("users", users.Select(user => ReferenceExpression.Create($"{user}")));
    }

    /// <summary>
    /// Adds a <c>basicAuth</c> middleware for one user with a plain password, for local development.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="name">The name of the middleware.</param>
    /// <param name="user">The user name.</param>
    /// <param name="password">The password.</param>
    /// <returns>A reference to the middleware's <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="InvalidOperationException">The application is being published.</exception>
    /// <remarks>
    /// <para>
    /// The password is hashed with SHA1 (the <c>{SHA}</c> <c>htpasswd</c> format) when Traefik's configuration is written.
    /// SHA1 is fast to brute-force, which is acceptable on a developer machine but not on a server.
    /// </para>
    /// <para>
    /// Only available in run mode, because the password is only known when the published files are deployed. Use the
    /// overload that takes <c>htpasswd</c> entries for published applications.
    /// </para>
    /// </remarks>
    [AspireExport("addBasicAuthWithPassword")]
    public static IResourceBuilder<TraefikMiddlewareResource> AddBasicAuth(this IResourceBuilder<TraefikResource> builder, [ResourceName] string name, string user, IResourceBuilder<ParameterResource> password)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        ArgumentNullException.ThrowIfNull(password);

        if (user.Contains(':') || user.Contains(','))
        {
            throw new ArgumentException($"The user name '{user}' can't contain ':' or ','.", nameof(user));
        }

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            throw new InvalidOperationException($"The Traefik middleware '{name}' uses a plain password, which can't be hashed when publishing. Pass htpasswd entries to AddBasicAuth instead.");
        }

        return builder.AddMiddleware(name, BasicAuthType)
            .WithOption("users", [ReferenceExpression.Create($"{new TraefikHtpasswdShaEntry(user, password.Resource)}")]);
    }

    private static void RequirePaths(string[] paths, string parameterName)
    {
        RequireItems(paths, parameterName);

        foreach (var path in paths)
        {
            TraefikNames.ThrowIfInvalidPath(path, "path", parameterName);
        }
    }

    private static void RequireItems(string[] values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);

        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one value is required, and values can't be empty.", parameterName);
        }
    }
}
