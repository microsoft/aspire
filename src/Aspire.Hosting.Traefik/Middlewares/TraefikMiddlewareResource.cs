// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Traefik;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A Traefik HTTP middleware, which changes requests or responses on the routes that use it.
/// </summary>
/// <remarks>
/// <para>
/// A middleware is configuration of its parent <see cref="TraefikResource"/>; it doesn't run anything itself. It is
/// written to Traefik's dynamic configuration in run mode and to <c>traefik.http.middlewares.*</c> labels at publish
/// time. The Traefik middleware name is the resource name.
/// </para>
/// <para>
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/overview/"/>.
/// </para>
/// </remarks>
[AspireExport]
public class TraefikMiddlewareResource : Resource, IResourceWithParent<TraefikResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="TraefikMiddlewareResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource, which is also the Traefik middleware name.</param>
    /// <param name="type">The Traefik middleware type, for example <c>stripPrefix</c> or <c>headers</c>.</param>
    /// <param name="parent">The Traefik resource the middleware belongs to.</param>
    public TraefikMiddlewareResource(string name, string type, TraefikResource parent) : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(parent);

        Type = type;
        Parent = parent;
    }

    /// <summary>
    /// Gets the Traefik middleware type, for example <c>stripPrefix</c> or <c>headers</c>.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets the Traefik resource the middleware belongs to.
    /// </summary>
    public TraefikResource Parent { get; }

    /// <summary>
    /// Gets the options of the middleware, the keys under <c>&lt;type&gt;</c> in Traefik's configuration.
    /// </summary>
    internal TraefikOptionMap Options { get; } = new();

    /// <summary>
    /// Gets the middlewares this middleware refers to by name, such as the members of a <c>chain</c>. Their
    /// definitions must be published wherever this one is.
    /// </summary>
    internal List<TraefikMiddlewareResource> References { get; } = [];

    /// <summary>
    /// Gets or sets the oauth2-proxy container this middleware forwards to, when it was added with <c>AddOAuth2ProxyForwardAuth</c>.
    /// Routes that use such a middleware also get a sign-in route to the container.
    /// </summary>
    internal ContainerResource? OAuth2Proxy { get; set; }

    /// <summary>
    /// Gets or sets the path oauth2-proxy serves its pages under, which the sign-in routes match. Set with
    /// <see cref="OAuth2Proxy"/>.
    /// </summary>
    internal string? OAuth2ProxyPrefix { get; set; }

    /// <summary>
    /// Gets the hosts of the routes that use this middleware. Populated by <see cref="TraefikRoutingSubscriber"/>.
    /// </summary>
    internal List<ReferenceExpression> ProtectedHosts { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether a route that uses this middleware is served over plain HTTP. Populated by
    /// <see cref="TraefikRoutingSubscriber"/>.
    /// </summary>
    /// <remarks>
    /// oauth2-proxy has one session cookie for all hosts it protects. A <c>Secure</c> cookie is never sent over plain
    /// HTTP, so a single HTTP route means the cookie can't be <c>Secure</c>.
    /// </remarks>
    internal bool ProtectsPlainHttp { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a route that uses this middleware is published on an entry point whose
    /// public port isn't the standard one of its scheme. Populated by <see cref="TraefikRoutingSubscriber"/>.
    /// </summary>
    internal bool ProtectsNonStandardPort { get; set; }

    /// <summary>
    /// Gets or sets the resource that serves the error pages of an <c>errors</c> middleware. Its Traefik service name is only
    /// known once the routes are applied, so <see cref="TraefikRoutingSubscriber"/> sets the <c>service</c> option.
    /// </summary>
    internal IResource? ErrorPageResource { get; set; }

    /// <summary>
    /// Gets this middleware followed by every middleware it refers to, directly or through other middlewares.
    /// </summary>
    /// <returns>This middleware first, then the ones it refers to, depth first. A middleware reached twice is returned twice.</returns>
    internal IEnumerable<TraefikMiddlewareResource> WithReferences()
    {
        yield return this;

        foreach (var reference in References.SelectMany(r => r.WithReferences()))
        {
            yield return reference;
        }
    }
}
