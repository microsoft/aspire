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

    /// <summary>
    /// Exports the public key of the highest-versioned ASP.NET Core HTTPS development certificate
    /// as a PEM file at the specified path.
    /// </summary>
    /// <param name="outputPath">The file path to write the PEM certificate to.</param>
    /// <returns>The output path if a certificate was exported; <see langword="null"/> if no valid certificate was found.</returns>
    string? ExportDevCertificatePublicPem(string outputPath);
}
