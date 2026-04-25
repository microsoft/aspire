// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Certificates;

/// <summary>
/// The result of ensuring certificates are trusted.
/// </summary>
internal sealed class EnsureCertificatesTrustedResult
{
    /// <summary>
    /// Gets the environment variables that should be set for the AppHost process
    /// to ensure certificates are properly trusted.
    /// </summary>
    public required IDictionary<string, string> EnvironmentVariables { get; init; }

    /// <summary>
    /// Gets whether the trust operation completed successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Gets whether the operation was cancelled by the user.
    /// </summary>
    public bool WasCancelled { get; init; }

    /// <summary>
    /// Gets the underlying result code from the certificate manager.
    /// </summary>
    public EnsureCertificateResult? ResultCode { get; init; }
}

internal interface ICertificateService
{
    Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken);
}

internal sealed class CertificateService(
    ICertificateToolRunner certificateToolRunner,
    IInteractionService interactionService,
    AspireCliTelemetry telemetry,
    ICliHostEnvironment hostEnvironment) : ICertificateService
{
    private const string SslCertDirEnvVar = "SSL_CERT_DIR";

    public async Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var environmentVariables = new Dictionary<string, string>();

        // In non-interactive environments on macOS and Windows we can't successfully
        // prompt for trust (macOS Keychain password, Windows trust dialog) and we also
        // don't want to silently generate a new certificate that won't be trusted.
        // Skip the trust attempt but still check the current state so we can warn when
        // the environment does not already have a trusted certificate. Linux trust is
        // non-interactive so it's safe to run the full flow there.
        var canPerformTrust = hostEnvironment.SupportsInteractiveInput || OperatingSystem.IsLinux();

        if (!canPerformTrust)
        {
            var preCheck = certificateToolRunner.CheckHttpCertificate();
            if (preCheck.IsPartiallyTrusted)
            {
                interactionService.DisplayMessage(KnownEmojis.Warning, ErrorStrings.CertificatesPartiallyTrustedNonInteractive);
            }
            else if (!preCheck.IsFullyTrusted)
            {
                interactionService.DisplayMessage(KnownEmojis.Warning, ErrorStrings.CertificatesNotTrustedNonInteractive);
            }

            if (preCheck.IsPartiallyTrusted && OperatingSystem.IsLinux())
            {
                ConfigureSslCertDir(environmentVariables);
            }

            return new EnsureCertificatesTrustedResult
            {
                EnvironmentVariables = environmentVariables,
                Success = true
            };
        }

        // Always run trust so the Aspire cache stays populated even when the certificate
        // is already trusted. Each platform's TrustCertificateCore short-circuits without
        // prompting when the certificate is already in the trust store.
        var trustResultCode = await interactionService.ShowStatusAsync(
            InteractionServiceStrings.TrustingCertificates,
            () => Task.FromResult(certificateToolRunner.TrustHttpCertificate()),
            emoji: KnownEmojis.LockedWithKey);

        if (trustResultCode == EnsureCertificateResult.UserCancelledTrustStep)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, CertificatesCommandStrings.TrustCancelled);
        }
        else if (!CertificateHelpers.IsSuccessfulTrustResult(trustResultCode))
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.CertificatesMayNotBeFullyTrusted, trustResultCode));
        }

        var postTrustCheck = certificateToolRunner.CheckHttpCertificate();
        if (postTrustCheck.IsPartiallyTrusted && OperatingSystem.IsLinux())
        {
            ConfigureSslCertDir(environmentVariables);
        }

        return new EnsureCertificatesTrustedResult
        {
            EnvironmentVariables = environmentVariables,
            Success = CertificateHelpers.IsSuccessfulTrustResult(trustResultCode),
            WasCancelled = trustResultCode == EnsureCertificateResult.UserCancelledTrustStep,
            ResultCode = trustResultCode
        };
    }

    private static void ConfigureSslCertDir(Dictionary<string, string> environmentVariables)
    {
        // Get the dev-certs trust path (respects DOTNET_DEV_CERTS_OPENSSL_CERTIFICATE_DIRECTORY override)
        var devCertsTrustPath = CertificateHelpers.GetDevCertsTrustPath();

        // Get the current SSL_CERT_DIR value (if any)
        var currentSslCertDir = Environment.GetEnvironmentVariable(SslCertDirEnvVar);

        // Check if the dev-certs trust path is already included
        if (!string.IsNullOrEmpty(currentSslCertDir))
        {
            var paths = currentSslCertDir.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (paths.Any(p => string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), devCertsTrustPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            {
                // Already included, nothing to do
                return;
            }

            // Append the dev-certs trust path to the existing value
            environmentVariables[SslCertDirEnvVar] = $"{currentSslCertDir}{Path.PathSeparator}{devCertsTrustPath}";
        }
        else
        {
            // Set the dev-certs trust path combined with the system certificate directory.
            var systemCertDirs = CertificateHelpers.GetSystemCertificateDirectories();
            systemCertDirs.Add(devCertsTrustPath);

            environmentVariables[SslCertDirEnvVar] = string.Join(Path.PathSeparator, systemCertDirs);
        }
    }
}

internal sealed class CertificateServiceException(string message) : Exception(message)
{
}
