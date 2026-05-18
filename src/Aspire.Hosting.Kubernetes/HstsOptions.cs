// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Configures the HSTS (<c>Strict-Transport-Security</c>) response header emitted by
/// <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>.
/// </summary>
/// <remarks>
/// HSTS belongs at the gateway, not the backend, because (a) it is a transport-layer
/// assertion ("this hostname does TLS") and the thing that knows that is the thing
/// terminating TLS, (b) Aspire AppHosts mix polyglot backends — the gateway is the
/// uniform answer regardless of what is behind it, and (c) HSTS is per-hostname and
/// gateways already scope per-hostname.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class HstsOptions
{
    /// <summary>
    /// When <see langword="true"/> (the default), the <c>Strict-Transport-Security</c>
    /// header is added via <c>ResponseHeaderModifier</c> filters on user routes
    /// attached to the gateway's HTTPS listener.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The <c>max-age</c> directive on the HSTS header. Defaults to 365 days, matching
    /// the modal value across production HTTPS sites (GitHub, Microsoft, Azure,
    /// YouTube, Reddit, Spring Security defaults), the HSTS preload list minimum, and
    /// the Mozilla Observatory A+ threshold.
    /// </summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// When <see langword="true"/>, the <c>includeSubDomains</c> directive is appended,
    /// extending HSTS to every subdomain of the apex hostname. Off by default because a
    /// mistake here is not recoverable on client cached entries for up to <see cref="MaxAge"/>.
    /// </summary>
    public bool IncludeSubDomains { get; set; }

    /// <summary>
    /// When <see langword="true"/>, the <c>preload</c> directive is appended, marking
    /// the policy eligible for submission to Chrome, Firefox, and Safari's baked-in
    /// HSTS preload lists. Off by default — this is a consent flag that a scaffolding
    /// command should not opt users into.
    /// </summary>
    /// <remarks>
    /// See <see href="https://hstspreload.org/">hstspreload.org</see> for the eligibility
    /// rules; in particular, preload requires <see cref="IncludeSubDomains"/> = true,
    /// <see cref="MaxAge"/> ≥ 1 year, and full-site HTTPS.
    /// </remarks>
    public bool Preload { get; set; }
}
