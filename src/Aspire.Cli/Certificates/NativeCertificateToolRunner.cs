// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Certificate tool runner that uses the native CertificateManager directly (no subprocess needed).
/// </summary>
internal sealed class NativeCertificateToolRunner(
    CertificateManager certificateManager,
    IEnvironment environment,
    ILogger<NativeCertificateToolRunner> logger) : ICertificateToolRunner
{
    public CertificateTrustResult CheckHttpCertificate(CancellationToken cancellationToken = default)
    {
        var availableCertificates = certificateManager.ListCertificates(
            StoreName.My, StoreLocation.CurrentUser, isValid: true);

        try
        {
            var now = DateTimeOffset.Now;
            var certInfos = availableCertificates.Select(cert =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var status = certificateManager.CheckCertificateState(cert);
                CertificateManager.TrustLevel trustLevel;
                if (!status.Success)
                {
                    trustLevel = CertificateManager.TrustLevel.None;
                }
                else
                {
                    trustLevel = certificateManager is UnixCertificateManager unixCertificateManager
                        ? unixCertificateManager.GetTrustLevel(cert, cancellationToken)
                        : certificateManager.GetTrustLevel(cert);
                }

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

    public EnsureCertificateResult EnsureHttpCertificateExists()
    {
        var now = DateTimeOffset.Now;
        return certificateManager.EnsureAspNetCoreHttpsDevelopmentCertificate(
            now, now.Add(TimeSpan.FromDays(365)),
            trust: false,
            isInteractive: false);
    }

    public EnsureCertificateResult TrustHttpCertificate()
    {
        if (environment.IsLinux())
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

    public string? ExportDevCertificatePublicPem(string outputDirectory, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Searching for a trusted ASP.NET Core development certificate to export");

        var availableCertificates = certificateManager.ListCertificates(
            StoreName.My, StoreLocation.CurrentUser, isValid: false, requireExportable: false);

        try
        {
            var now = DateTimeOffset.Now;
            var validCertificates = availableCertificates
                .Where(c => c.HasPrivateKey && c.NotBefore <= now && now <= c.NotAfter)
                .ToList();

            if (validCertificates.Any(c => c.HasSubjectKeyIdentifier()))
            {
                validCertificates = validCertificates.Where(c => c.HasSubjectKeyIdentifier()).ToList();
            }

            var certificate = validCertificates
                .GroupBy(c => c.Extensions.OfType<X509SubjectKeyIdentifierExtension>().FirstOrDefault()?.SubjectKeyIdentifier)
                .SelectMany(group => group.OrderByVersion().Take(1))
                .OrderByVersion()
                .GetTrustedCertificates(cancellationToken)
                .FirstOrDefault();

            if (certificate is null)
            {
                logger.LogDebug("No trusted ASP.NET Core development certificate was available to export");
                return null;
            }

            logger.LogDebug(
                "Selected ASP.NET Core development certificate {Thumbprint} for public PEM export",
                certificate.Thumbprint);

            return GetOrCreateCertificateCacheFile(certificate, outputDirectory);
        }
        finally
        {
            CertificateManager.DisposeCertificates(availableCertificates);
        }
    }

    internal string GetOrCreateCertificateCacheFile(X509Certificate2 certificate, string outputDirectory)
    {
        var pemContents = Encoding.UTF8.GetBytes(certificate.ExportCertificatePem());
        var hash = Convert.ToHexString(XxHash128.Hash(pemContents)).ToLowerInvariant();
        var outputPath = Path.Combine(outputDirectory, $"aspire-dev-cert-{hash}.pem");

        if (File.Exists(outputPath))
        {
            logger.LogDebug("Reusing cached development certificate PEM at {Path}", outputPath);
            return outputPath;
        }

        logger.LogDebug("Writing development certificate PEM to cache at {Path}", outputPath);
        CertificateCacheWriter.WriteFile(outputPath, pemContents, logger);
        return outputPath;
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
