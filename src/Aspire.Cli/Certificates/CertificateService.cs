// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
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
    ICliHostEnvironment hostEnvironment,
    CliExecutionContext executionContext,
    IEnvironment environment) : ICertificateService
{
    private const string SslCertDirEnvVar = "SSL_CERT_DIR";

    public async Task<EnsureCertificatesTrustedResult> EnsureCertificatesTrustedAsync(CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        var environmentVariables = new Dictionary<string, string>();
        var isLinux = environment.IsLinux;

        // In non-interactive environments on macOS and Windows we can't successfully
        // prompt for trust (macOS Keychain password, Windows trust dialog).
        // Skip the trust attempt but still check the current state so we can warn when
        // the environment does not already have a trusted certificate. Linux trust is
        // non-interactive so it's safe to run the full flow there.
        var canPerformTrust = hostEnvironment.SupportsInteractiveInput || isLinux;

        if (!canPerformTrust)
        {
            var preCheck = certificateToolRunner.CheckHttpCertificate();

            if (!preCheck.HasCertificates && ShouldGenerateHttpsCertificate())
            {
                // No certificate exists yet. Generate one without trusting it so that
                // Kestrel's UseHttps() can load the cert from the personal store.
                // Trust requires user interaction (Windows dialog / macOS Keychain) which
                // is not possible here, but generation is non-interactive and safe.
                //
                // The .NET SDK's first-run experience normally handles this: the first
                // invocation of any `dotnet` command calls EnsureAspNetCoreHttpsDevelopmentCertificate
                // (trust: false) and writes a sentinel to ~/.dotnet/ so it only runs once per
                // SDK version. For C# AppHosts this happens implicitly via `dotnet run`, but
                // non-.NET AppHost languages (TypeScript, Python, etc.) launch a prebuilt
                // native binary and never invoke `dotnet`, so the first-run cert generation
                // never triggers. This call ensures consistent behavior across all languages.
                var generateResult = certificateToolRunner.EnsureHttpCertificateExists();

                if (generateResult is EnsureCertificateResult.Succeeded or EnsureCertificateResult.ValidCertificatePresent)
                {
                    // Refresh the check so subsequent trust-level logic reflects the newly created cert.
                    preCheck = certificateToolRunner.CheckHttpCertificate();
                }
                else
                {
                    interactionService.DisplayMessage(KnownEmojis.Warning, string.Format(CultureInfo.CurrentCulture, ErrorStrings.CertificateGenerationFailed, generateResult));
                }
            }

            if (preCheck.IsPartiallyTrusted)
            {
                interactionService.DisplayMessage(KnownEmojis.Warning, ErrorStrings.CertificatesPartiallyTrustedNonInteractive);
            }
            else if (!preCheck.IsFullyTrusted)
            {
                interactionService.DisplayMessage(KnownEmojis.Warning, ErrorStrings.CertificatesNotTrustedNonInteractive);
            }

            if (preCheck.IsPartiallyTrusted && isLinux)
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
        if (postTrustCheck.IsPartiallyTrusted && isLinux)
        {
            ConfigureSslCertDir(environmentVariables);
        }

        var partialTrustAccepted = !hostEnvironment.SupportsInteractiveInput
            && isLinux
            && trustResultCode == EnsureCertificateResult.PartiallyFailedToTrustTheCertificate
            && postTrustCheck.IsPartiallyTrusted;

        return new EnsureCertificatesTrustedResult
        {
            EnvironmentVariables = environmentVariables,
            Success = CertificateHelpers.IsSuccessfulTrustResult(trustResultCode) || partialTrustAccepted,
            WasCancelled = trustResultCode == EnsureCertificateResult.UserCancelledTrustStep,
            ResultCode = trustResultCode
        };
    }

    /// <summary>
    /// Checks whether automatic HTTPS certificate generation is enabled.
    /// Set ASPIRE_CLI_GENERATE_HTTPS_CERTIFICATE=false to suppress generation,
    /// mirroring the .NET SDK's DOTNET_GENERATE_ASPNET_CERTIFICATE opt-out.
    /// </summary>
    private bool ShouldGenerateHttpsCertificate()
    {
        var value = executionContext.GetEnvironmentVariable(KnownConfigNames.CliGenerateHttpsCertificate);
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
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
