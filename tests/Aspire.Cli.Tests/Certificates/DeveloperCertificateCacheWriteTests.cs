// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Certificates;

public class DeveloperCertificateCacheWriteTests
{
    [Fact]
    public void WriteAspireCacheFromDiskPfx_WritesCacheFiles()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Only supported on Linux in CI.");

        using var homeDirectory = new TestTempDirectory();
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["HOME"] = homeDirectory.Path;
        options.StartInfo.Environment["USERPROFILE"] = homeDirectory.Path;

        RemoteExecutor.Invoke(static homePath =>
        {
            var userHttpsCertificateLocation = Path.Combine(homePath, ".aspnet", "dev-certs", "https");
            var aspireDevCertsCacheDirectory = Path.Combine(homePath, ".aspire", "dev-certs", "https");
            Directory.CreateDirectory(userHttpsCertificateLocation);

            var certificateManager = new MacOSCertificateManager(NullLogger.Instance);
            using var certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(30));

            var onDiskPfxPath = Path.Combine(userHttpsCertificateLocation, $"aspnetcore-localhost-{certificate.Thumbprint}.pfx");
            File.WriteAllBytes(onDiskPfxPath, certificate.Export(X509ContentType.Pfx));

            InvokeWriteAspireCacheFromDiskPfx(certificateManager, onDiskPfxPath, certificate);

            var aspireLookup = GetAspireLookup(certificate);
            var cachedPfxPath = Path.Combine(aspireDevCertsCacheDirectory, $"{aspireLookup}.pfx");
            var cachedKeyPath = Path.Combine(aspireDevCertsCacheDirectory, $"{aspireLookup}.key");

            Assert.True(File.Exists(cachedPfxPath));
            Assert.True(File.Exists(cachedKeyPath));
            using var cachedPfxCertificate = X509CertificateLoader.LoadPkcs12FromFile(cachedPfxPath, password: null);
            Assert.Equal(certificate.Thumbprint, cachedPfxCertificate.Thumbprint);
            Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(cachedKeyPath), StringComparison.Ordinal);
        }, homeDirectory.Path, options).Dispose();
    }

    private static string GetAspireLookup(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(certificate.Thumbprint)));

    private static void InvokeWriteAspireCacheFromDiskPfx(
        MacOSCertificateManager certificateManager,
        string onDiskPfxPath,
        X509Certificate2 certificate)
    {
        var writeAspireCacheMethod = typeof(MacOSCertificateManager).GetMethod(
            "WriteAspireCacheFromDiskPfx",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        writeAspireCacheMethod.Invoke(certificateManager, [onDiskPfxPath, certificate]);
    }
}
