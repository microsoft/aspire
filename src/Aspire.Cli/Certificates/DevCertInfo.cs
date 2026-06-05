// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Represents information about an HTTPS development certificate.
/// </summary>
internal sealed class DevCertInfo
{
    public string? Thumbprint { get; set; }

    public string? Subject { get; set; }

    public string[]? SubjectAlternativeNames { get; set; }

    public int Version { get; set; }

    public DateTimeOffset ValidityNotBefore { get; set; }

    public DateTimeOffset ValidityNotAfter { get; set; }

    public bool IsHttpsDevelopmentCertificate { get; set; }

    public bool IsExportable { get; set; }

    public CertificateManager.TrustLevel TrustLevel { get; set; }
}

/// <summary>
/// The result of a certificate clean operation.
/// </summary>
internal sealed class CertificateCleanResult
{
    /// <summary>
    /// Gets whether the clean operation completed successfully.
    /// </summary>
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public required bool Success { get; init; }

    /// <summary>
    /// Gets whether the operation was cancelled by the user.
    /// </summary>
    public bool WasCancelled { get; init; }

    /// <summary>
    /// Gets the error message when the operation fails.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// The result of checking certificate trust status.
/// </summary>
internal sealed class CertificateTrustResult
{
    /// <summary>
    /// Gets whether any valid certificate was found.
    /// </summary>
    public required bool HasCertificates { get; init; }

    /// <summary>
    /// Gets the trust level of the highest versioned valid certificate.
    /// </summary>
    public required CertificateManager.TrustLevel? TrustLevel { get; init; }

    /// <summary>
    /// Gets all certificate information returned by the check.
    /// </summary>
    public required IReadOnlyList<DevCertInfo> Certificates { get; init; }

    /// <summary>
    /// Gets whether the certificate is fully trusted.
    /// </summary>
    public bool IsFullyTrusted => TrustLevel == CertificateManager.TrustLevel.Full;

    /// <summary>
    /// Gets whether the certificate is partially trusted.
    /// </summary>
    public bool IsPartiallyTrusted => TrustLevel == CertificateManager.TrustLevel.Partial;

    /// <summary>
    /// Gets whether the certificate is not trusted at all.
    /// </summary>
    public bool IsNotTrusted => !HasCertificates || TrustLevel == CertificateManager.TrustLevel.None;
}
