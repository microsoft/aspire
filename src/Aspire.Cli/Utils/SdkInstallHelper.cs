// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Utils;

/// <summary>
/// Helper class for managing SDK installation UX and user interaction.
/// </summary>
internal static class SdkInstallHelper
{
    /// <summary>
    /// Ensures that the .NET SDK is installed and available, attempting installation if needed.
    /// </summary>
    /// <param name="sdkInstaller">The SDK installer service.</param>
    /// <param name="interactionService">The interaction service for user communication.</param>
    /// <param name="runtimeSelector">The runtime selector for managing private SDK installation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the SDK is available or was successfully installed, false otherwise.</returns>
    public static async Task<bool> EnsureSdkInstalledAsync(
        IDotNetSdkInstaller sdkInstaller,
        IInteractionService interactionService,
        IDotNetRuntimeSelector runtimeSelector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(runtimeSelector);

        // Initialize the runtime selector (which may install a private SDK and update the installer's dotnet path).
        // If initialization succeeds the SDK is available; no additional check is needed.
        var isInitialized = await runtimeSelector.InitializeAsync(cancellationToken);

        if (!isInitialized)
        {
            var sdkErrorMessage = string.Format(CultureInfo.InvariantCulture, ErrorStrings.MininumSdkVersionMissing, DotNetSdkInstaller.MinimumSdkVersion);
            interactionService.DisplayError(sdkErrorMessage);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensures that the .NET SDK is installed and available, displaying an error message if it's not.
    /// This is the legacy method signature for backwards compatibility.
    /// </summary>
    /// <param name="sdkInstaller">The SDK installer service.</param>
    /// <param name="interactionService">The interaction service for user communication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the SDK is available, false if it's missing.</returns>
    public static async Task<bool> EnsureSdkInstalledAsync(
        IDotNetSdkInstaller sdkInstaller,
        IInteractionService interactionService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(interactionService);

        var isSdkAvailable = await sdkInstaller.CheckAsync(cancellationToken);

        if (!isSdkAvailable)
        {
            var sdkErrorMessage = string.Format(CultureInfo.InvariantCulture, ErrorStrings.MininumSdkVersionMissing, DotNetSdkInstaller.MinimumSdkVersion);
            interactionService.DisplayError(sdkErrorMessage);
            return false;
        }

        return true;
    }
}