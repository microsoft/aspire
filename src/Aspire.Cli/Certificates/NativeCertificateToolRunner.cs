// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Certificate tool runner that uses the native CertificateManager directly (no subprocess needed).
/// </summary>
internal sealed class NativeCertificateToolRunner(CertificateManager certificateManager) : ICertificateToolRunner
{
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
