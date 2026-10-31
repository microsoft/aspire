// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Traefik;

namespace Aspire.Hosting;

public static partial class TraefikBuilderExtensions
{
    private const string DefaultLetsEncryptResolverName = "letsencrypt";

    /// <summary>
    /// Enables HTTPS with certificates issued by Let's Encrypt.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="email">The email address registered with Let's Encrypt, used for expiry notices.</param>
    /// <param name="resolverName">The name of the Traefik certificate resolver.</param>
    /// <param name="challenge">The ACME challenge used to prove control of the domains.</param>
    /// <param name="staging">Whether to use the Let's Encrypt staging environment, which has higher rate limits but issues untrusted certificates.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// Adds the <c>websecure</c> entry point (container port 443) and makes routes use it with TLS. At publish time
    /// the routes request certificates from the resolver, and the certificates are stored in a volume.
    /// </para>
    /// <para>
    /// Calling the method again with another <paramref name="resolverName"/> adds another resolver, for example one that
    /// uses the DNS-01 challenge (<c>WithDnsChallenge</c>) for some hosts. The first resolver is the default; a route
    /// uses another with <see cref="TraefikRouteOptions.CertResolver"/>. Calling it again with the same name replaces
    /// that resolver's settings.
    /// </para>
    /// <para>
    /// Let's Encrypt can't issue certificates for a local run, because it must reach the machine on public ports
    /// 80 and 443 under a public DNS name. In run mode HTTPS still works on <c>websecure</c>, with the HTTPS certificate
    /// from Aspire: the ASP.NET Core developer certificate, which browsers trust for <c>localhost</c> and
    /// <c>*.dev.localhost</c> once it's trusted (<c>dotnet dev-certs https --trust</c>), unless <c>WithHttpsCertificate</c> sets another one or <c>WithoutHttpsCertificate</c> turns
    /// it off. Without one, Traefik uses its self-signed default certificate.
    /// </para>
    /// <para>
    /// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, string email, string resolverName = DefaultLetsEncryptResolverName, TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        return ConfigureLetsEncrypt(builder, ReferenceExpression.Create($"{email}"), resolverName, challenge, staging);
    }

    /// <inheritdoc cref="WithLetsEncrypt(IResourceBuilder{TraefikResource}, string, string, TraefikAcmeChallenge, bool)"/>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, IResourceBuilder<ParameterResource> email, string resolverName = DefaultLetsEncryptResolverName, TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(email);

        return ConfigureLetsEncrypt(builder, ReferenceExpression.Create($"{email}"), resolverName, challenge, staging);
    }

    /// <inheritdoc cref="WithLetsEncrypt(IResourceBuilder{TraefikResource}, string, string, TraefikAcmeChallenge, bool)"/>
    /// <example>
    /// Build the address from the domain parameter instead of adding a second parameter:
    /// <code lang="csharp">
    /// var domain = builder.AddParameter("domain");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt(ReferenceExpression.Create($"admin@{domain}"));
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withLetsEncrypt export, which accepts a string, a parameter, or a reference expression.")]
    public static IResourceBuilder<TraefikResource> WithLetsEncrypt(this IResourceBuilder<TraefikResource> builder, ReferenceExpression email, string resolverName = DefaultLetsEncryptResolverName, TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn, bool staging = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(email);

        return ConfigureLetsEncrypt(builder, email, resolverName, challenge, staging);
    }

    /// <summary>
    /// Makes a certificate resolver prove control of the domains with the ACME DNS-01 challenge, through a DNS provider's
    /// API.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="provider">The lego name of the DNS provider, for example <c>cloudflare</c>, <c>route53</c>, or <c>azuredns</c>.</param>
    /// <param name="configure">Sets the provider's credentials and the other options of the challenge.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// DNS-01 works for hosts that the internet can't reach, and it is the only challenge that issues wildcard
    /// certificates (see <c>WithTlsDomain</c>). Call <c>WithLetsEncrypt</c> for the resolver first.
    /// </para>
    /// <para>
    /// See <see href="https://go-acme.github.io/lego/dns/"/> for the providers and their environment variables, and
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/#dnschallenge"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// var token = builder.AddParameter("cloudflare-token", secret: true);
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt("admin@example.com")
    ///     .WithDnsChallenge("cloudflare", dns => dns.Credentials["CF_DNS_API_TOKEN"] = token)
    ///     .WithTlsDomain("example.com", "*.example.com");
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "The options hold a dictionary of parameter builders, which polyglot AppHosts can't pass yet.")]
    public static IResourceBuilder<TraefikResource> WithDnsChallenge(this IResourceBuilder<TraefikResource> builder, string provider, Action<TraefikDnsChallengeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        if (!DnsProviderRegex().IsMatch(provider))
        {
            throw new ArgumentException($"The DNS provider '{provider}' is invalid. Use the provider's lego name, for example 'cloudflare'.", nameof(provider));
        }

        var options = new TraefikDnsChallengeOptions();
        configure?.Invoke(options);

        var resource = builder.Resource;
        var resolver = options.ResolverName is null ? resource.DefaultAcmeResolver : resource.FindAcmeResolver(options.ResolverName);

        if (resolver is null)
        {
            throw new InvalidOperationException(options.ResolverName is null
                ? $"Traefik resource '{resource.Name}' has no certificate resolver for the DNS challenge. Call WithLetsEncrypt first."
                : $"Traefik resource '{resource.Name}' has no certificate resolver named '{options.ResolverName}'. Call WithLetsEncrypt with that name first.");
        }

        foreach (var (name, parameter) in options.Credentials)
        {
            if (!EnvironmentVariableRegex().IsMatch(name))
            {
                throw new ArgumentException($"The DNS credential name '{name}' is invalid. Use the environment variable name from the lego documentation, for example 'CF_DNS_API_TOKEN'.", nameof(configure));
            }

            ArgumentNullException.ThrowIfNull(parameter, nameof(configure));
        }

        foreach (var server in options.Resolvers)
        {
            // lego expects `host:port`; without a port it fails only when the first certificate is requested.
            if (string.IsNullOrWhiteSpace(server) || !server.Contains(':') || server.Contains(','))
            {
                throw new ArgumentException($"The DNS resolver '{server}' is invalid. Use host:port, for example '1.1.1.1:53'.", nameof(configure));
            }
        }

        resolver.DnsChallenge = new TraefikDnsChallenge(
            provider,
            options.Credentials.ToDictionary(c => c.Key, c => c.Value.Resource, StringComparer.Ordinal),
            [.. options.Resolvers],
            options.DelayBeforeChecks is { } delay ? TraefikNames.ToDuration(delay, nameof(configure)) : null);

        return builder;
    }

    /// <summary>
    /// Requests a certificate for a domain and its alternative names on the <c>websecure</c> entry point, for example a
    /// wildcard certificate.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="main">The main domain of the certificate, for example <c>example.com</c>.</param>
    /// <param name="sans">The subject alternative names, for example <c>*.example.com</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException">A name is empty or contains <c>,</c>.</exception>
    /// <remarks>
    /// <para>
    /// Without TLS domains, Traefik requests a certificate per router host. With them, the default resolver requests one
    /// certificate per domain at publish time, and routers whose host it covers use it. A wildcard requires the DNS-01
    /// challenge (<c>WithDnsChallenge</c>) on the default resolver.
    /// </para>
    /// <para>
    /// The domains only apply to the managed Traefik at publish time; in run mode Traefik uses the HTTPS certificate from
    /// Aspire (see <c>WithLetsEncrypt</c>). See <see href="https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/#http-tls"/>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTlsDomain export, which accepts strings or reference expressions.")]
    public static IResourceBuilder<TraefikResource> WithTlsDomain(this IResourceBuilder<TraefikResource> builder, string main, params string[] sans)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(main);
        ArgumentNullException.ThrowIfNull(sans);

        foreach (var san in sans)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(san, nameof(sans));
        }

        return builder.WithTlsDomain(ReferenceExpression.Create($"{main}"), [.. sans.Select(san => ReferenceExpression.Create($"{san}"))]);
    }

    /// <inheritdoc cref="WithTlsDomain(IResourceBuilder{TraefikResource}, string, string[])"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="main">The main domain of the certificate. It can reference parameters, for example <c>ReferenceExpression.Create($"{domain}")</c>.</param>
    /// <param name="sans">The subject alternative names, for example <c>ReferenceExpression.Create($"*.{domain}")</c>.</param>
    /// <exception cref="ArgumentException">A name contains <c>,</c>.</exception>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withTlsDomain export, which accepts strings or reference expressions.")]
    public static IResourceBuilder<TraefikResource> WithTlsDomain(this IResourceBuilder<TraefikResource> builder, ReferenceExpression main, params ReferenceExpression[] sans)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(main);
        ArgumentNullException.ThrowIfNull(sans);

        foreach (var name in sans.Prepend(main))
        {
            ArgumentNullException.ThrowIfNull(name, nameof(sans));

            // Static configuration splits list values on commas.
            if (name.Format.Contains(','))
            {
                throw new ArgumentException($"The TLS domain '{name.ValueExpression}' contains ','.", nameof(sans));
            }
        }

        builder.Resource.TlsDomains.Add(new TraefikTlsDomain(main, sans));

        return builder;
    }

    /// <summary>
    /// Sets the ACME directory URL that the certificate resolver configured with <c>WithLetsEncrypt</c> requests
    /// certificates from.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="caServer">The ACME directory URL, for example <c>https://acme-staging-v02.api.letsencrypt.org/directory</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="caServer"/> isn't an absolute HTTP or HTTPS URL.</exception>
    /// <remarks>
    /// Use it instead of the <c>staging</c> argument of <c>WithLetsEncrypt</c> to choose the directory per
    /// deployment, or to use another ACME certificate authority. Like the other ACME settings it only applies at
    /// publish time. See
    /// <see href="https://doc.traefik.io/traefik/reference/install-configuration/tls/certificate-resolvers/acme/"/>.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withAcmeCaServer export, which accepts a string or a parameter.")]
    public static IResourceBuilder<TraefikResource> WithAcmeCaServer(this IResourceBuilder<TraefikResource> builder, string caServer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(caServer);

        if (!Uri.TryCreate(caServer, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException($"The ACME directory URL '{caServer}' must be an absolute HTTP or HTTPS URL.", nameof(caServer));
        }

        builder.Resource.AcmeCaServer = ReferenceExpression.Create($"{caServer}");

        return builder;
    }

    /// <inheritdoc cref="WithAcmeCaServer(IResourceBuilder{TraefikResource}, string)"/>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="caServer">A parameter holding the ACME directory URL, so it can differ per deployment.</param>
    /// <example>
    /// Default to the production directory and switch a test deployment to staging in its <c>.env</c> file:
    /// <code lang="csharp">
    /// var caServer = builder.AddParameter("acme-ca-server", "https://acme-v02.api.letsencrypt.org/directory");
    ///
    /// var traefik = builder.AddTraefik("traefik")
    ///     .WithLetsEncrypt(email)
    ///     .WithAcmeCaServer(caServer);
    /// </code>
    /// </example>
    [AspireExportIgnore(Reason = "Polyglot AppHosts use the internal withAcmeCaServer export, which accepts a string or a parameter.")]
    public static IResourceBuilder<TraefikResource> WithAcmeCaServer(this IResourceBuilder<TraefikResource> builder, IResourceBuilder<ParameterResource> caServer)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(caServer);

        builder.Resource.AcmeCaServer = ReferenceExpression.Create($"{caServer}");

        return builder;
    }

    /// <summary>
    /// Redirects HTTP requests on the <c>web</c> entry point to HTTPS on the <c>websecure</c> entry point.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// Requires TLS to be configured, for example with <c>WithLetsEncrypt</c>. In run mode the redirect targets the
    /// host port of <c>websecure</c>, so it works with randomly assigned ports.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<TraefikResource> WithHttpsRedirect(this IResourceBuilder<TraefikResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.HttpsRedirectEnabled = true;

        return builder;
    }

    /// <summary>
    /// Enables HTTPS with certificates issued by Let's Encrypt.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="email">The email address registered with Let's Encrypt, used for expiry notices. A string, a parameter, or a reference expression.</param>
    /// <param name="resolverName">The name of the Traefik certificate resolver.</param>
    /// <param name="challenge">The ACME challenge used to prove control of the domains.</param>
    /// <param name="staging">Whether to use the Let's Encrypt staging environment, which has higher rate limits but issues untrusted certificates.</param>
    /// <returns>A reference to the Traefik resource builder.</returns>
    /// <remarks>
    /// Adds the <c>websecure</c> entry point and makes routes use it with TLS. Certificates are only requested at publish
    /// time; in run mode Traefik serves the HTTPS certificate from Aspire.
    /// </remarks>
    [AspireExport("withLetsEncrypt")]
    internal static IResourceBuilder<TraefikResource> WithLetsEncryptPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>), typeof(ReferenceExpression))] object email,
        string resolverName = DefaultLetsEncryptResolverName,
        TraefikAcmeChallenge challenge = TraefikAcmeChallenge.TlsAlpn,
        bool staging = false)
    {
        return email switch
        {
            string value => builder.WithLetsEncrypt(value, resolverName, challenge, staging),
            IResourceBuilder<ParameterResource> parameter => builder.WithLetsEncrypt(parameter, resolverName, challenge, staging),
            ReferenceExpression expression => builder.WithLetsEncrypt(expression, resolverName, challenge, staging),
            _ => throw new ArgumentException($"Unsupported type '{email?.GetType().Name}'. Expected a string, a parameter, or a reference expression.", nameof(email)),
        };
    }

    /// <summary>
    /// Requests a certificate for a domain and its alternative names on the <c>websecure</c> entry point, for example a
    /// wildcard certificate.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="main">The main domain of the certificate, for example <c>example.com</c>. It can reference parameters.</param>
    /// <param name="sans">The subject alternative names as reference expressions, for example <c>*.example.com</c>.</param>
    /// <returns>A reference to the Traefik resource builder.</returns>
    /// <remarks>
    /// A wildcard requires the DNS-01 challenge on the default resolver. The domains only apply at publish time.
    /// </remarks>
    // The alternative names are reference expressions only: ATS has no arrays of unions, and a union on an `object[]`
    // parameter is exported as a single value.
    [AspireExport("withTlsDomain")]
    internal static IResourceBuilder<TraefikResource> WithTlsDomainPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(ReferenceExpression))] object main,
        ReferenceExpression[]? sans = null)
    {
        var mainExpression = main switch
        {
            string text => ReferenceExpression.Create($"{text}"),
            ReferenceExpression expression => expression,
            _ => throw new ArgumentException($"Unsupported type '{main?.GetType().Name}'. Expected a string or a reference expression.", nameof(main)),
        };

        return builder.WithTlsDomain(mainExpression, sans ?? []);
    }

    /// <summary>
    /// Sets the ACME directory URL that the certificate resolver configured with <c>withLetsEncrypt</c> requests
    /// certificates from.
    /// </summary>
    /// <param name="builder">The Traefik resource builder.</param>
    /// <param name="caServer">The ACME directory URL, as a string or a parameter, for example <c>https://acme-staging-v02.api.letsencrypt.org/directory</c>.</param>
    /// <returns>A reference to the Traefik resource builder.</returns>
    [AspireExport("withAcmeCaServer")]
    internal static IResourceBuilder<TraefikResource> WithAcmeCaServerPolyglot(
        this IResourceBuilder<TraefikResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object caServer)
    {
        return caServer switch
        {
            string value => builder.WithAcmeCaServer(value),
            IResourceBuilder<ParameterResource> parameter => builder.WithAcmeCaServer(parameter),
            _ => throw new ArgumentException($"Unsupported type '{caServer?.GetType().Name}'. Expected a string or a parameter.", nameof(caServer)),
        };
    }

    private static IResourceBuilder<TraefikResource> ConfigureLetsEncrypt(IResourceBuilder<TraefikResource> builder, ReferenceExpression email, string resolverName, TraefikAcmeChallenge challenge, bool staging)
    {
        TraefikNames.ThrowIfInvalidName(resolverName, "certificate resolver name", nameof(resolverName));

        var resource = builder.Resource;
        var isPublishMode = builder.ApplicationBuilder.ExecutionContext.IsPublishMode;
        var hadWebSecure = resource.HasWebSecureEntryPoint;

        var resolver = resource.FindAcmeResolver(resolverName);
        if (resolver is null)
        {
            resolver = new TraefikAcmeResolver(resolverName, email);
            resource.AcmeResolvers.Add(resolver);
        }

        resolver.Email = email;
        resolver.Challenge = challenge;
        resolver.Staging = staging;

        if (!hadWebSecure)
        {
            builder.WithEndpoint(
                port: resource.HostHttpsPort ?? (isPublishMode ? TraefikResource.WebSecureTargetPort : null),
                targetPort: TraefikResource.WebSecureTargetPort,
                scheme: "https",
                name: TraefikResource.WebSecureEntryPointName,
                isExternal: true);

            if (isPublishMode)
            {
                // Keep issued certificates across restarts; Let's Encrypt rate-limits repeated issuance.
                builder.WithVolume(VolumeNameGenerator.Generate(builder, "letsencrypt"), TraefikResource.AcmeStorageDirectory);
            }
        }

        return builder;
    }

    // lego provider names, for example `cloudflare`, `route53`, `azuredns`, or `ovh`.
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*$")]
    private static partial Regex DnsProviderRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableRegex();
}
