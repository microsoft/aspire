// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Certificate tool runner that uses the native CertificateManager directly (no subprocess needed).
/// </summary>
internal sealed class NativeCertificateToolRunner(CertificateManager certificateManager, Func<bool>? isLinux = null) : ICertificateToolRunner
{
    private readonly Func<bool> _isLinux = isLinux ?? OperatingSystem.IsLinux;

    public CertificateTrustResult CheckHttpCertificate()
    {
        var availableCertificates = certificateManager.ListCertificates(
            StoreName.My, StoreLocation.CurrentUser, isValid: true);

        try
        {
            var now = DateTimeOffset.Now;
            var certInfos = availableCertificates.Select(cert =>
            {
                var status = certificateManager.CheckCertificateState(cert);
                var trustLevel = status.Success
                    ? certificateManager.GetTrustLevel(cert)
                    : CertificateManager.TrustLevel.None;

                return new DevCertInfo
                {
                    Thumbprint = cert.Thumbprint,
                    Subject = cert.Subject,
                    SubjectAlternativeNames = GetSanExtension(cert),
                    Version = CertificateManager.GetCertificateVersion(cert),
                    ValidityNotBefore = cert.NotBefore,
                    ValidityNotAfter = cert.NotAfter,
                    IsHttpsDevelopmentCertificate = CertificateManager.IsHttpsDevelopmentCertificate(cert),
                    IsExportable = certificateManager.IsExportable(cert),
                    TrustLevel = trustLevel
                };
            }).ToList();

            var validCerts = certInfos
                .Where(c => c.IsHttpsDevelopmentCertificate && c.ValidityNotBefore <= now && now <= c.ValidityNotAfter)
                .OrderByDescending(c => c.Version)
                .ToList();

            var highestVersionedCert = validCerts.FirstOrDefault();

            return new CertificateTrustResult
            {
                HasCertificates = validCerts.Count > 0,
                TrustLevel = highestVersionedCert?.TrustLevel,
                Certificates = certInfos
            };
        }
        finally
        {
            CertificateManager.DisposeCertificates(availableCertificates);
        }
    }

    public EnsureCertificateResult TrustHttpCertificate()
    {
        if (_isLinux())
        {
            var availableCertificates = certificateManager.ListCertificates(
                StoreName.My, StoreLocation.CurrentUser, isValid: true);

            try
            {
                return TrustHttpCertificateOnLinux(availableCertificates, DateTimeOffset.Now);
            }
            finally
            {
                CertificateManager.DisposeCertificates(availableCertificates);
            }
        }

        var now = DateTimeOffset.Now;
        return certificateManager.EnsureAspNetCoreHttpsDevelopmentCertificate(
            now, now.Add(TimeSpan.FromDays(365)),
            trust: true);
    }

    public EnsureCertificateResult EnsureHttpCertificateExists()
    {
        var now = DateTimeOffset.Now;
        return certificateManager.EnsureAspNetCoreHttpsDevelopmentCertificate(
            now,
            now.Add(TimeSpan.FromDays(365)),
            trust: false,
            isInteractive: false);
    }

    internal EnsureCertificateResult TrustHttpCertificateOnLinux(IEnumerable<X509Certificate2> availableCertificates, DateTimeOffset now)
    {
        X509Certificate2? certificate = null;
        var createdCertificate = false;

        try
        {
            certificate = availableCertificates
                .Where(c => c.Subject == certificateManager.Subject && CertificateManager.GetCertificateVersion(c) >= CertificateManager.CurrentAspNetCoreCertificateVersion)
                .OrderByDescending(CertificateManager.GetCertificateVersion)
                .FirstOrDefault();

            var successResult = EnsureCertificateResult.ExistingHttpsCertificateTrusted;

            if (certificate is null)
            {
                try
                {
                    certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(now, now.Add(TimeSpan.FromDays(365)));
                    createdCertificate = true;
                }
                catch
                {
                    return EnsureCertificateResult.ErrorCreatingTheCertificate;
                }

                try
                {
                    certificate = certificateManager.SaveCertificate(certificate);
                }
                catch
                {
                    return EnsureCertificateResult.ErrorSavingTheCertificateIntoTheCurrentUserPersonalStore;
                }

                successResult = EnsureCertificateResult.NewHttpsCertificateTrusted;
            }

            try
            {
                return certificateManager.TrustCertificate(certificate) switch
                {
                    CertificateManager.TrustLevel.Full => successResult,
                    CertificateManager.TrustLevel.Partial => EnsureCertificateResult.PartiallyFailedToTrustTheCertificate,
                    _ => EnsureCertificateResult.FailedToTrustTheCertificate
                };
            }
            catch (CertificateManager.UserCancelledTrustException)
            {
                return EnsureCertificateResult.UserCancelledTrustStep;
            }
            catch
            {
                return EnsureCertificateResult.FailedToTrustTheCertificate;
            }
        }
        finally
        {
            if (createdCertificate)
            {
                certificate?.Dispose();
            }
        }
    }

    /// Win32 ERROR_CANCELLED (0x4C7) encoded as an HRESULT (0x800704C7).
    /// Thrown when the user dismisses the Windows certificate-store security dialog.
    private const int UserCancelledHResult = unchecked((int)0x800704C7);
    private const int UserCancelledErrorCode = 1223;

    public CertificateCleanResult CleanHttpCertificate()
    {
        try
        {
            certificateManager.CleanupHttpsCertificates();
            return new CertificateCleanResult { Success = true };
        }
        catch (CryptographicException ex) when (ex.HResult == UserCancelledHResult || ex.HResult == UserCancelledErrorCode)
        {
            return new CertificateCleanResult { Success = false, WasCancelled = true, ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new CertificateCleanResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Well-known location on disk where dev-cert key material is cached,
    /// matching the convention used by <c>DeveloperCertificateService</c> in
    /// <c>Aspire.Hosting</c>.
    /// </summary>
    private static readonly string s_cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aspire", "dev-certs", "https");

    /// <summary>
    /// On-disk location where the <see cref="MacOSCertificateManager"/> stores dev-cert PFX
    /// files during certificate creation. These files contain the private key and can be read
    /// without triggering a macOS Keychain access prompt.
    /// </summary>
    private static readonly string s_macOSCertificateLocation = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".aspnet", "dev-certs", "https");

    /// <inheritdoc />
    public Task PreExportKeyMaterialAsync(CancellationToken cancellationToken)
    {
        // The primary export path is in MacOSCertificateManager.SaveCertificateToAspireCache,
        // which writes to the Aspire hosting cache during cert creation/correction using the
        // in-memory certificate (no Keychain access prompt). This method is a fallback for
        // certificates that were created before SaveCertificateToAspireCache existed.
        if (!OperatingSystem.IsMacOS())
        {
            return Task.CompletedTask;
        }

        var availableCertificates = certificateManager.ListCertificates(
            StoreName.My, StoreLocation.CurrentUser, isValid: true);

        try
        {
            var now = DateTimeOffset.Now;

            var certificate = availableCertificates
                .Where(CertificateManager.IsHttpsDevelopmentCertificate)
                .Where(c => c.NotBefore <= now && now <= c.NotAfter)
                .Where(c => c.HasPrivateKey)
                .OrderByDescending(CertificateManager.GetCertificateVersion)
                .FirstOrDefault();

            if (certificate is null)
            {
                return Task.CompletedTask;
            }

            var lookup = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(certificate.Thumbprint)));

            var keyPath = Path.Join(s_cacheDirectory, $"{lookup}.key");
            var pfxPath = Path.Join(s_cacheDirectory, $"{lookup}.pfx");

            // Skip if both cache entries already exist and are non-empty.
            if (File.Exists(keyPath) && new FileInfo(keyPath).Length > 0
                && File.Exists(pfxPath) && new FileInfo(pfxPath).Length > 0)
            {
                return Task.CompletedTask;
            }

            // Load the certificate from the on-disk PFX file that MacOSCertificateManager
            // writes during certificate creation. Reading from disk avoids triggering a
            // macOS Keychain access prompt.
            var onDiskPfxPath = Path.Combine(s_macOSCertificateLocation,
                $"aspnetcore-localhost-{certificate.Thumbprint}.pfx");

            if (!File.Exists(onDiskPfxPath))
            {
                return Task.CompletedTask;
            }

            using var diskCert = X509CertificateLoader.LoadPkcs12FromFile(onDiskPfxPath, password: null);
            using var privateKey = diskCert.GetRSAPrivateKey();
            if (privateKey is null)
            {
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(s_cacheDirectory,
                UnixFileMode.UserExecute | UnixFileMode.UserWrite | UnixFileMode.UserRead);

            if (!File.Exists(keyPath) || new FileInfo(keyPath).Length == 0)
            {
                var keyBytes = privateKey.ExportEncryptedPkcs8PrivateKey(
                    string.Empty,
                    new PbeParameters(
                        PbeEncryptionAlgorithm.Aes256Cbc,
                        HashAlgorithmName.SHA256,
                        iterationCount: 1));
                var pemKey = PemEncoding.Write("ENCRYPTED PRIVATE KEY", keyBytes);

                using var tempKey = RSA.Create();
                tempKey.ImportFromEncryptedPem(pemKey, string.Empty);
                Array.Clear(keyBytes, 0, keyBytes.Length);
                Array.Clear(pemKey, 0, pemKey.Length);
                keyBytes = tempKey.ExportPkcs8PrivateKey();
                pemKey = PemEncoding.Write("PRIVATE KEY", keyBytes);

                File.WriteAllText(keyPath, new string(pemKey));

                Array.Clear(keyBytes, 0, keyBytes.Length);
                Array.Clear(pemKey, 0, pemKey.Length);
            }

            if (!File.Exists(pfxPath) || new FileInfo(pfxPath).Length == 0)
            {
                var pfxBytes = diskCert.Export(X509ContentType.Pfx);
                File.WriteAllBytes(pfxPath, pfxBytes);
                Array.Clear(pfxBytes, 0, pfxBytes.Length);
            }
        }
        catch
        {
            // Best effort — the app host will fall back to accessing the keychain directly.
        }
        finally
        {
            CertificateManager.DisposeCertificates(availableCertificates);
        }

        return Task.CompletedTask;
    }

    private static string[]? GetSanExtension(X509Certificate2 cert)
    {
        var dnsNames = new List<string>();
        foreach (var extension in cert.Extensions)
        {
            if (extension is X509SubjectAlternativeNameExtension sanExtension)
            {
                foreach (var dns in sanExtension.EnumerateDnsNames())
                {
                    dnsNames.Add(dns);
                }
            }
        }
        return dnsNames.Count > 0 ? dnsNames.ToArray() : null;
    }
}
