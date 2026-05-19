// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Interface for running dev-certs operations.
/// </summary>
internal interface ICertificateToolRunner
{
    /// <summary>
    /// Checks certificate trust status, returning structured certificate information.
    /// </summary>
    CertificateTrustResult CheckHttpCertificate();

    /// <summary>
    /// Trusts the HTTPS development certificate, creating one if necessary.
    /// </summary>
    EnsureCertificateResult TrustHttpCertificate();

    /// <summary>
    /// Removes all HTTPS development certificates.
    /// </summary>
    CertificateCleanResult CleanHttpCertificate();
}
