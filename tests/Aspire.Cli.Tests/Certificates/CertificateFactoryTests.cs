// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Certificates;

public class CertificateFactoryTests
{
    [Fact]
    public void CreateSelfSignedCertificate_PreservesSubjectValidityAndSigningKey()
    {
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var notAfter = notBefore.AddDays(2);
        using var certificate = CertificateFactory.CreateSelfSignedCertificate(
            new X500DistinguishedName("CN=Aspire Entra test"),
            [new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true)],
            notBefore,
            notAfter);

        Assert.Equal("CN=Aspire Entra test", certificate.Subject);
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.Equal(notBefore.UtcDateTime, certificate.NotBefore.ToUniversalTime());
        Assert.Equal(notAfter.UtcDateTime, certificate.NotAfter.ToUniversalTime());
        Assert.Equal("1.2.840.113549.1.1.11", certificate.SignatureAlgorithm.Value);
        Assert.Collection(certificate.Extensions,
            extension =>
            {
                var usage = Assert.IsType<X509KeyUsageExtension>(extension);
                Assert.Equal(X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);
                Assert.True(usage.Critical);
            },
            extension => Assert.IsType<X509SubjectKeyIdentifierExtension>(extension),
            extension => Assert.IsType<X509AuthorityKeyIdentifierExtension>(extension));

        using var privateKey = certificate.GetRSAPrivateKey();
        using var publicKey = certificate.GetRSAPublicKey();
        Assert.NotNull(privateKey);
        Assert.NotNull(publicKey);
        Assert.Equal(2048, privateKey.KeySize);
        var signature = privateKey.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Assert.True(publicKey.VerifyData(new byte[] { 1, 2, 3 }, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CreateSelfSignedCertificate_PreservesSuppliedKeyIdentifiers(bool includeSubjectKeyIdentifier, bool includeAuthorityKeyIdentifier)
    {
        var subjectKeyIdentifier = new X509SubjectKeyIdentifierExtension("01020304", critical: false);
        var authorityKeyIdentifier = X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(subjectKeyIdentifier);
        var extensions = new List<X509Extension>();
        if (includeSubjectKeyIdentifier)
        {
            extensions.Add(subjectKeyIdentifier);
        }
        if (includeAuthorityKeyIdentifier)
        {
            extensions.Add(authorityKeyIdentifier);
        }

        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var certificate = CertificateFactory.CreateSelfSignedCertificate(
            new X500DistinguishedName("CN=custom-identifiers"),
            extensions,
            notBefore,
            notBefore.AddDays(2));

        if (extensions.Count == 0)
        {
            var generatedSubject = Assert.IsType<X509SubjectKeyIdentifierExtension>(certificate.Extensions[0]);
            var generatedAuthority = Assert.IsType<X509AuthorityKeyIdentifierExtension>(certificate.Extensions[1]);
            Assert.Equal(2, certificate.Extensions.Count);
            Assert.Equal(
                X509AuthorityKeyIdentifierExtension.CreateFromSubjectKeyIdentifier(generatedSubject).RawData,
                generatedAuthority.RawData);
        }
        else
        {
            Assert.Equal(extensions.Count, certificate.Extensions.Count);
            for (var i = 0; i < extensions.Count; i++)
            {
                Assert.Equal(extensions[i].Oid?.Value, certificate.Extensions[i].Oid?.Value);
                Assert.Equal(extensions[i].RawData, certificate.Extensions[i].RawData);
            }
        }
    }

    [Fact]
    public void CreateAspNetCoreHttpsDevelopmentCertificate_PreservesHttpsExtensions()
    {
        var manager = CertificateManager.Create(NullLogger.Instance, TestEnvironment.CreateLinux(), nssDbOverride: null);
        var notBefore = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var certificate = manager.CreateAspNetCoreHttpsDevelopmentCertificate(notBefore, notBefore.AddDays(2));

        Assert.Equal("CN=localhost", certificate.Subject);
        Assert.Collection(certificate.Extensions,
            extension => Assert.IsType<X509BasicConstraintsExtension>(extension),
            extension =>
            {
                var usage = Assert.IsType<X509KeyUsageExtension>(extension);
                Assert.Equal(X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);
            },
            extension =>
            {
                var usage = Assert.IsType<X509EnhancedKeyUsageExtension>(extension);
                Assert.Equal("1.3.6.1.5.5.7.3.1", Assert.Single(usage.EnhancedKeyUsages.Cast<Oid>()).Value);
            },
            extension =>
            {
                var names = Assert.IsType<X509SubjectAlternativeNameExtension>(extension);
                Assert.Equal(
                    ["localhost", "*.dev.localhost", "*.dev.internal", "host.docker.internal", "host.containers.internal"],
                    names.EnumerateDnsNames());
                Assert.Equal(["127.0.0.1", "::1"], names.EnumerateIPAddresses().Select(address => address.ToString()));
            },
            extension =>
            {
                Assert.Equal(CertificateManager.AspNetHttpsOid, extension.Oid?.Value);
                Assert.Equal(new byte[] { CertificateManager.CurrentAspNetCoreCertificateVersion }, extension.RawData);
            },
            extension => Assert.IsType<X509SubjectKeyIdentifierExtension>(extension),
            extension => Assert.IsType<X509AuthorityKeyIdentifierExtension>(extension));
    }
}
