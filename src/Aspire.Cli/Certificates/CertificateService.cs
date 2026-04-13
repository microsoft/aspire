// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
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
}

internal interface ICertificateService
{
    Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken);
}

internal sealed class CertificateService(
    ICertificateToolRunner certificateToolRunner,
    IInteractionService interactionService,
    AspireCliTelemetry telemetry,
    ICliHostEnvironment hostEnvironment,
    Func<bool>? isNonInteractiveTrustSupported = null) : ICertificateService
{
    private const string SslCertDirEnvVar = "SSL_CERT_DIR";
    private readonly Func<bool> _isNonInteractiveTrustSupported = isNonInteractiveTrustSupported ?? OperatingSystem.IsLinux;

    public async Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var environmentVariables = new Dictionary<string, string>();

        // Use the machine-readable check (available in .NET 10 SDK which is the minimum required)
        var trustResult = await CheckMachineReadableAsync();
        await HandleMachineReadableTrustAsync(trustResult, environmentVariables);

        return new EnsureCertificatesTrustedResult
        {
            EnvironmentVariables = environmentVariables
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
            if (!hostEnvironment.SupportsInteractiveInput && !_isNonInteractiveTrustSupported())
            {
                // In non-interactive mode (e.g. CI), skip the trust operation on platforms
                // where it requires user interaction (macOS Keychain password prompt, Windows
                // certificate trust dialog). Linux trust is non-interactive, so it can proceed.
                if (!trustResult.HasCertificates)
                {
                    var ensureResultCode = await interactionService.ShowStatusAsync(
                        InteractionServiceStrings.CheckingCertificates,
                        () => Task.FromResult(certificateToolRunner.EnsureHttpCertificateExists()),
                        emoji: KnownEmojis.LockedWithKey);

                    if (!IsSuccessfulEnsureResult(ensureResultCode))
                    {
                        interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.CertificatesMayNotBeFullyTrusted, ensureResultCode));
                    }
                }

                return;
            }

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

    private static bool IsSuccessfulEnsureResult(EnsureCertificateResult result) =>
        result is EnsureCertificateResult.Succeeded
            or EnsureCertificateResult.ValidCertificatePresent;

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
