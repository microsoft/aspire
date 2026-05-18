// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Configures the full TLS posture applied by
/// <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
/// and its overloads: HTTPS termination, the HTTP→HTTPS redirect on the HTTP listener,
/// and HSTS on HTTPS responses.
/// </summary>
/// <remarks>
/// <para>
/// TLS termination, the HTTP→HTTPS redirect, and HSTS are three faces of one decision —
/// "this gateway is HTTPS-only". Keeping them on one options bag keeps users from
/// having to remember to call paired methods in the right order, and leaves room for
/// future TLS knobs (cipher policy, TLS version floor, mTLS) to land on the same type.
/// </para>
/// <para>
/// Port 80 on a TLS'd gateway only exists for ACME HTTP-01 challenges; every other
/// request gets a 301 redirect to HTTPS by default. The cert-manager Gateway API HTTP-01
/// solver creates its own HTTPRoute with <c>path.type: Exact</c>, which wins over the
/// catch-all redirect's <c>PathPrefix: /</c> per Gateway API route precedence rules, so
/// ACME validation continues to work without a carve-out in the redirect.
/// </para>
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TlsOptions
{
    /// <summary>
    /// When <see langword="true"/> (the default), <see cref="KubernetesGatewayExtensions.WithTls(global::Aspire.Hosting.ApplicationModel.IResourceBuilder{KubernetesGatewayResource}, System.Action{TlsOptions}?)"/>
    /// emits a synthetic <c>HTTPRoute</c> bound to the HTTP listener that returns a
    /// <c>301 Moved Permanently</c> response with a <c>Location</c> header pointing at
    /// the HTTPS scheme. Set to <see langword="false"/> for the rare case where a
    /// hostname legitimately needs plaintext compatibility.
    /// </summary>
    /// <remarks>
    /// 301 (not 308) is the chosen status code because it matches what every major
    /// HTTPS-only site on the public internet returns — clients, CDNs, proxies, and
    /// HTTP libraries are battle-tested against 301, and the gain from 308's strict
    /// method preservation is small relative to the deviation it causes.
    /// </remarks>
    public bool RedirectHttp { get; set; } = true;

    /// <summary>
    /// HSTS (HTTP Strict Transport Security) policy applied via
    /// <c>ResponseHeaderModifier</c> filters on user <c>HTTPRoute</c> resources attached
    /// to the gateway's HTTPS listener.
    /// </summary>
    public HstsOptions Hsts { get; } = new();
}
