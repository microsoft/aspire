// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;

namespace Aspire.Cli.Utils;

/// <summary>
/// Helper class for managing SDK availability checking and user interaction.
/// </summary>
internal static class SdkInstallHelper
{
    /// <summary>
    /// Ensures that the .NET SDK is installed and available, displaying an error message if it's not.
    /// </summary>
    /// <param name="sdkInstaller">The SDK installer service.</param>
    /// <param name="interactionService">The interaction service for user communication.</param>
    /// <param name="telemetry">The telemetry service for tracking SDK installation operations.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the SDK is available, false if it's missing.</returns>
    public static async Task<bool> EnsureSdkInstalledAsync(
        IDotNetSdkInstaller sdkInstaller,
        IInteractionService interactionService,
        AspireCliTelemetry telemetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(telemetry);

        using var activity = telemetry.StartReportedActivity(name: TelemetryConstants.Activities.EnsureSdkInstalled);

        var (success, highestInstalledVersion, minimumRequiredVersion) = await sdkInstaller.CheckAsync(cancellationToken);

        var detectedVersion = highestInstalledVersion ?? "(not found)";

        var checkResult = success ? SdkCheckResult.AlreadyInstalled : SdkCheckResult.NotInstalled;

        activity?.SetTag(TelemetryConstants.Tags.SdkDetectedVersion, detectedVersion);
        activity?.SetTag(TelemetryConstants.Tags.SdkMinimumRequiredVersion, minimumRequiredVersion.ToString());
        activity?.SetTag(TelemetryConstants.Tags.SdkCheckResult, ToTelemetryString(checkResult));

        if (!success)
        {
            var sdkErrorMessage = string.Format(CultureInfo.InvariantCulture,
                ErrorStrings.MinimumSdkVersionNotMet,
                minimumRequiredVersion,
                detectedVersion);
            interactionService.DisplayError(sdkErrorMessage);
        }

        return success;
    }

    private static string ToTelemetryString(SdkCheckResult result) => result switch
    {
        SdkCheckResult.AlreadyInstalled => "already_installed",
        SdkCheckResult.NotInstalled => "not_installed",
        _ => result.ToString().ToLowerInvariant()
    };
}
