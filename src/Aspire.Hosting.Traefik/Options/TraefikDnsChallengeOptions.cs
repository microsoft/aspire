// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Traefik;

/// <summary>
/// Options of the ACME DNS-01 challenge, set with <c>WithDnsChallenge</c>.
/// </summary>
/// <remarks>
/// Traefik answers the challenge with a TXT record created through the DNS provider's API, using
/// <see href="https://go-acme.github.io/lego/dns/">lego</see>. See
/// <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/#dnschallenge"/>.
/// </remarks>
public sealed class TraefikDnsChallengeOptions
{
    /// <summary>
    /// Gets or sets the name of the certificate resolver that uses the challenge. When unset, the default resolver: the
    /// first one added with <c>WithLetsEncrypt</c>.
    /// </summary>
    public string? ResolverName { get; set; }

    /// <summary>
    /// Gets the credentials of the DNS provider, as environment variable names and the parameters that hold them, for
    /// example <c>CF_DNS_API_TOKEN</c> for Cloudflare.
    /// </summary>
    /// <remarks>
    /// The variable names are listed per provider in the lego documentation. The variables are only set on Traefik when
    /// publishing, because certificates aren't requested in run mode, so <c>aspire run</c> doesn't ask for the values.
    /// </remarks>
    public IDictionary<string, IResourceBuilder<ParameterResource>> Credentials { get; } = new Dictionary<string, IResourceBuilder<ParameterResource>>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the DNS servers used to check that the TXT record has propagated, as <c>host:port</c>, for example
    /// <c>1.1.1.1:53</c>. When empty, the system's resolvers are used.
    /// </summary>
    public IList<string> Resolvers { get; } = [];

    /// <summary>
    /// Gets or sets how long to wait after creating the TXT record before checking that it has propagated.
    /// </summary>
    public TimeSpan? DelayBeforeChecks { get; set; }
}
