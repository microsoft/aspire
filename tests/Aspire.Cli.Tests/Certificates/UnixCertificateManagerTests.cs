// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Certificates;

public class UnixCertificateManagerTests
{
    [Fact]
    public void GetTrustLevel_WithCorruptOpenSslCertificate_DoesNotThrow()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "OpenSSL certificate directory trust is only exercised on Linux.");

        var openSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(openSslDirectory.FullName, "missing-tools"),
                ["SSL_CERT_DIR"] = openSslDirectory.FullName,
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = Path.Combine(openSslDirectory.FullName, "missing-nss-db")
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));
            var certificatePath = Path.Combine(openSslDirectory.FullName, $"aspnetcore-localhost-{certificate.Thumbprint}.pem");
            File.WriteAllText(certificatePath, "not a certificate");

            var exception = Record.Exception(() => manager.GetTrustLevel(certificate));

            Assert.Null(exception);
        }
        finally
        {
            openSslDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void RemoveCertificate_WithMissingCertUtilAndNssDbs_SkipsNssCleanup()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "NSS certificate cleanup is only exercised on Linux.");

        var nssDbDirectory = Directory.CreateTempSubdirectory();
        var openSslDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var environment = TestEnvironment.CreateLinux(new Dictionary<string, string?>
            {
                ["PATH"] = Path.Combine(nssDbDirectory.FullName, "missing-certutil"),
                ["DOTNET_DEV_CERTS_NSSDB_PATHS"] = nssDbDirectory.FullName,
                [CertificateHelpers.DevCertsOpenSslCertDirEnvVar] = openSslDirectory.FullName
            });
            var manager = new UnixCertificateManager(NullLogger.Instance, environment);
            using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(365));

            var exception = Record.Exception(() => manager.RemoveCertificate(certificate, CertificateManager.RemoveLocations.Trusted));

            Assert.Null(exception);
        }
        finally
        {
            nssDbDirectory.Delete(recursive: true);
            openSslDirectory.Delete(recursive: true);
        }
    }
}
