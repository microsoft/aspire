// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestCertificateManager(int version = CertificateManager.CurrentAspNetCoreCertificateVersion)
    : CertificateManager(NullLogger.Instance, CertificateManager.LocalhostHttpsDistinguishedName, version, version)
{
    public List<byte[]> Certificates { get; } = [];
    public bool SaveCalled { get; private set; }
    public bool TrustCalled { get; private set; }
    public TrustLevel TrustResult { get; set; } = TrustLevel.None;

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

    public override TrustLevel GetTrustLevel(X509Certificate2 certificate) => TrustResult;

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
