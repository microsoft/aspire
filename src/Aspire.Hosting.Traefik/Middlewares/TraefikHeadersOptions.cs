// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options for a Traefik <c>headers</c> middleware.
/// </summary>
/// <remarks>
/// Unset options are left out, so Traefik's defaults apply. Values that need parameters can be set afterwards with
/// <c>WithOption</c>, for example <c>WithOption("accessControlAllowOriginList", [ReferenceExpression.Create($"https://app.{domain}")])</c>.
/// See <see href="https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/headers/"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikHeadersOptions
{
    /// <summary>
    /// Gets the headers added to the request before it is forwarded. An empty value removes the header.
    /// </summary>
    public IDictionary<string, string> CustomRequestHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the headers added to the response. An empty value removes the header.
    /// </summary>
    public IDictionary<string, string> CustomResponseHeaders { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the origins allowed by CORS (<c>Access-Control-Allow-Origin</c>), for example <c>https://app.example.com</c>.
    /// </summary>
    public IList<string> AccessControlAllowOriginList { get; } = [];

    /// <summary>
    /// Gets the methods allowed by CORS (<c>Access-Control-Allow-Methods</c>).
    /// </summary>
    public IList<string> AccessControlAllowMethods { get; } = [];

    /// <summary>
    /// Gets the request headers allowed by CORS (<c>Access-Control-Allow-Headers</c>).
    /// </summary>
    public IList<string> AccessControlAllowHeaders { get; } = [];

    /// <summary>
    /// Gets the response headers exposed by CORS (<c>Access-Control-Expose-Headers</c>).
    /// </summary>
    public IList<string> AccessControlExposeHeaders { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether CORS requests may include credentials (<c>Access-Control-Allow-Credentials</c>).
    /// </summary>
    public bool? AccessControlAllowCredentials { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, the result of a CORS preflight request can be cached (<c>Access-Control-Max-Age</c>).
    /// </summary>
    public long? AccessControlMaxAge { get; set; }

    /// <summary>
    /// Gets or sets the <c>max-age</c> of the <c>Strict-Transport-Security</c> header, in seconds. Traefik only sends the
    /// header on HTTPS responses.
    /// </summary>
    public long? StsSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <c>Strict-Transport-Security</c> header includes <c>includeSubDomains</c>.
    /// </summary>
    public bool? StsIncludeSubdomains { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <c>Strict-Transport-Security</c> header includes <c>preload</c>.
    /// </summary>
    public bool? StsPreload { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>X-Frame-Options: DENY</c> is added.
    /// </summary>
    public bool? FrameDeny { get; set; }

    /// <summary>
    /// Gets or sets the <c>X-Frame-Options</c> value, for example <c>SAMEORIGIN</c>. Overrides <see cref="FrameDeny"/>.
    /// </summary>
    public string? CustomFrameOptionsValue { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether <c>X-Content-Type-Options: nosniff</c> is added.
    /// </summary>
    public bool? ContentTypeNosniff { get; set; }

    /// <summary>
    /// Gets or sets the <c>Referrer-Policy</c> value, for example <c>strict-origin-when-cross-origin</c>.
    /// </summary>
    public string? ReferrerPolicy { get; set; }

    /// <summary>
    /// Gets or sets the <c>Content-Security-Policy</c> value.
    /// </summary>
    public string? ContentSecurityPolicy { get; set; }

    /// <summary>
    /// Gets or sets the <c>Permissions-Policy</c> value.
    /// </summary>
    public string? PermissionsPolicy { get; set; }

    /// <summary>
    /// Sets the options that have a value on the middleware. Unset options keep Traefik's defaults.
    /// </summary>
    /// <param name="options">The options of the middleware the values are written to.</param>
    internal void ApplyTo(TraefikOptionMap options)
    {
        foreach (var (name, value) in CustomRequestHeaders)
        {
            options.Set($"customRequestHeaders.{name}", value);
        }

        foreach (var (name, value) in CustomResponseHeaders)
        {
            options.Set($"customResponseHeaders.{name}", value);
        }

        options.SetIfNotEmpty("accessControlAllowOriginList", AccessControlAllowOriginList);
        options.SetIfNotEmpty("accessControlAllowMethods", AccessControlAllowMethods);
        options.SetIfNotEmpty("accessControlAllowHeaders", AccessControlAllowHeaders);
        options.SetIfNotEmpty("accessControlExposeHeaders", AccessControlExposeHeaders);
        options.SetIfNotNull("accessControlAllowCredentials", AccessControlAllowCredentials);
        options.SetIfNotNull("accessControlMaxAge", AccessControlMaxAge);
        options.SetIfNotNull("stsSeconds", StsSeconds);
        options.SetIfNotNull("stsIncludeSubdomains", StsIncludeSubdomains);
        options.SetIfNotNull("stsPreload", StsPreload);
        options.SetIfNotNull("frameDeny", FrameDeny);
        options.SetIfNotNull("customFrameOptionsValue", CustomFrameOptionsValue);
        options.SetIfNotNull("contentTypeNosniff", ContentTypeNosniff);
        options.SetIfNotNull("referrerPolicy", ReferrerPolicy);
        options.SetIfNotNull("contentSecurityPolicy", ContentSecurityPolicy);
        options.SetIfNotNull("permissionsPolicy", PermissionsPolicy);
    }
}
