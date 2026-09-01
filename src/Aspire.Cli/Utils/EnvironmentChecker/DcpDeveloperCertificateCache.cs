// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Certificates;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Utils.EnvironmentChecker;

internal static class DcpDeveloperCertificateCache
{
    public static string EnsureDeveloperCertificateCache(CertificateManager certificateManager, X509Certificate2 certificate)
    {
        if (!certificate.IsAspNetCoreDevelopmentCertificate() || string.IsNullOrWhiteSpace(certificate.Thumbprint))
        {
            throw new DcpDeveloperCertificateUnavailableException(DoctorCommandStrings.DcpDeveloperCertificateInvalidForCacheDetails);
        }

        var cacheDirectory = CertificateHelpers.AspireDevCertsHttpsCacheDirectory;
        if (!Path.IsPathFullyQualified(cacheDirectory))
        {
            throw new DcpDeveloperCertificateUnavailableException(DoctorCommandStrings.DcpDeveloperCertificateUserProfileMissingDetails);
        }

        var lookup = CertificateHelpers.GetAspireCertificateHash(certificate);
        var certificatePath = Path.Combine(cacheDirectory, $"{lookup}.crt");
        var keyPath = Path.ChangeExtension(certificatePath, ".key");

        // The public certificate export does not require private key access, so older caches with
        // a key but no certificate can be filled without re-exporting the cached key.
        certificateManager.ExportCertificate(certificate, certificatePath, includePrivateKey: !File.Exists(keyPath), password: null, CertificateKeyExportFormat.Pem);

        return certificatePath;
    }
}
