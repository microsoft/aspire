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
    private static readonly string s_userDevCertificateLocation = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "dev-certs", "https");

    private static readonly SemaphoreSlim s_certificateCacheSemaphore = new(1, 1);

    /// <summary>
    /// Returns the certificate PEM format key and/or PFX bytes for the specified certificate.
    /// On macOS, both outputs are cached as separate files to avoid triggering repeated
    /// keychain prompts. The cache is read without loading any PFX into an X509Certificate2,
    /// because EphemeralKeySet is not supported on macOS with net8.0 and loading without it
    /// imports the private key into the keychain.
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
        if (!needKeyPem && !needPfx)
        {
            return (null, null);
        }

        // This is a user managed certificate, not an asp.net core style dev cert
        if (!certificate.IsAspNetCoreDevelopmentCertificate())
        {
            return ExportFromPrivateKey(certificate, password, needKeyPem, needPfx);
        }

        // For dev certs we prefer reading from cache to avoid repeated keychain access prompts.
        // Ensure only one thread at a time is resolving certificates to avoid concurrent cache misses
        // all trying to update the cache at the same time.
        await s_certificateCacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = EnsureCachedKeyMaterial(certificate, password);
            return (
                needKeyPem ? Encoding.UTF8.GetString(cached.keyBytes).ToCharArray() : null,
                needPfx ? cached.pfxBytes : null);
        }
        finally
        {
            s_certificateCacheSemaphore.Release();
        }
    }

    /// <summary>
    /// Ensures the public certificate (.crt), PFX (.pfx) and PEM private key (.key) cache files
    /// exist for the specified ASP.NET Core developer certificate and returns the paths along with
    /// the certificate thumbprint. Returns <c>(null, null, null)</c> if the supplied certificate is
    /// not a developer certificate, has no thumbprint, or the cache files could not be produced.
    /// </summary>
    internal static async Task<(string? certificateFilePath, string? keyFilePath, string? thumbprint)> GetCachedCertificateFilePathsAsync(
        X509Certificate2 certificate,
        string? password,
        CancellationToken cancellationToken)
    {
        if (!certificate.IsAspNetCoreDevelopmentCertificate() || string.IsNullOrWhiteSpace(certificate.Thumbprint))
        {
            return (null, null, null);
        }

        string certificateFileName;
        string keyFileName;

        await s_certificateCacheSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = EnsureCachedKeyMaterial(certificate, password);
            certificateFileName = cached.certFileName;
            keyFileName = cached.keyFileName;
        }
        finally
        {
            s_certificateCacheSemaphore.Release();
        }

        return File.Exists(certificateFileName) && File.Exists(keyFileName)
            ? (certificateFileName, keyFileName, certificate.Thumbprint)
            : (null, null, null);
    }

    /// <summary>
    /// Ensures the public certificate (.crt), PFX (.pfx) and PEM private key (.key) cache files
    /// exist for the specified certificate, returning their paths along with the cached PFX/key
    /// byte contents. On cache miss the private key is accessed once (which may trigger a keychain
    /// prompt on macOS) to export both private-key formats; on cache hit the bytes are read
    /// directly from disk to avoid importing the PFX into the macOS keychain via
    /// <see cref="X509Certificate2"/>. The public .crt file is written whenever it is missing,
    /// since it can be produced without accessing the private key.
    /// </summary>
    /// <remarks>The caller must hold <see cref="s_certificateCacheSemaphore"/>.</remarks>
    private static (string certFileName, string pfxFileName, string keyFileName, byte[] pfxBytes, byte[] keyBytes) EnsureCachedKeyMaterial(
        X509Certificate2 certificate, string? password)
    {
        var lookup = GetKeyMaterialCacheLookup(certificate, password);
        var certFileName = Path.Join(s_userDevCertificateLocation, $"{lookup}.crt");
        var pfxFileName = Path.Join(s_userDevCertificateLocation, $"{lookup}.pfx");
        var keyFileName = Path.Join(s_userDevCertificateLocation, $"{lookup}.key");

        var cachedPfx = TryReadCacheFile(pfxFileName);
        var cachedKey = TryReadCacheFile(keyFileName);

        byte[] pfxBytes;
        byte[] keyBytes;

        if (cachedPfx is not null && cachedKey is not null)
        {
            pfxBytes = cachedPfx;
            keyBytes = cachedKey;
        }
        else
        {
            // Fall back to accessing the private key directly (triggers a keychain prompt on macOS).
            // Always produce both formats for caching, even if the caller only needs one.
            var result = ExportFromPrivateKey(certificate, password, needKeyPem: true, needPfx: true);
            pfxBytes = result.pfxBytes!;
            keyBytes = Encoding.UTF8.GetBytes(result.keyPem!);
            Array.Clear(result.keyPem!);

            WriteCacheFiles(certFileName, certificate, pfxFileName, pfxBytes, keyFileName, keyBytes);
        }

        // The public certificate cache file can be produced without touching the private key,
        // so refresh it whenever it is missing (including on cache hits from older caches that
        // pre-date this file).
        if (!File.Exists(certFileName))
        {
            WriteCacheFiles(certFileName, certificate, pfxFileName: null, pfxBytes: null, keyFileName: null, keyBytes: null);
        }

        return (certFileName, pfxFileName, keyFileName, pfxBytes, keyBytes);
    }

    private static string GetKeyMaterialCacheLookup(X509Certificate2 certificate, string? password)
    {
        var lookup = certificate.Thumbprint;
        if (password is not null)
        {
            lookup += $"-{password}";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lookup)));
    }

    /// <summary>
    /// Reads a cache file from disk, returning null if it doesn't exist or is empty.
    /// </summary>
    private static byte[]? TryReadCacheFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            return bytes.Length > 0 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Exports PEM key and/or PFX from the certificate using a single private key access.
    /// </summary>
    private static (char[]? keyPem, byte[]? pfxBytes) ExportFromPrivateKey(
        X509Certificate2 certificate, string? password, bool needKeyPem, bool needPfx)
    {
        if (!needKeyPem && !needPfx)
        {
            return (null, null);
        }

        using AsymmetricAlgorithm? privateKey =
            (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey()
            ?? certificate.GetECDsaPrivateKey();

        if (privateKey is null)
        {
            throw new InvalidOperationException("The certificate does not have an associated RSA or ECDSA private key.");
        }

        var keyPem = needKeyPem ? ExportKeyPem(privateKey, password) : null;
        var pfxBytes = needPfx ? certificate.Export(X509ContentType.Pfx, password) : null;

        return (keyPem, pfxBytes);
    }

    /// <summary>
    /// Exports an asymmetric private key in PEM format. Supports RSA and ECDSA keys.
    /// </summary>
    private static char[] ExportKeyPem(AsymmetricAlgorithm privateKey, string? password)
    {
        var keyBytes = privateKey.ExportEncryptedPkcs8PrivateKey(
            password ?? string.Empty,
            new PbeParameters(
                PbeEncryptionAlgorithm.Aes256Cbc,
                HashAlgorithmName.SHA256,
                iterationCount: password is null ? 1 : 100_000));
        var pemKey = PemEncoding.Write("ENCRYPTED PRIVATE KEY", keyBytes);

        if (password is null)
        {
            using AsymmetricAlgorithm tempKey = privateKey switch
            {
                RSA => RSA.Create(),
                ECDsa => ECDsa.Create(),
                _ => throw new InvalidOperationException($"Unsupported private key type: {privateKey.GetType().FullName}.")
            };
            tempKey.ImportFromEncryptedPem(pemKey, string.Empty);
            Array.Clear(keyBytes, 0, keyBytes.Length);
            Array.Clear(pemKey, 0, pemKey.Length);
            keyBytes = tempKey.ExportPkcs8PrivateKey();
            pemKey = PemEncoding.Write("PRIVATE KEY", keyBytes);
        }

        Array.Clear(keyBytes, 0, keyBytes.Length);
        return pemKey;
    }

    /// <summary>
    /// Writes the public certificate (.crt), PFX (.pfx) and PEM private key (.key) cache files.
    /// Any of the file-name / payload pairs may be null to skip writing that file. Best-effort;
    /// failures are silently ignored.
    /// </summary>
    private static void WriteCacheFiles(
        string certFileName,
        X509Certificate2 certificate,
        string? pfxFileName,
        byte[]? pfxBytes,
        string? keyFileName,
        byte[]? keyBytes)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(s_userDevCertificateLocation);
            }
            else
            {
                Directory.CreateDirectory(s_userDevCertificateLocation, UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);
            }

            File.WriteAllText(certFileName, certificate.ExportCertificatePem());

            if (pfxFileName is not null && pfxBytes is not null)
            {
                File.WriteAllBytes(pfxFileName, pfxBytes);
            }

            if (keyFileName is not null && keyBytes is not null)
            {
                File.WriteAllBytes(keyFileName, keyBytes);
            }
        }
        catch
        {
            // Best-effort caching operation.
        }
    }
}
