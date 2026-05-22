// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Handles NuGet.config creation and updates for template output directories,
/// and provides channel-aware template package resolution and installation.
/// </summary>
internal sealed class TemplateNuGetConfigService(
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IPackagingService packagingService)
{
    /// <summary>
    /// The name of the NuGet package that ships the Aspire project templates.
    /// </summary>
    public const string TemplatesPackageName = "Aspire.ProjectTemplates";

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
    /// Applies NuGet.config create/update behavior for a channel name resolved from any of
    /// the equivalent channel-name sources: <c>--channel</c>, per-project
    /// <c>aspire.config.json#channel</c>, or the running CLI's
    /// <see cref="CliExecutionContext.IdentityChannel"/>.
    /// </summary>
    /// <param name="channelName">
    /// The channel name to look up in the packaging service. May be sourced from
    /// <c>--channel</c>, per-project <c>aspire.config.json#channel</c>, or the running
    /// CLI's <see cref="CliExecutionContext.IdentityChannel"/> — all are name-equivalent
    /// lookup keys for this entrypoint.
    /// </param>
    /// <param name="outputPath">The output path where the project was created.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task PromptToCreateOrUpdateNuGetConfigAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
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
    /// trip up automation/tests that match on substrings). Suitable for non-interactive
    /// code paths such as <c>aspire init</c> where the caller wants to display its own
    /// message (or none). The channel name may come from any of the equivalent
    /// channel-name sources: <c>--channel</c>, per-project
    /// <c>aspire.config.json#channel</c>, or the running CLI's
    /// <see cref="CliExecutionContext.IdentityChannel"/>.
    /// </summary>
    /// <param name="channelName">
    /// The channel name to look up in the packaging service. May be sourced from
    /// <c>--channel</c>, per-project <c>aspire.config.json#channel</c>, or the running
    /// CLI's <see cref="CliExecutionContext.IdentityChannel"/> — all are name-equivalent
    /// lookup keys for this entrypoint.
    /// </param>
    /// <param name="outputPath">The output path where the NuGet.config should be created or updated.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns><see langword="true"/> if a NuGet.config was created or updated; otherwise <see langword="false"/>.</returns>
    public async Task<bool> CreateOrUpdateNuGetConfigWithoutPromptAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
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

    /// <summary>
    /// Creates or updates a project NuGet.config that maps Aspire packages to an explicit package source override.
    /// </summary>
    public async Task<bool> CreateOrUpdateNuGetConfigForSourceOverrideAsync(
        string? sourceOverride,
        string? channelName,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceOverride))
        {
            return false;
        }

        PackageChannel? matchingChannel = null;

        if (!string.IsNullOrWhiteSpace(channelName))
        {
            var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
            matchingChannel = channels.FirstOrDefault(c =>
                string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
        }

        return await CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, matchingChannel, outputPath, cancellationToken);
    }

    /// <summary>
    /// Creates or updates a project NuGet.config that maps Aspire packages to an explicit package source override.
    /// </summary>
    public static async Task<bool> CreateOrUpdateNuGetConfigForSourceOverrideAsync(
        string? sourceOverride,
        PackageChannel? channel,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceOverride))
        {
            return false;
        }

        var mappings = PackageSourceOverrideMappings.Create(sourceOverride, channel);
        await NuGetConfigMerger.CreateOrUpdateAsync(
            new DirectoryInfo(outputPath),
            mappings,
            channel?.ConfigureGlobalPackagesFolder ?? false,
            cancellationToken: cancellationToken);
        return true;
    }

    /// <summary>
    /// Looks up a configured <see cref="PackageChannel"/> by name. Returns
    /// <see langword="null"/> when <paramref name="channelName"/> is null/empty or does
    /// not match any configured channel (e.g. identity=local on a machine without a
    /// local hive). Used by post-install scaffolding to decide whether to write a
    /// per-project channel pin into <c>aspire.config.json</c> and to look up channel
    /// mappings for the scaffolded project's <c>NuGet.config</c>.
    /// </summary>
    public async Task<PackageChannel?> GetChannelByNameAsync(string? channelName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return null;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
        return channels.FirstOrDefault(c =>
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
    }
}
