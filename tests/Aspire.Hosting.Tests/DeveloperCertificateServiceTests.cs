// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Aspire.Hosting.Tests;

public class DeveloperCertificateServiceTests
{
    [Fact]
    public async Task GetKeyMaterialAsync_WithRsaUserCertificate_ReturnsKeyAndPfx()
    {
        using var cert = CreateRsaSelfSignedCertificate();

        var (keyPem, pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
            cert,
            password: null,
            needKeyPem: true,
            needPfx: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(keyPem);
        Assert.NotNull(pfxBytes);
        Assert.Contains("PRIVATE KEY", new string(keyPem!));
    }

    [Fact]
    public async Task GetKeyMaterialAsync_WithEcdsaUserCertificate_ReturnsKeyAndPfx()
    {
        using var cert = CreateEcdsaSelfSignedCertificate();

        var (keyPem, pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
            cert,
            password: null,
            needKeyPem: true,
            needPfx: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(keyPem);
        Assert.NotNull(pfxBytes);
        Assert.Contains("PRIVATE KEY", new string(keyPem!));
    }

    [Fact]
    public async Task GetKeyMaterialAsync_WithEcdsaUserCertificate_WithPassword_ProducesEncryptedPem()
    {
        using var cert = CreateEcdsaSelfSignedCertificate();

        var (keyPem, pfxBytes) = await DeveloperCertificateService.GetKeyMaterialAsync(
            cert,
            password: "test-password",
            needKeyPem: true,
            needPfx: true,
            TestContext.Current.CancellationToken);

        Assert.NotNull(keyPem);
        Assert.NotNull(pfxBytes);
        Assert.Contains("ENCRYPTED PRIVATE KEY", new string(keyPem!));
    }

    [Fact]
    public async Task GetKeyMaterialAsync_WithCertificateWithoutPrivateKey_Throws()
    {
        using var cert = CreateEcdsaSelfSignedCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(cert.Export(X509ContentType.Cert));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DeveloperCertificateService.GetKeyMaterialAsync(
                publicOnly,
                password: null,
                needKeyPem: true,
                needPfx: true,
                TestContext.Current.CancellationToken));
    }

    private static X509Certificate2 CreateRsaSelfSignedCertificate()
    {
        var subject = new X500DistinguishedName($"CN=aspire-test-{Guid.NewGuid():N}");

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CreateEcdsaSelfSignedCertificate()
    {
        var subject = new X500DistinguishedName($"CN=aspire-test-{Guid.NewGuid():N}");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subject, ecdsa, HashAlgorithmName.SHA256);
        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Round-trip through PFX so the returned cert is detached from the in-memory ECDsa instance.
        var pfxBytes = selfSigned.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }
}
