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
    CertificateTrustResult CheckHttpCertificate(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures an HTTPS development certificate exists in the personal certificate store,
    /// creating one if necessary, without trusting it.
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
    /// Exports the highest-versioned trusted ASP.NET Core HTTPS development certificate
    /// as a content-addressed PEM file in the specified directory.
    /// </summary>
    /// <param name="outputDirectory">The directory where the PEM certificate should be cached.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
    /// <returns>The output path if a certificate was exported; <see langword="null"/> if no valid certificate was found.</returns>
    string? ExportDevCertificatePublicPem(string outputDirectory, CancellationToken cancellationToken = default);
}
