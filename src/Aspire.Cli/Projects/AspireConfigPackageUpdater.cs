// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Semver;
using Spectre.Console;

namespace Aspire.Cli.Projects;

/// <summary>
/// Updates config-backed SDK and integration package versions.
/// </summary>
internal static class AspireConfigPackageUpdater
{
    public static async Task<UpdatePackagesResult> UpdatePackagesAsync(
        DirectoryInfo appHostDirectory,
        AspireConfigFile config,
        UpdatePackagesContext context,
        IInteractionService interactionService,
        ILogger logger,
        Func<AspireConfigFile, string?, CancellationToken, Task<bool>> tryApplyUpdatesAsync,
        CancellationToken cancellationToken)
    {
        string? newSdkVersion = null;
        ExceptionDispatchInfo? updateCheckFailure = null;
        var updates = await interactionService.ShowStatusAsync(
            UpdateCommandStrings.AnalyzingProjectStatus,
            async () =>
            {
                var packageUpdates = new List<(string PackageId, string CurrentVersion, string NewVersion)>();

                try
                {
                    var latestSdkPackage = await context.Channel.GetLatestGuestAppHostSdkPackageAsync(appHostDirectory, cancellationToken);
                    if (latestSdkPackage is not null && latestSdkPackage.Version != config.SdkVersion)
                    {
                        newSdkVersion = latestSdkPackage.Version;
                    }
                }
                catch (Exception ex)
                {
                    updateCheckFailure ??= ExceptionDispatchInfo.Capture(ex);
                    logger.LogWarning(ex, "Failed to check for SDK version updates");
                }

                if (config.Packages is not null)
                {
                    foreach (var (packageId, currentVersion) in config.Packages)
                    {
                        try
                        {
                            var packages = await context.Channel.GetPackagesAsync(packageId, appHostDirectory, cancellationToken);
                            var latestPackage = packages
                                .Where(p => SemVersion.TryParse(p.Version, SemVersionStyles.Strict, out _))
                                .OrderByDescending(p => SemVersion.Parse(p.Version, SemVersionStyles.Strict), SemVersion.PrecedenceComparer)
                                .FirstOrDefault();

                            if (latestPackage is not null && latestPackage.Version != currentVersion)
                            {
                                packageUpdates.Add((packageId, currentVersion, latestPackage.Version));
                            }
                        }
                        catch (Exception ex)
                        {
                            updateCheckFailure ??= ExceptionDispatchInfo.Capture(ex);
                            logger.LogWarning(ex, "Failed to check for updates to package {PackageId}", packageId);
                        }
                    }
                }

                return packageUpdates;
            });

        var explicitChannelName = context.Channel.ShouldPersistChannelName() ? context.Channel.Name : null;
        var clearExplicitChannel = context.Channel.Type is PackageChannelType.Explicit &&
            string.Equals(context.Channel.Name, PackageChannelNames.Stable, StringComparisons.ChannelName) &&
            config.Channel is not null;
        var explicitChannelChanged = clearExplicitChannel ||
            explicitChannelName is not null &&
            !string.Equals(config.Channel, explicitChannelName, StringComparisons.CliInputOrOutput);

        if (explicitChannelChanged && updateCheckFailure is not null)
        {
            // A channel transition changes the restore policy as well as version selection.
            // Do not persist a partial transition when any package version could not be evaluated.
            updateCheckFailure.Throw();
        }

        var hasVersionUpdates = updates.Count > 0 || newSdkVersion is not null;
        if (!hasVersionUpdates && !explicitChannelChanged)
        {
            interactionService.DisplayMessage(KnownEmojis.CheckMarkButton, UpdateCommandStrings.ProjectUpToDateMessage);
            return new UpdatePackagesResult { UpdatesApplied = false };
        }

        if (hasVersionUpdates)
        {
            interactionService.DisplayEmptyLine();
            if (newSdkVersion is not null)
            {
                interactionService.DisplayMessage(KnownEmojis.Package, $"[bold yellow]Aspire SDK[/] [bold green]{config.SdkVersion.EscapeMarkup()}[/] to [bold green]{newSdkVersion.EscapeMarkup()}[/]", allowMarkup: true);
            }
            foreach (var (packageId, currentVersion, newVersion) in updates)
            {
                interactionService.DisplayMessage(KnownEmojis.Package, $"[bold yellow]{packageId.EscapeMarkup()}[/] [bold green]{currentVersion.EscapeMarkup()}[/] to [bold green]{newVersion.EscapeMarkup()}[/]", allowMarkup: true);
            }
            interactionService.DisplayEmptyLine();

            if (!await interactionService.PromptConfirmAsync(UpdateCommandStrings.PerformUpdatesPrompt, context.ConfirmBinding, cancellationToken: cancellationToken))
            {
                return new UpdatePackagesResult { UpdatesApplied = false };
            }
        }

        if (newSdkVersion is not null)
        {
            config.SdkVersion = newSdkVersion;
        }

        // Non-stable explicit channels are persisted because their source policy must be
        // reproducible. Selecting the explicit stable channel clears any previous pin so the
        // project returns to the ambient stable source policy without persisting "stable".
        if (explicitChannelChanged)
        {
            config.Channel = explicitChannelName;
        }

        foreach (var (packageId, _, newVersion) in updates)
        {
            config.AddOrUpdatePackage(packageId, newVersion);
        }

        interactionService.DisplayEmptyLine();
        var regenerateResult = await interactionService.ShowStatusAsync(
            UpdateCommandStrings.RegeneratingSdkCode,
            async () =>
            {
                var requestedChannel = context.Channel.Type is PackageChannelType.Explicit
                    ? context.Channel.Name
                    : config.Channel;
                var regenerateSuccess = await tryApplyUpdatesAsync(config, requestedChannel, cancellationToken);

                return new UpdatePackagesResult { UpdatesApplied = regenerateSuccess };
            });

        if (!regenerateResult.UpdatesApplied)
        {
            return regenerateResult;
        }

        var configDirectory = ConfigurationHelper.GetConfigRootDirectory(appHostDirectory);
        config.Save(configDirectory.FullName);

        interactionService.DisplayMessage(KnownEmojis.Package, UpdateCommandStrings.RegeneratedSdkCode);
        interactionService.DisplayEmptyLine();
        interactionService.DisplaySuccess(UpdateCommandStrings.UpdateSuccessfulMessage);

        return new UpdatePackagesResult { UpdatesApplied = true };
    }
}
