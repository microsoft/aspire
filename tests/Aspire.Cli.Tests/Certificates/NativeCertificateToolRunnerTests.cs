// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Aspire.Cli.Certificates;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Certificates;

public class NativeCertificateToolRunnerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void TrustHttpCertificateOnLinux_WithNoCurrentCertificate_CreatesAndTrustsCertificate()
    {
        var certificateManager = new TestCertificateManager();
        var runner = CreateRunner(certificateManager);

        var result = runner.TrustHttpCertificateOnLinux([], DateTimeOffset.UtcNow);

        Assert.Equal(EnsureCertificateResult.NewHttpsCertificateTrusted, result);
        Assert.True(certificateManager.SaveCalled);
        Assert.True(certificateManager.TrustCalled);
    }

    [Fact]
    public void TrustHttpCertificateOnLinux_WithExistingCurrentCertificate_TrustsWithoutSaving()
    {
        var certificateManager = new TestCertificateManager();
        using var certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        var runner = CreateRunner(certificateManager);

        var result = runner.TrustHttpCertificateOnLinux([certificate], DateTimeOffset.UtcNow);

        Assert.Equal(EnsureCertificateResult.ExistingHttpsCertificateTrusted, result);
        Assert.False(certificateManager.SaveCalled);
        Assert.True(certificateManager.TrustCalled);
    }

    [Fact]
    public void TrustHttpCertificateOnLinux_WithOnlyOlderCertificate_CreatesCurrentCertificate()
    {
        var currentVersionManager = new TestCertificateManager(CertificateManager.CurrentAspNetCoreCertificateVersion);
        var olderVersionManager = new TestCertificateManager(CertificateManager.CurrentAspNetCoreCertificateVersion - 1);
        using var olderCertificate = olderVersionManager.CreateAspNetCoreHttpsDevelopmentCertificate(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        var runner = CreateRunner(currentVersionManager);

        var result = runner.TrustHttpCertificateOnLinux([olderCertificate], DateTimeOffset.UtcNow);

        Assert.Equal(EnsureCertificateResult.NewHttpsCertificateTrusted, result);
        Assert.True(currentVersionManager.SaveCalled);
        Assert.True(currentVersionManager.TrustCalled);
    }

    [Fact]
    public void ExportDevCertificatePublicPem_WithUntrustedCertificate_ReturnsNull()
    {
        var certificateManager = new TestCertificateManager();
        using var certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        certificateManager.Certificates.Add(certificate.Export(X509ContentType.Pfx));
        var logger = new FakeLogger<NativeCertificateToolRunner>();
        var runner = CreateRunner(certificateManager, logger);
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var result = runner.ExportDevCertificatePublicPem(
            workspace.WorkspaceRoot.FullName,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Message.Contains("No trusted ASP.NET Core development certificate", StringComparison.Ordinal));
    }

    [Fact]
    public void GetOrCreateCertificateCacheFile_CachesPublicPemWithRestrictedPermissions()
    {
        var certificateManager = new TestCertificateManager();
        using var certificate = certificateManager.CreateAspNetCoreHttpsDevelopmentCertificate(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(365));
        var logger = new FakeLogger<NativeCertificateToolRunner>();
        var runner = CreateRunner(certificateManager, logger);
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "dev-certs");
        var pemContents = certificate.ExportCertificatePem();
        var pemBytes = Encoding.UTF8.GetBytes(pemContents);
        var hash = Convert.ToHexString(XxHash128.Hash(pemBytes)).ToLowerInvariant();
        var expectedPath = Path.Combine(outputDirectory, $"aspire-dev-cert-{hash}.pem");

        var firstResult = runner.GetOrCreateCertificateCacheFile(certificate, outputDirectory);
        var lastWriteTime = DateTime.UtcNow.AddHours(-1);
        File.SetLastWriteTimeUtc(firstResult, lastWriteTime);
        lastWriteTime = File.GetLastWriteTimeUtc(firstResult);
        var secondResult = runner.GetOrCreateCertificateCacheFile(certificate, outputDirectory);

        Assert.Equal(expectedPath, firstResult);
        Assert.Equal(firstResult, secondResult);
        Assert.Equal(pemContents, File.ReadAllText(firstResult));
        Assert.Equal(lastWriteTime, File.GetLastWriteTimeUtc(secondResult));
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Message.Contains("Writing development certificate PEM to cache", StringComparison.Ordinal));
        Assert.Contains(
            logger.Collector.GetSnapshot(),
            record => record.Message.Contains("Reusing cached development certificate PEM", StringComparison.Ordinal));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(outputDirectory));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(firstResult));
        }
    }

    private static NativeCertificateToolRunner CreateRunner(
        CertificateManager certificateManager,
        FakeLogger<NativeCertificateToolRunner>? logger = null) =>
        new(
            certificateManager,
            TestEnvironment.CreateLinux(),
            logger ?? new FakeLogger<NativeCertificateToolRunner>());

    private sealed class TestCertificateManager(int version = CertificateManager.CurrentAspNetCoreCertificateVersion)
        : CertificateManager(NullLogger.Instance, CertificateManager.LocalhostHttpsDistinguishedName, version, version)
    {
        public List<byte[]> Certificates { get; } = [];
        public bool SaveCalled { get; private set; }
        public bool TrustCalled { get; private set; }

        protected override void PopulateCertificatesFromStore(
            X509Store store,
            List<X509Certificate2> certificates,
            bool requireExportable)
        {
            certificates.AddRange(
                Certificates.Select(certificate => X509CertificateLoader.LoadPkcs12(
                    certificate,
                    password: null,
                    X509KeyStorageFlags.Exportable)));
        }

        protected override X509Certificate2 SaveCertificateCore(X509Certificate2 certificate, StoreName storeName, StoreLocation storeLocation)
        {
            SaveCalled = true;
            return certificate;
        }

        protected override TrustLevel TrustCertificateCore(X509Certificate2 certificate)
        {
            TrustCalled = true;
            return TrustLevel.Full;
        }

        public override TrustLevel GetTrustLevel(X509Certificate2 certificate) => TrustLevel.None;

        internal override bool IsExportable(X509Certificate2 c) => true;

        protected override void RemoveCertificateFromTrustedRoots(X509Certificate2 certificate)
        {
        }

        protected override IList<X509Certificate2> GetCertificatesToRemove(StoreName storeName, StoreLocation storeLocation) => [];

        protected override void CreateDirectoryWithPermissions(string directoryPath)
        {
        }

        internal override CheckCertificateStateResult CheckCertificateState(X509Certificate2 candidate) => new(true, null);

        internal override void CorrectCertificateState(X509Certificate2 candidate)
        {
        }
    }
}
