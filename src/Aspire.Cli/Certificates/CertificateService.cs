// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Microsoft.AspNetCore.Certificates.Generation;
using Microsoft.Extensions.Logging;

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
    /// Gets the path to the exported PEM certificate file, if one was exported.
    /// </summary>
    public string? DevCertPemPath { get; init; }
}

internal interface ICertificateService
{
    Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken);
}

internal sealed class CertificateService(
    ICertificateToolRunner certificateToolRunner,
    IInteractionService interactionService,
    AspireCliTelemetry telemetry,
    CliExecutionContext executionContext,
    ILogger<CertificateService> logger) : ICertificateService
{
    private const string SslCertDirEnvVar = "SSL_CERT_DIR";
    private const string DevCertPemFileName = "aspire-dev-cert.pem";

    internal string DevCertPemPath => Path.Combine(
        executionContext.HomeDirectory.FullName, ".aspire", "dev-certs", DevCertPemFileName);

    public async Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var environmentVariables = new Dictionary<string, string>();

        // Use the machine-readable check (available in .NET 10 SDK which is the minimum required)
        var trustResult = await CheckMachineReadableAsync();
        await HandleMachineReadableTrustAsync(trustResult, environmentVariables);

        var devCertPemPath = ExportDevCertificatePem();

        return new EnsureCertificatesTrustedResult
        {
            EnvironmentVariables = environmentVariables,
            DevCertPemPath = devCertPemPath
        };
    }

    private async Task<CertificateTrustResult> CheckMachineReadableAsync()
    {
        var result = await interactionService.ShowStatusAsync(
            InteractionServiceStrings.CheckingCertificates,
            () => Task.FromResult(certificateToolRunner.CheckHttpCertificate()),
            emoji: KnownEmojis.LockedWithKey);

        return result;
    }

    private async Task HandleMachineReadableTrustAsync(
        CertificateTrustResult trustResult,
        Dictionary<string, string> environmentVariables)
    {
        // If fully trusted, nothing more to do
        if (trustResult.IsFullyTrusted)
        {
            return;
        }

        // If not trusted at all, run the trust operation
        if (trustResult.IsNotTrusted)
        {
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

            // Re-check trust status after trust operation
            trustResult = certificateToolRunner.CheckHttpCertificate();
        }

        // If partially trusted (either initially or after trust), configure SSL_CERT_DIR on Linux
        if (trustResult.IsPartiallyTrusted && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ConfigureSslCertDir(environmentVariables);
        }
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

    private string? ExportDevCertificatePem()
    {
        try
        {
            var result = certificateToolRunner.ExportDevCertificatePublicPem(DevCertPemPath);
            if (result is not null)
            {
                logger.LogDebug("Exported dev certificate public PEM to {Path}", result);
            }
            else
            {
                logger.LogDebug("No valid dev certificate found to export as PEM");
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to export dev certificate as PEM");
            return null;
        }
    }

}

internal sealed class CertificateServiceException(string message) : Exception(message)
{
}
