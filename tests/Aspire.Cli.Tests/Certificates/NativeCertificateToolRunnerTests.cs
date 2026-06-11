// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Cli.Certificates;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Certificates;

public class NativeCertificateToolRunnerTests
{
    [Fact]
    public void TrustHttpCertificateOnLinux_WithNoCurrentCertificate_CreatesAndTrustsCertificate()
    {
        var certificateManager = new TestCertificateManager();
        var runner = new NativeCertificateToolRunner(certificateManager, isLinux: () => true);

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
        var runner = new NativeCertificateToolRunner(certificateManager, isLinux: () => true);

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
        var runner = new NativeCertificateToolRunner(currentVersionManager, isLinux: () => true);

        var result = runner.TrustHttpCertificateOnLinux([olderCertificate], DateTimeOffset.UtcNow);

        Assert.Equal(EnsureCertificateResult.NewHttpsCertificateTrusted, result);
        Assert.True(currentVersionManager.SaveCalled);
        Assert.True(currentVersionManager.TrustCalled);
    }

    private sealed class TestCertificateManager(int version = CertificateManager.CurrentAspNetCoreCertificateVersion)
        : CertificateManager(NullLogger.Instance, CertificateManager.LocalhostHttpsDistinguishedName, version, version)
    {
        public bool SaveCalled { get; private set; }
        public bool TrustCalled { get; private set; }

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
