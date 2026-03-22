#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREFILESYSTEM001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting;

internal class DeveloperCertificateService : IDeveloperCertificateService
{
    private readonly Lazy<ImmutableList<X509Certificate2>> _certificates;
    private readonly Lazy<bool> _supportsContainerTrust;
    private readonly Lazy<bool> _supportsTlsTermination;
    private bool _latestCertificateIsUntrusted;

    public DeveloperCertificateService(ILogger<DeveloperCertificateService> logger, IConfiguration configuration, DistributedApplicationOptions options)
    {
        TrustCertificate = configuration.GetBool(KnownConfigNames.DeveloperCertificateDefaultTrust) ??
            options.TrustDeveloperCertificate ??
            true;

        _certificates = new Lazy<ImmutableList<X509Certificate2>>(() =>
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);

                var now = DateTimeOffset.Now;

                // Get all valid ASP.NET Core development certificates.
                // Use .Where() instead of .Find() to preserve the original keychain-backed certificate
                // instances on macOS. Find() clones certificates which can invalidate keychain handles.
                var validCerts = FindDevCertificates(store, now).ToList();

                // If any certificate has a Subject Key Identifier extension, exclude certificates without it
                if (validCerts.Any(c => c.HasSubjectKeyIdentifier()))
                {
                    validCerts = validCerts.Where(c => c.HasSubjectKeyIdentifier()).ToList();
                }

                // Order by version and expiration date descending to get the most recent, highest version first.
                // OpenSSL will only check the first self-signed certificate in the bundle that matches a given domain,
                // so we want to ensure the certificate that will be used by ASP.NET Core is the first one in the bundle.
                // Match the ordering logic ASP.NET Core uses, including DateTimeOffset.Now for current time: https://github.com/dotnet/aspnetcore/blob/0aefdae365ff9b73b52961acafd227309524ce3c/src/Shared/CertificateGeneration/CertificateManager.cs#L122
                var bestCerts = validCerts
                    .GroupBy(c => c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier)
                    .SelectMany(g => g.OrderByVersion().Take(1))
                    .OrderByVersion()
                    .ToList();

                // Partition into trusted and untrusted using a single X509Chain instance.
                // RevocationMode is set to NoCheck since revocation doesn't apply to self-signed dev certs.
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

                // On Windows, chain.Build() can succeed even when the certificate isn't in the
                // trusted root store. Open the CurrentUser Root store so we can verify membership.
                X509Certificate2Collection? rootCerts = null;
                if (OperatingSystem.IsWindows())
                {
                    using var rootStore = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                    rootStore.Open(OpenFlags.ReadOnly);
                    rootCerts = rootStore.Certificates;
                }

                // Find the dev certs that are trusted
                var trustedCerts = new List<X509Certificate2>();
                foreach (var cert in bestCerts)
                {
                    try
                    {
                        if (!chain.Build(cert))
                        {
                            continue;
                        }

                        // On Windows, also verify the certificate exists in the root store
                        if (rootCerts is not null &&
                            !rootCerts.Any(rc => rc.RawDataMemory.Span.SequenceEqual(cert.RawDataMemory.Span)))
                        {
                            continue;
                        }

                        trustedCerts.Add(cert);
                    }
                    finally
                    {
                        // Reset the chain for the next certificate regardless of branch taken.
                        chain.Reset();
                    }
                }

                // Dispose root store certificates after use
                if (rootCerts is not null)
                {
                    foreach (var rc in rootCerts)
                    {
                        rc.Dispose();
                    }
                }

                // Flag if the newest/highest-version cert is not trusted
                if (bestCerts.Count > 0 &&
                    (trustedCerts.Count == 0 || trustedCerts[0].Thumbprint != bestCerts[0].Thumbprint))
                {
                    _latestCertificateIsUntrusted = true;
                }

                // Release the unused certificates
                foreach (var unusedCert in validCerts.Except(trustedCerts))
                {
                    unusedCert.Dispose();
                }

                if (trustedCerts.Count == 0)
                {
                    return ImmutableList<X509Certificate2>.Empty;
                }

                return trustedCerts.ToImmutableList();
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to load developer certificates from the CurrentUser/My certificate store. Automatic trust of development certificates will not be available. Reason: {Message}", ex.Message);
                return ImmutableList<X509Certificate2>.Empty;
            }
        });

        _supportsContainerTrust = new Lazy<bool>(() =>
        {
            var containerTrustAvailable = Certificates.Any(c => c.GetCertificateVersion() >= X509Certificate2Extensions.MinimumCertificateVersionSupportingContainerTrust);
            logger.LogDebug("Container trust for developer certificates is {Status}.", containerTrustAvailable ? "available" : "not available");
            return containerTrustAvailable;
        });

        _supportsTlsTermination = new Lazy<bool>(() =>
        {
            var supportsTlsTermination = Certificates.Any(c => c.HasPrivateKey);
            logger.LogDebug("Developer certificate HTTPS/TLS termination support: {Available}", supportsTlsTermination);
            return supportsTlsTermination;
        });

        // By default, only use for server authentication if trust is also enabled (and a developer certificate with a private key is available)
        UseForHttps = (configuration.GetBool(KnownConfigNames.DeveloperCertificateDefaultHttpsTermination) ??
            options.DeveloperCertificateDefaultHttpsTerminationEnabled ??
            true) && TrustCertificate && _supportsTlsTermination.Value;
    }

    /// <inheritdoc />
    public ImmutableList<X509Certificate2> Certificates => _certificates.Value;

    /// <inheritdoc />
    public bool SupportsContainerTrust => _supportsContainerTrust.Value;

    /// <inheritdoc />
    public bool TrustCertificate { get; }

    /// <inheritdoc />
    public bool UseForHttps { get; }

    /// <summary>
    /// Gets a value indicating whether a newer ASP.NET Core development certificate was detected
    /// that is not in the trusted set. This is true when the highest-version/most-recent dev cert
    /// is not trusted, even though older trusted certs may exist.
    /// </summary>
    internal bool LatestCertificateIsUntrusted
    {
        get
        {
            _ = _certificates.Value; // Ensure certificates have been evaluated
            return _latestCertificateIsUntrusted;
        }
    }

    /// <summary>
    /// Finds ASP.NET Core development certificates in the store, filtered by date validity and private key presence.
    /// </summary>
    private static IEnumerable<X509Certificate2> FindDevCertificates(X509Store store, DateTimeOffset now)
    {
        return store.Certificates
            .Where(c => c.IsAspNetCoreDevelopmentCertificate())
            .Where(c => c.NotBefore <= now && now <= c.NotAfter)
            .Where(c => c.HasPrivateKey);
    }
}
