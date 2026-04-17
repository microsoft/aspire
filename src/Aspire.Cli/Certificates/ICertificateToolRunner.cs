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
    /// Ensures the HTTPS development certificate exists without trusting it.
    /// </summary>
    EnsureCertificateResult EnsureHttpCertificateExists();

    /// <summary>
    /// Trusts the HTTPS development certificate, creating one if necessary.
    /// </summary>
    EnsureCertificateResult TrustHttpCertificate();

    /// <summary>
    /// Removes all HTTPS development certificates.
    /// </summary>
    CertificateCleanResult CleanHttpCertificate();

    /// <summary>
    /// Pre-exports the developer certificate's key material to the well-known cache location
    /// (<c>~/.aspire/dev-certs/https/</c>) so that the app host can access it without triggering
    /// a macOS Keychain access prompt. This is a best-effort operation; failures are silently ignored.
    /// On non-macOS platforms, this is a no-op.
    /// </summary>
    Task PreExportKeyMaterialAsync(CancellationToken cancellationToken);
}
