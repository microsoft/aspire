#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREFILESYSTEM001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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

    // Well-known location on disk where dev-cert key material is cached on macOS.
    private static readonly string s_macOSUserDevCertificateLocation = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "dev-certs", "https");

    private static readonly SemaphoreSlim s_certificateCacheSemaphore = new(1, 1);

    /// <summary>
    /// Returns the certificate PEM format key and/or PFX bytes for the specified certificate.
    /// On macOS, ASP.NET Core development certificate key material is cached to avoid
    /// triggering repeated keychain prompts.
    /// </summary>
    /// <param name="certificate">The certificate to export key material from.</param>
    /// <param name="password">The password for the private key, or <c>null</c> for unencrypted export.</param>
    /// <param name="needKeyPem">Whether to export the private key in PEM format.</param>
    /// <param name="needPfx">Whether to export the certificate in PFX format.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>A tuple containing the PEM-encoded key and PFX bytes.</returns>
    internal static async Task<(char[]? keyPem, byte[]? pfxBytes)> GetKeyMaterialAsync(
        X509Certificate2 certificate,
        string? password,
        bool needKeyPem,
        bool needPfx,
        CancellationToken cancellationToken)
    {
        var lookup = certificate.Thumbprint;
        if (password is not null)
        {
            lookup += $"-{password}";
        }

        lookup = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lookup)));

        char[]? pemKey = null;
        byte[]? pfxBytes = null;

        // Ensure only one thread at a time is resolving certificates to avoid concurrent cache misses
        // all trying to update the cache at the same time.
        await s_certificateCacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (needKeyPem)
            {
                pemKey = await GetKeyPemAsync(certificate, password, lookup, cancellationToken).ConfigureAwait(false);
            }

            if (needPfx)
            {
                pfxBytes = GetPfxBytes(certificate, password, lookup);
            }
        }
        finally
        {
            s_certificateCacheSemaphore.Release();
        }

        return (pemKey, pfxBytes);
    }

    private static async Task<char[]?> GetKeyPemAsync(
        X509Certificate2 certificate,
        string? password,
        string lookup,
        CancellationToken cancellationToken)
    {
        char[]? pemKey = null;
        var keyFileName = Path.Join(s_macOSUserDevCertificateLocation, $"{lookup}.key");

        if (OperatingSystem.IsMacOS() && certificate.IsAspNetCoreDevelopmentCertificate())
        {
            // On macOS, we cache development certificate key material to avoid triggering repeated
            // keychain prompts when referencing the development certificate key. We don't do this
            // for other OSes or other certificates.
            try
            {
                if (File.Exists(keyFileName))
                {
                    var keyCandidate = File.ReadAllText(keyFileName);

                    if (!string.IsNullOrEmpty(keyCandidate))
                    {
                        pemKey = keyCandidate.ToCharArray();
                    }
                }
            }
            catch
            {
                // Ignore errors and retrieve the key from the certificate
            }
        }

        if (pemKey is null)
        {
            // See: https://github.com/dotnet/aspnetcore/blob/main/src/Shared/CertificateGeneration/CertificateManager.cs
            using var privateKey = certificate.GetRSAPrivateKey();
            if (privateKey is null)
            {
                throw new InvalidOperationException("The certificate does not have an associated RSA private key.");
            }

            var keyBytes = privateKey.ExportEncryptedPkcs8PrivateKey(
                password ?? string.Empty,
                new PbeParameters(
                    PbeEncryptionAlgorithm.Aes256Cbc,
                    HashAlgorithmName.SHA256,
                    iterationCount: password is null ? 1 : 100_000));
            pemKey = PemEncoding.Write("ENCRYPTED PRIVATE KEY", keyBytes);

            if (password is null)
            {
                using var tempKey = RSA.Create();
                tempKey.ImportFromEncryptedPem(pemKey, string.Empty);
                Array.Clear(keyBytes, 0, keyBytes.Length);
                Array.Clear(pemKey, 0, pemKey.Length);
                keyBytes = tempKey.ExportPkcs8PrivateKey();
                pemKey = PemEncoding.Write("PRIVATE KEY", keyBytes);
            }

            Array.Clear(keyBytes, 0, keyBytes.Length);

            if (pemKey is not null && OperatingSystem.IsMacOS() && certificate.IsAspNetCoreDevelopmentCertificate())
            {
                // On macOS, cache the development certificate key material
                try
                {
                    Directory.CreateDirectory(s_macOSUserDevCertificateLocation, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);

                    await File.WriteAllTextAsync(keyFileName, new string(pemKey), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // This is a best effort caching operation
                }
            }
        }

        return pemKey;
    }

    private static byte[]? GetPfxBytes(
        X509Certificate2 certificate,
        string? password,
        string lookup)
    {
        byte[]? pfxBytes = null;
        var pfxFileName = Path.Join(s_macOSUserDevCertificateLocation, $"{lookup}.pfx");

        if (OperatingSystem.IsMacOS() && certificate.IsAspNetCoreDevelopmentCertificate())
        {
            // On macOS, we cache development certificate key material to avoid triggering repeated
            // keychain prompts when referencing the development certificate key.
            try
            {
                if (File.Exists(pfxFileName))
                {
                    var pfxCandidate = File.ReadAllBytes(pfxFileName);
                    if (pfxCandidate.Length > 0)
                    {
                        using var tempCert = new X509Certificate2(pfxCandidate, password);
                        if (tempCert.Thumbprint.Equals(certificate.Thumbprint, StringComparison.Ordinal))
                        {
                            pfxBytes = pfxCandidate;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors and retrieve the key from the certificate
            }
        }

        if (pfxBytes is null)
        {
            pfxBytes = certificate.Export(X509ContentType.Pfx, password);

            if (pfxBytes is not null && OperatingSystem.IsMacOS() && certificate.IsAspNetCoreDevelopmentCertificate())
            {
                try
                {
                    Directory.CreateDirectory(s_macOSUserDevCertificateLocation, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);

                    File.WriteAllBytes(pfxFileName, pfxBytes);
                }
                catch
                {
                    // This is a best effort caching operation
                }
            }
        }

        return pfxBytes;
    }
}
