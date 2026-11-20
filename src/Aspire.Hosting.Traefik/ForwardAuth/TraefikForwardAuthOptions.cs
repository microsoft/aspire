// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for a Traefik <c>forwardAuth</c> middleware.
/// </summary>
/// <remarks>
/// Unset options are left out, so Traefik's defaults apply. See
/// <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/forwardauth/"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikForwardAuthOptions
{
    /// <summary>
    /// Gets the headers copied from the authentication server's response to the forwarded request, for example
    /// <c>X-Auth-Request-User</c>.
    /// </summary>
    public IList<string> AuthResponseHeaders { get; } = [];

    /// <summary>
    /// Gets or sets a regular expression for more response headers to copy to the forwarded request.
    /// </summary>
    public string? AuthResponseHeadersRegex { get; set; }

    /// <summary>
    /// Gets the request headers sent to the authentication server. When empty, all headers are sent.
    /// </summary>
    public IList<string> AuthRequestHeaders { get; } = [];

    /// <summary>
    /// Gets the cookies from the authentication server's response that are added to the response to the client.
    /// </summary>
    public IList<string> AddAuthCookiesToResponse { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether the request body is sent to the authentication server.
    /// </summary>
    public bool? ForwardBody { get; set; }

    /// <summary>
    /// Gets or sets the largest request body sent to the authentication server when <see cref="ForwardBody"/> is set.
    /// </summary>
    public long? MaxBodySize { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a relative <c>Location</c> header from the authentication server is kept
    /// as is instead of being made absolute.
    /// </summary>
    public bool? PreserveLocationHeader { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the authentication request uses the method of the original request
    /// instead of <c>GET</c>.
    /// </summary>
    public bool? PreserveRequestMethod { get; set; }

    /// <summary>
    /// Sets the options that have a value on the middleware. Unset options keep Traefik's defaults.
    /// </summary>
    /// <param name="options">The options of the middleware the values are written to.</param>
    internal void ApplyTo(TraefikOptionMap options)
    {
        options.SetIfNotEmpty("authResponseHeaders", AuthResponseHeaders);
        options.SetIfNotNull("authResponseHeadersRegex", AuthResponseHeadersRegex);
        options.SetIfNotEmpty("authRequestHeaders", AuthRequestHeaders);
        options.SetIfNotEmpty("addAuthCookiesToResponse", AddAuthCookiesToResponse);
        options.SetIfNotNull("forwardBody", ForwardBody);
        options.SetIfNotNull("maxBodySize", MaxBodySize);
        options.SetIfNotNull("preserveLocationHeader", PreserveLocationHeader);
        options.SetIfNotNull("preserveRequestMethod", PreserveRequestMethod);
    }
}

/// <summary>
/// Options for the oauth2-proxy container added by <c>AddOAuth2ProxyForwardAuth</c>.
/// </summary>
/// <remarks>
/// See <see href="https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikOAuth2ProxyOptions
{
    /// <summary>
    /// Gets or sets the oauth2-proxy provider, for example <c>oidc</c> (any OpenID Connect provider), <c>keycloak-oidc</c>,
    /// or <c>entra-id</c>. Defaults to <c>oidc</c>.
    /// </summary>
    public string Provider { get; set; } = "oidc";

    /// <summary>
    /// Gets or sets the URL the browser is sent to for signing in. Set it together with <see cref="RedeemUrl"/> and
    /// <see cref="JwksUrl"/> to skip OpenID Connect discovery, for example when oauth2-proxy reaches the provider on a
    /// different address than the browser.
    /// </summary>
    public ReferenceExpression? LoginUrl { get; set; }

    /// <summary>
    /// Gets or sets the token endpoint oauth2-proxy calls to redeem the authorization code.
    /// </summary>
    public ReferenceExpression? RedeemUrl { get; set; }

    /// <summary>
    /// Gets or sets the URL of the provider's token signing keys.
    /// </summary>
    public ReferenceExpression? JwksUrl { get; set; }

    /// <summary>
    /// Gets the email domains of users who may sign in, for example <c>example.com</c>. When empty, any email is accepted.
    /// </summary>
    public IList<string> AllowedEmailDomains { get; } = [];

    /// <summary>
    /// Gets the groups a user must belong to (any of them) to sign in. When empty, group membership isn't checked.
    /// </summary>
    public IList<string> AllowedGroups { get; } = [];

    /// <summary>
    /// Gets or sets the requested OAuth scopes. When unset, oauth2-proxy uses <c>openid email profile</c>.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether users whose email address isn't verified by the provider may sign in.
    /// </summary>
    public bool AllowUnverifiedEmail { get; set; }

    /// <summary>
    /// Gets or sets the path oauth2-proxy serves its sign-in, callback, and sign-out pages under on every protected host.
    /// Defaults to <c>/oauth2</c>.
    /// </summary>
    /// <remarks>
    /// Requests under this path on a protected host go to oauth2-proxy instead of the routed resource, so change it when
    /// a protected application has its own <c>/oauth2</c> paths. The redirect URI to register with the provider is
    /// <c>&lt;public URL of the host&gt;&lt;ProxyPrefix&gt;/callback</c>.
    /// </remarks>
    public string ProxyPrefix { get; set; } = "/oauth2";

    /// <summary>
    /// Gets or sets a value indicating whether the session cookie is <c>Secure</c>, which browsers only send over HTTPS.
    /// When unset, it is <c>Secure</c> unless a protected route is served over plain HTTP.
    /// </summary>
    public bool? CookieSecure { get; set; }

    /// <summary>
    /// Gets or sets the name of the session cookie. When unset, oauth2-proxy uses <c>_oauth2_proxy</c>.
    /// </summary>
    /// <remarks>
    /// Give each oauth2-proxy middleware its own name when they share a cookie domain, so their sessions don't overwrite each other.
    /// </remarks>
    public string? CookieName { get; set; }

    /// <summary>
    /// Gets the domains the session cookie is set for, for example <c>.example.com</c> to sign in once for every
    /// subdomain. When empty, the cookie belongs to the host that set it.
    /// </summary>
    public IList<string> CookieDomains { get; } = [];

    /// <summary>
    /// Gets more command-line arguments passed to oauth2-proxy, for example <c>--session-store-type=redis</c>. They are
    /// added after the arguments the integration sets.
    /// </summary>
    /// <remarks>
    /// Each entry is one argument, passed as it is: no shell splits it, and a <c>$</c> stays literal in the published
    /// Docker Compose file. The arguments are shown in the dashboard and written to the published files, so pass secrets
    /// as environment variables instead. See <see href="https://oauth2-proxy.github.io/oauth2-proxy/configuration/overview"/>.
    /// </remarks>
    public IList<string> AdditionalArguments { get; } = [];
}
