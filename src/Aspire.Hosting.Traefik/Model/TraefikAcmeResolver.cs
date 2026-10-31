// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// An ACME certificate resolver added with <c>WithLetsEncrypt</c>.
/// </summary>
/// <remarks>
/// Mutable because <c>WithLetsEncrypt</c> and <c>WithDnsChallenge</c> configure the same resolver, in either order of
/// later calls.
/// </remarks>
internal sealed class TraefikAcmeResolver(string name, ReferenceExpression email)
{
    /// <summary>
    /// Gets the name of the resolver, which routes reference with <c>certresolver</c>.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets or sets the email address registered with the certificate authority.
    /// </summary>
    public ReferenceExpression Email { get; set; } = email;

    /// <summary>
    /// Gets or sets the HTTP-01 or TLS-ALPN-01 challenge, used when <see cref="DnsChallenge"/> isn't set.
    /// </summary>
    public TraefikAcmeChallenge Challenge { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the resolver uses the Let's Encrypt staging directory.
    /// </summary>
    public bool Staging { get; set; }

    /// <summary>
    /// Gets or sets the DNS-01 challenge, which replaces <see cref="Challenge"/> when set.
    /// </summary>
    public TraefikDnsChallenge? DnsChallenge { get; set; }
}

/// <summary>
/// The DNS-01 challenge of a resolver, added with <c>WithDnsChallenge</c>.
/// </summary>
/// <param name="Provider">The lego DNS provider, for example <c>cloudflare</c>.</param>
/// <param name="Credentials">The environment variables the provider reads, by name.</param>
/// <param name="Resolvers">The DNS servers used to check that the challenge record propagated.</param>
/// <param name="DelayBeforeChecks">The delay before the propagation checks, as a Traefik duration.</param>
internal sealed record TraefikDnsChallenge(string Provider, IReadOnlyDictionary<string, ParameterResource> Credentials, IReadOnlyList<string> Resolvers, string? DelayBeforeChecks);

/// <summary>
/// A certificate requested for a domain and its alternative names, added with <c>WithTlsDomain</c>.
/// </summary>
/// <param name="Main">The main domain.</param>
/// <param name="Sans">The subject alternative names.</param>
internal sealed record TraefikTlsDomain(ReferenceExpression Main, IReadOnlyList<ReferenceExpression> Sans)
{
    /// <summary>
    /// Gets a value indicating whether a name is a wildcard, which Let's Encrypt only issues with the DNS-01 challenge.
    /// </summary>
    public bool HasWildcard => Sans.Prepend(Main).Any(name => name.Format.Contains('*'));
}
