// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Aspire.Cli.Certificates;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils.EnvironmentChecker;

/// <summary>
/// Checks if the HTTPS development certificate is trusted and detects multiple certificates.
/// </summary>
internal sealed class DevCertsCheck(ILogger<DevCertsCheck> logger, ICertificateToolRunner certificateToolRunner, IEnvironment environment, IProcessExecutionFactory processExecutionFactory) : IEnvironmentCheck
{
    internal const string CheckName = "dev-certs";
    internal const string VersionCheckName = "dev-certs-version";
    internal const string CertUtilCheckName = "dev-certs-certutil";
    internal const string OpenSslCertificateCacheCheckName = "dev-certs-openssl-cache";
    private const string OpenSslCommand = "openssl";
    private const int OpenSslHashCollisionSearchLimit = 10;
    private static readonly TimeSpan s_openSslHashTimeout = TimeSpan.FromSeconds(5);

    public int Order => 35; // After SDK check (30), before container checks (40+)

    private static readonly string s_trustFixCommand = string.Format(CultureInfo.InvariantCulture, DoctorCommandStrings.DevCertsTrustFixFormat, "aspire certs trust");
    private static readonly string s_cleanAndTrustFixCommand = string.Format(CultureInfo.InvariantCulture, DoctorCommandStrings.DevCertsCleanAndTrustFixFormat, "aspire certs clean", "aspire certs trust");
    private static readonly string s_installOpenSslCleanAndTrustFixCommand = string.Format(CultureInfo.InvariantCulture, DoctorCommandStrings.DevCertsInstallOpenSslCleanAndTrustFixFormat, "openssl", "aspire certs clean", "aspire certs trust");
    private static readonly Regex s_openSslHashFileNameRegex = new("^[0-9a-fA-F]{8}\\.\\d+$", RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<EnvironmentCheckResult>> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var trustResult = certificateToolRunner.CheckHttpCertificate();
            var results = EvaluateCertificateResults(trustResult.Certificates, environment);
            await AddLinuxOpenSslCertificateCacheWarningsAsync(results, trustResult.Certificates, environment, cancellationToken).ConfigureAwait(false);
            AddLinuxCertificateToolWarnings(results, environment);

            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error checking dev-certs");
            return [new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = "Unable to check HTTPS development certificate",
                Details = ex.Message
            }];
        }
    }

    /// <summary>
    /// Evaluates certificate information and produces the appropriate check results.
    /// </summary>
    /// <param name="certInfos">Certificate information from <see cref="ICertificateToolRunner.CheckHttpCertificate"/>.</param>
    /// <param name="environment">The environment abstraction for reading environment variables.</param>
    /// <returns>The list of environment check results.</returns>
    internal static List<EnvironmentCheckResult> EvaluateCertificateResults(
        IReadOnlyList<DevCertInfo> certInfos, IEnvironment environment)
    {
        if (certInfos.Count == 0)
        {
            return [new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
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
                    Category = EnvironmentCheckCategories.Environment,
                    Name = CheckName,
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
                    Category = EnvironmentCheckCategories.Environment,
                    Name = CheckName,
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
                    Category = EnvironmentCheckCategories.Environment,
                    Name = CheckName,
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
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
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
            var devCertsTrustPath = CertificateHelpers.GetDevCertsTrustPath(environment);
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = DoctorCommandStrings.DevCertsPartiallyTrustedMessage,
                Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsPartiallyTrustedDetailsFormat, devCertsTrustPath),
                Fix = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsPartiallyTrustedFixFormat, BuildSslCertDirFixCommand(devCertsTrustPath, environment)),
                Link = "https://aka.ms/aspire-prerequisites#dev-certs",
                Metadata = metadata
            });
        }
        else
        {
            // Trusted certificate - success case
            results.Add(new EnvironmentCheckResult
            {
                Category = EnvironmentCheckCategories.Environment,
                Name = CheckName,
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
                Category = EnvironmentCheckCategories.Environment,
                Name = VersionCheckName,
                Status = EnvironmentCheckStatus.Warning,
                Message = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOldVersionMessageFormat, versions),
                Details = string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOldVersionDetailsFormat, CertificateManager.CurrentMinimumAspNetCoreCertificateVersion),
                Fix = s_cleanAndTrustFixCommand,
                Link = "https://aka.ms/aspire-prerequisites#dev-certs"
            });
        }

        return results;
    }

    private async Task AddLinuxOpenSslCertificateCacheWarningsAsync(List<EnvironmentCheckResult> results, IReadOnlyList<DevCertInfo> certInfos, IEnvironment environment, CancellationToken cancellationToken)
    {
        if (!environment.IsLinux())
        {
            return;
        }

        var currentCertificates = GetCurrentDevCertificates(certInfos).ToList();
        if (currentCertificates.Count == 0)
        {
            return;
        }

        var trustPath = CertificateHelpers.GetDevCertsTrustPath(environment);
        var environmentVariables = GetEnvironmentVariables(environment);
        PathLookupHelper.TryResolveExecutablePath(OpenSslCommand, out var openSslPath, environmentVariables);

        var cacheStatus = await EvaluateOpenSslCertificateCacheAsync(trustPath, currentCertificates, openSslPath, cancellationToken).ConfigureAwait(false);
        if (cacheStatus is null)
        {
            return;
        }

        results.Add(new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = OpenSslCertificateCacheCheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = cacheStatus.Message,
            Details = cacheStatus.Details,
            Fix = cacheStatus.Fix,
            Link = "https://aka.ms/aspire-prerequisites#dev-certs"
        });
    }

    private static IEnumerable<DevCertInfo> GetCurrentDevCertificates(IReadOnlyList<DevCertInfo> certInfos)
    {
        var now = DateTimeOffset.Now;
        return certInfos
            .Where(c => c.IsHttpsDevelopmentCertificate &&
                c.ValidityNotBefore <= now &&
                now <= c.ValidityNotAfter &&
                !string.IsNullOrEmpty(c.Thumbprint))
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.ValidityNotAfter)
            .ThenBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<OpenSslCertificateCacheStatus?> EvaluateOpenSslCertificateCacheAsync(string trustPath, IReadOnlyList<DevCertInfo> currentCertificates, string? openSslPath, CancellationToken cancellationToken)
    {
        var fix = GetOpenSslCertificateCacheFix(openSslPath);

        if (!Directory.Exists(trustPath))
        {
            var trustedThumbprints = GetTrustedThumbprints(currentCertificates);
            if (trustedThumbprints.Count == 0)
            {
                return null;
            }

            return new(
                DoctorCommandStrings.DevCertsOpenSslCacheMissingCurrentCertificateMessage,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOpenSslCacheMissingDetailsFormat, trustPath, string.Join(", ", trustedThumbprints)),
                fix);
        }

        var unreadableFiles = new List<string>();
        var mismatchedThumbprints = new List<string>();
        var missingTrustedThumbprints = new List<string>();
        var missingHashLinkThumbprints = new List<string>();

        foreach (var certificate in currentCertificates)
        {
            var certificateFileName = GetOpenSslCertificateFileName(certificate.Thumbprint!);
            var certificateFile = Path.Combine(trustPath, certificateFileName);
            if (!File.Exists(certificateFile))
            {
                if (certificate.TrustLevel != CertificateManager.TrustLevel.None)
                {
                    missingTrustedThumbprints.Add(certificate.Thumbprint!);
                }

                continue;
            }

            try
            {
                using var cachedCertificate = X509CertificateLoader.LoadCertificateFromFile(certificateFile);
                if (!string.Equals(certificate.Thumbprint, cachedCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    mismatchedThumbprints.Add(certificate.Thumbprint!);
                }
                else if (certificate.TrustLevel != CertificateManager.TrustLevel.None &&
                    !await HasOpenSslHashEntryAsync(trustPath, certificateFile, cachedCertificate, openSslPath, cancellationToken).ConfigureAwait(false))
                {
                    missingHashLinkThumbprints.Add(certificate.Thumbprint!);
                }
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                unreadableFiles.Add(certificateFileName);
            }
        }

        if (unreadableFiles.Count > 0)
        {
            return new(
                DoctorCommandStrings.DevCertsOpenSslCacheUnreadableMessage,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOpenSslCacheUnreadableFilesDetailsFormat, trustPath, string.Join(", ", unreadableFiles)),
                fix);
        }

        if (mismatchedThumbprints.Count > 0)
        {
            return new(
                DoctorCommandStrings.DevCertsOpenSslCacheMissingCurrentCertificateMessage,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOpenSslCacheMissingDetailsFormat, trustPath, string.Join(", ", mismatchedThumbprints)),
                fix);
        }

        if (missingTrustedThumbprints.Count > 0)
        {
            return new(
                DoctorCommandStrings.DevCertsOpenSslCacheMissingCurrentCertificateMessage,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOpenSslCacheMissingDetailsFormat, trustPath, string.Join(", ", missingTrustedThumbprints)),
                fix);
        }

        if (missingHashLinkThumbprints.Count > 0)
        {
            return new(
                DoctorCommandStrings.DevCertsOpenSslCacheMissingHashLinkMessage,
                string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DevCertsOpenSslCacheMissingHashLinkDetailsFormat, trustPath, string.Join(", ", missingHashLinkThumbprints)),
                fix);
        }

        return null;
    }

    private static string GetOpenSslCertificateCacheFix(string? openSslPath) =>
        openSslPath is null ? s_installOpenSslCleanAndTrustFixCommand : s_cleanAndTrustFixCommand;

    private async Task<bool> HasOpenSslHashEntryAsync(string trustPath, string certificateFile, X509Certificate2 certificate, string? openSslPath, CancellationToken cancellationToken)
    {
        if (openSslPath is not null)
        {
            var (success, hash) = await TryGetOpenSslHashAsync(openSslPath, certificateFile, cancellationToken).ConfigureAwait(false);
            return success
                ? HasMatchingHashEntry(trustPath, hash, certificate)
                : HasMatchingHashStyleEntry(trustPath, certificate);
        }

        // Without openssl we cannot compute the subject hash that OpenSSL requires. The
        // absence of any hash-style entry for the certificate is still definitely broken,
        // but a matching entry can only be treated as sufficient evidence to avoid warning.
        return HasMatchingHashStyleEntry(trustPath, certificate);
    }

    private static bool HasMatchingHashStyleEntry(string trustPath, X509Certificate2 certificate)
    {
        return Directory.EnumerateFiles(trustPath)
            .Where(path => s_openSslHashFileNameRegex.IsMatch(Path.GetFileName(path)))
            .Any(path => CertificateFileMatches(path, certificate));
    }

    private static bool HasMatchingHashEntry(string trustPath, string hash, X509Certificate2 certificate)
    {
        for (var i = 0; i < OpenSslHashCollisionSearchLimit; i++)
        {
            var hashEntryPath = Path.Combine(trustPath, $"{hash}.{i}");
            if (CertificateFileMatches(hashEntryPath, certificate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CertificateFileMatches(string certificateFile, X509Certificate2 certificate)
    {
        if (!File.Exists(certificateFile))
        {
            return false;
        }

        try
        {
            using var cachedCertificate = X509CertificateLoader.LoadCertificateFromFile(certificateFile);

            return string.Equals(certificate.Thumbprint, cachedCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<(bool Success, string Hash)> TryGetOpenSslHashAsync(string openSslPath, string certificateFile, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var workingDirectory = Path.GetDirectoryName(certificateFile) is { } directory
            ? new DirectoryInfo(directory)
            : new DirectoryInfo(Directory.GetCurrentDirectory());
        await using var process = processExecutionFactory.CreateExecution(
            openSslPath,
            ["x509", "-hash", "-noout", "-in", certificateFile],
            env: null,
            workingDirectory,
            new ProcessInvocationOptions
            {
                SuppressLogging = true,
                StandardOutputCallback = line => stdout.AppendLine(line),
                StandardErrorCallback = _ => { },
            });
        var started = false;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_openSslHashTimeout);

        try
        {
            started = await process.StartAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!started)
            {
                return (false, "");
            }

            var exitCode = await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return (false, "");
            }

            var hash = stdout.ToString().Trim();
            return hash.Length > 0 ? (true, hash) : (false, "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (started)
            {
                TryKillProcess(process);
            }

            throw;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (started)
            {
                TryKillProcess(process);
            }

            return (false, "");
        }
    }

    private static void TryKillProcess(IProcessExecution process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static List<string> GetTrustedThumbprints(IReadOnlyList<DevCertInfo> currentCertificates)
    {
        return currentCertificates
            .Where(c => c.TrustLevel != CertificateManager.TrustLevel.None)
            .Select(c => c.Thumbprint!)
            .ToList();
    }

    private static string GetOpenSslCertificateFileName(string certificateThumbprint) =>
        $"aspnetcore-localhost-{certificateThumbprint}.pem";

    private static void AddLinuxCertificateToolWarnings(List<EnvironmentCheckResult> results, IEnvironment environment)
    {
        if (!environment.IsLinux())
        {
            return;
        }

        var environmentVariables = GetEnvironmentVariables(environment);

        if (PathLookupHelper.TryResolveExecutablePath(CertificateHelpers.CertUtilCommand, out _, environmentVariables))
        {
            return;
        }

        results.Add(new EnvironmentCheckResult
        {
            Category = EnvironmentCheckCategories.Environment,
            Name = CertUtilCheckName,
            Status = EnvironmentCheckStatus.Warning,
            Message = DoctorCommandStrings.DevCertsMissingCertUtilMessage,
            Details = DoctorCommandStrings.DevCertsMissingCertUtilDetails,
            Fix = DoctorCommandStrings.DevCertsMissingCertUtilFix,
            Link = "https://aka.ms/aspire-prerequisites#dev-certs"
        });
    }

    private static Dictionary<string, string> GetEnvironmentVariables(IEnvironment environment) =>
        environment.GetEnvironmentVariables()
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Name, kv => kv.Value!);

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
    private static string BuildSslCertDirFixCommand(string devCertsTrustPath, IEnvironment environment)
    {
        var currentSslCertDir = environment.GetEnvironmentVariable("SSL_CERT_DIR");

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

    private sealed record OpenSslCertificateCacheStatus(string Message, string Details, string Fix);
}
