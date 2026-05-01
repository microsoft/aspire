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

    /// <summary>
    /// Creates or updates NuGet.config for the given channel name without prompting the user
    /// and without displaying a confirmation message containing "NuGet.config" (which can
    /// trip up automation/tests that match on substrings). Resolves the channel name from
    /// configuration if not provided. Suitable for non-interactive code paths such as
    /// <c>aspire init</c> where the caller wants to display its own message (or none).
    /// </summary>
    /// <param name="channelName">The optional channel name from command input.</param>
    /// <param name="outputPath">The output path where the NuGet.config should be created or updated.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> if a NuGet.config was created or updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> CreateOrUpdateNuGetConfigWithoutPromptAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            channelName = await configurationService.GetConfigurationAsync("channel", cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken);
        var matchingChannel = channels.FirstOrDefault(c =>
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (matchingChannel is null || matchingChannel.Type is not PackageChannelType.Explicit)
        {
            return false;
        }

        var mappings = matchingChannel.Mappings;
        if (mappings is null || mappings.Length == 0)
        {
            return false;
        }

        // Call the merger directly — bypass NuGetConfigPrompter so we don't emit a
        // confirmation message containing the substring "NuGet.config", which the
        // AspireInitAsync test helper false-matches as a user-facing Y/n prompt.
        await NuGetConfigMerger.CreateOrUpdateAsync(new DirectoryInfo(outputPath), matchingChannel, cancellationToken: cancellationToken);
        return true;
    }
}
