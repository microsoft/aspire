// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Certificates;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if the HTTPS development certificate is trusted and detects multiple certificates.
/// </summary>
internal sealed class DevCertsCheck(ILogger<DevCertsCheck> logger, ICertificateToolRunner certificateToolRunner) : IEnvironmentCheck
{
    public int Order => 35; // After SDK check (30), before container checks (40+)

    private static readonly string s_trustFixCommand = string.Format(CultureInfo.InvariantCulture, DoctorCommandStrings.DevCertsTrustFixFormat, "aspire certs trust");
    private static readonly string s_cleanAndTrustFixCommand = string.Format(CultureInfo.InvariantCulture, DoctorCommandStrings.DevCertsCleanAndTrustFixFormat, "aspire certs clean", "aspire certs trust");

    public Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var trustResult = certificateToolRunner.CheckHttpCertificate();
            var results = EvaluateCertificateResults(trustResult.Certificates);

            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>(results);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking dev-certs");
            return Task.FromResult<IReadOnlyList<EnvironmentCheckResult>>([new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs",
                Status = EnvironmentCheckStatus.Warning,
                Message = "Unable to check HTTPS development certificate",
                Details = ex.Message
            }]);
        }
    }

    /// <summary>
    /// Evaluates certificate information and produces the appropriate check results.
    /// </summary>
    /// <param name="certInfos">Certificate information from <see cref="ICertificateToolRunner.CheckHttpCertificate"/>.</param>
    /// <returns>The list of environment check results.</returns>
    internal static List<EnvironmentCheckResult> EvaluateCertificateResults(
        IReadOnlyList<DevCertInfo> certInfos)
    {
        if (certInfos.Count == 0)
        {
            return [new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.DevCertsNoCertificateMessage,
                Details = DoctorCommandStrings.DevCertsNoCertificateDetails,
                Fix = s_trustFixCommand,
                Link = "https://aka.ms/aspire-prerequisites#dev-certs"
            }];
        }

        var trustedCount = certInfos.Count(c => c.TrustLevel != CertificateManager.TrustLevel.None);
        var fullyTrustedCount = certInfos.Count(c => c.TrustLevel == CertificateManager.TrustLevel.Full);
        var partiallyTrustedCount = certInfos.Count(c => c.TrustLevel == CertificateManager.TrustLevel.Partial);

        // Check for old certificate versions among trusted certificates
        var oldTrustedVersions = certInfos
            .Where(c => c.TrustLevel != CertificateManager.TrustLevel.None && c.Version < CertificateManager.CurrentAspNetCoreCertificateVersion)
            .Select(c => c.Version)
            .ToList();

        var metadata = BuildCertificateMetadata(certInfos);
        var results = new List<EnvironmentCheckResult>();

        // Check for multiple dev certificates (in My store)
        if (certInfos.Count > 1)
        {
            var certDetails = string.Join(", ", certInfos.Select(c =>
            {
                var trustLabel = c.TrustLevel switch
                {
                    CertificateManager.TrustLevel.Full => $" {DoctorCommandStrings.DevCertsTrustLabelFull}",
                    CertificateManager.TrustLevel.Partial => $" {DoctorCommandStrings.DevCertsTrustLabelPartial}",
                    _ => ""
                };
                return $"v{c.Version} ({c.Thumbprint?[..8]}...){trustLabel}";
            }));

            if (trustedCount == 0)
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = "environment",
                    Name = "dev-certs",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsMultipleNoneTrustedMessageFormat, certInfos.Count),
                    Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsMultipleNoneTrustedDetailsFormat, certDetails),
                    Fix = s_cleanAndTrustFixCommand,
                    Link = "https://aka.ms/aspire-prerequisites#dev-certs",
                    Metadata = metadata
                });
            }
            else if (trustedCount < certInfos.Count)
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = "environment",
                    Name = "dev-certs",
                    Status = EnvironmentCheckStatus.Warning,
                    Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsMultipleSomeUntrustedMessageFormat, certInfos.Count),
                    Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsMultipleSomeUntrustedDetailsFormat, certDetails),
                    Fix = s_cleanAndTrustFixCommand,
                    Link = "https://aka.ms/aspire-prerequisites#dev-certs",
                    Metadata = metadata
                });
            }
            // else: all certificates are trusted — no warning needed
            else
            {
                results.Add(new EnvironmentCheckResult
                {
                    Category = "environment",
                    Name = "dev-certs",
                    Status = EnvironmentCheckStatus.Pass,
                    Message = DoctorCommandStrings.DevCertsTrustedMessage,
                    Metadata = metadata
                });
            }
        }
        else if (trustedCount == 0)
        {
            // Single certificate that's not trusted - provide diagnostic info
            var cert = certInfos[0];
            results.Add(new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.DevCertsNotTrustedMessage,
                Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsNotTrustedDetailsFormat, cert.Thumbprint ?? "unknown"),
                Fix = s_trustFixCommand,
                Link = "https://aka.ms/aspire-prerequisites#dev-certs",
                Metadata = metadata
            });
        }
        else if (partiallyTrustedCount > 0 && fullyTrustedCount == 0)
        {
            // Certificate is partially trusted (Linux with SSL_CERT_DIR not configured)
            var devCertsTrustPath = CertificateHelpers.GetDevCertsTrustPath();
            results.Add(new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs",
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.DevCertsPartiallyTrustedMessage,
                Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsPartiallyTrustedDetailsFormat, devCertsTrustPath),
                Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsPartiallyTrustedFixFormat, BuildSslCertDirFixCommand(devCertsTrustPath)),
                Link = "https://aka.ms/aspire-prerequisites#dev-certs",
                Metadata = metadata
            });
        }
        else
        {
            // Trusted certificate - success case
            results.Add(new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs",
                Status = EnvironmentCheckStatus.Pass,
                Message = DoctorCommandStrings.DevCertsTrustedMessage,
                Metadata = metadata
            });
        }

        // Warn about old certificate versions
        if (oldTrustedVersions.Count > 0)
        {
            var versions = string.Join(", ", oldTrustedVersions.Select(v => $"v{v}"));
            results.Add(new EnvironmentCheckResult
            {
                Category = "environment",
                Name = "dev-certs-version",
                Status = EnvironmentCheckStatus.Warning,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOldVersionMessageFormat, versions),
                Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOldVersionDetailsFormat, CertificateManager.CurrentMinimumAspNetCoreCertificateVersion),
                Fix = s_cleanAndTrustFixCommand,
                Link = "https://aka.ms/aspire-prerequisites#dev-certs"
            });
        }

        return results;
    }

    /// <summary>
    /// Builds structured metadata from certificate information for JSON output.
    /// </summary>
    private static JsonObject BuildCertificateMetadata(IReadOnlyList<DevCertInfo> certInfos)
    {
        var certificatesArray = new JsonArray();
        foreach (var cert in certInfos)
        {
            var certNode = new JsonObject
            {
                ["thumbprint"] = cert.Thumbprint ?? "unknown",
                ["version"] = cert.Version,
                ["trustLevel"] = cert.TrustLevel.ToString().ToLowerInvariant(),
                ["notBefore"] = cert.ValidityNotBefore.ToString("o", CultureInfo.InvariantCulture),
                ["notAfter"] = cert.ValidityNotAfter.ToString("o", CultureInfo.InvariantCulture)
            };
            certificatesArray.Add((JsonNode)certNode);
        }

        return new JsonObject
        {
            ["certificates"] = certificatesArray
        };
    }

    /// <summary>
    /// Builds the appropriate shell command for fixing SSL_CERT_DIR configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>SSL_CERT_DIR</c> is already set, only the dev-certs trust path is appended
    /// (preserving the existing value via <c>$SSL_CERT_DIR</c> shell expansion). When it is
    /// not set, the command includes system certificate directories so they are not lost.
    /// </para>
    /// <para>
    /// Includes system certificate directories detected via OpenSSL or well-known fallback
    /// locations, matching the behavior of <see cref="Aspire.Cli.Certificates.CertificateService"/>.
    /// </para>
    /// </remarks>
    private static string BuildSslCertDirFixCommand(string devCertsTrustPath)
    {
        var currentSslCertDir = Environment.GetEnvironmentVariable("SSL_CERT_DIR");

        if (!string.IsNullOrEmpty(currentSslCertDir))
        {
            // SSL_CERT_DIR is already set — just append the dev-certs trust path.
            // Preserve the existing value via $SSL_CERT_DIR shell expansion.
            return $"export SSL_CERT_DIR=\"$SSL_CERT_DIR:{devCertsTrustPath}\"";
        }

        // SSL_CERT_DIR is not set — include system cert directories so they aren't lost.
        var systemCertDirs = CertificateHelpers.GetSystemCertificateDirectories();
        systemCertDirs.Add(devCertsTrustPath);

        // We still prepend $SSL_CERT_DIR to be safe in case the user makes later modifications to their environment
        return $"export SSL_CERT_DIR=\"$SSL_CERT_DIR:{string.Join(':', systemCertDirs)}\"";
    }
}
