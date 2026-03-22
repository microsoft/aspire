// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Handles NuGet.config creation and updates for template output directories.
/// </summary>
internal sealed class TemplateNuGetConfigService(
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IPackagingService packagingService,
    IConfigurationService configurationService)
{
    /// <summary>
    /// Applies NuGet.config create/update behavior for a resolved package channel.
    /// </summary>
    /// <param name="channel">The resolved package channel.</param>
    /// <param name="outputPath">The output path where the project was created.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task PromptToCreateOrUpdateNuGetConfigAsync(PackageChannel channel, string outputPath, CancellationToken cancellationToken)
    {
        if (channel.Type is not PackageChannelType.Explicit)
        {
            return;
        }

        var mappings = channel.Mappings;
        if (mappings is null || mappings.Length == 0)
        {
            return;
        }

        var workingDir = executionContext.WorkingDirectory;
        var outputDir = new DirectoryInfo(outputPath);

        var normalizedOutputPath = Path.GetFullPath(outputPath);
        var normalizedWorkingPath = workingDir.FullName;
        var isInPlaceCreation = string.Equals(normalizedOutputPath, normalizedWorkingPath, StringComparison.OrdinalIgnoreCase);

        var nugetConfigPrompter = new NuGetConfigPrompter(interactionService);

        if (!isInPlaceCreation)
        {
            await nugetConfigPrompter.CreateOrUpdateWithoutPromptAsync(outputDir, channel, cancellationToken);
            return;
        }

        await nugetConfigPrompter.PromptToCreateOrUpdateAsync(workingDir, channel, cancellationToken);
    }

    /// <summary>
    /// Applies NuGet.config create/update behavior for a channel name (option or global config value).
    /// </summary>
    /// <param name="channelName">The optional channel name from command input.</param>
    /// <param name="outputPath">The output path where the project was created.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task PromptToCreateOrUpdateNuGetConfigAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            channelName = await configurationService.GetConfigurationAsync("channel", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            return;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken);
        var matchingChannel = channels.FirstOrDefault(c =>
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (matchingChannel is null)
        {
            return;
        }

        await PromptToCreateOrUpdateNuGetConfigAsync(matchingChannel, outputPath, cancellationToken);
    }
}
