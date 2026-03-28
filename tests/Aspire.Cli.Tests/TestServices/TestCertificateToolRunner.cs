// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Certificates;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Test implementation of ICertificateToolRunner that returns fully trusted certs by default.
/// Used to avoid real certificate operations in tests.
/// </summary>
internal sealed class TestCertificateToolRunner : ICertificateToolRunner
{
    public Func<CertificateTrustResult>? CheckHttpCertificateCallback { get; set; }
    public Func<EnsureCertificateResult>? EnsureHttpCertificateExistsCallback { get; set; }
    public Func<EnsureCertificateResult>? TrustHttpCertificateCallback { get; set; }
    public Func<CertificateCleanResult>? CleanHttpCertificateCallback { get; set; }

    public CertificateTrustResult CheckHttpCertificate()
    {
        if (CheckHttpCertificateCallback is not null)
        {
            return CheckHttpCertificateCallback();
        }

        // Default: Return a fully trusted certificate result
        return new CertificateTrustResult
        {
            HasCertificates = true,
            TrustLevel = CertificateManager.TrustLevel.Full,
            Certificates = []
        };
    }

    public EnsureCertificateResult TrustHttpCertificate()
    {
        return TrustHttpCertificateCallback is not null
            ? TrustHttpCertificateCallback()
            : EnsureCertificateResult.ExistingHttpsCertificateTrusted;
    }

    public EnsureCertificateResult EnsureHttpCertificateExists()
    {
        return EnsureHttpCertificateExistsCallback is not null
            ? EnsureHttpCertificateExistsCallback()
            : EnsureCertificateResult.ValidCertificatePresent;
    }

    public CertificateCleanResult CleanHttpCertificate()
    {
        return CleanHttpCertificateCallback is not null
            ? CleanHttpCertificateCallback()
            : new CertificateCleanResult { Success = true };
    }
}
