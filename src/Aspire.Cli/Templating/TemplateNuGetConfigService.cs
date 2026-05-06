// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Templating;

/// <summary>
/// Handles NuGet.config creation and updates for template output directories,
/// and provides channel-aware template package resolution and installation.
/// </summary>
internal sealed class TemplateNuGetConfigService(
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IPackagingService packagingService,
    ITemplateVersionPrompter templateVersionPrompter,
    ICliHostEnvironment hostEnvironment)
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
    /// Applies NuGet.config create/update behavior for a channel name (option or global config value).
    /// </summary>
    /// <param name="channelName">The optional channel name from command input.</param>
    /// <param name="outputPath">The output path where the project was created.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public async Task PromptToCreateOrUpdateNuGetConfigAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Resolves the channel and template package version that should be used to install Aspire project templates.
    /// </summary>
    /// <param name="query">Inputs that control channel/version selection.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The selected template package and the channel it was resolved from.</returns>
    /// <exception cref="ChannelNotFoundException">Thrown when <paramref name="query"/> specifies a channel name that does not match any configured channel.</exception>
    /// <exception cref="EmptyChoicesException">Thrown when no template package versions are available across the considered channels.</exception>
    public async Task<TemplatePackageSelection> ResolveTemplatePackageAsync(TemplatePackageQuery query, CancellationToken cancellationToken)
    {
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken);

        // Channel override (e.g. --channel) is the only channel input considered here;
        // the global ~/.aspire/aspire.config.json#channel value is no longer a source
        // (PR1: cross-route contamination G1).
        var channelName = query.ChannelOverride;

        // Honor PR hives only when the caller opts in. Init suppresses this so a developer
        // with stale ~/.aspire/hives/* doesn't get a different template than on a clean machine.
        var hasPrHives = query.IncludePrHives && executionContext.GetPrHiveCount() > 0;
        var hasChannelSetting = !string.IsNullOrEmpty(channelName);

        IEnumerable<PackageChannel> channels;
        if (hasChannelSetting)
        {
            var matchingChannel = allChannels.FirstOrDefault(c => string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));
            if (matchingChannel is null)
            {
                throw new ChannelNotFoundException($"No channel found matching '{channelName}'. Valid options are: {string.Join(", ", allChannels.Select(c => c.Name))}");
            }
            channels = new[] { matchingChannel };
        }
        else
        {
            // If there are hives (PR build directories), include all channels.
            // Otherwise, only use the implicit/default channel to avoid prompting.
            channels = hasPrHives
                ? allChannels
                : allChannels.Where(c => c.Type is PackageChannelType.Implicit);
        }

        var packagesFromChannels = await interactionService.ShowStatusAsync(Resources.TemplatingStrings.SearchingForAvailableTemplateVersions, async () =>
        {
            var results = new List<(NuGetPackage Package, PackageChannel Channel)>();
            var resultsLock = new object();

            await Parallel.ForEachAsync(channels, cancellationToken, async (channel, ct) =>
            {
                var templatePackages = await channel.GetTemplatePackagesAsync(executionContext.WorkingDirectory, ct);
                lock (resultsLock)
                {
                    results.AddRange(templatePackages.Select(p => (p, channel)));
                }
            });

            return results;
        });

        if (!packagesFromChannels.Any())
        {
            throw new EmptyChoicesException(Resources.TemplatingStrings.NoTemplateVersionsFound);
        }

        var orderedPackagesFromChannels = packagesFromChannels.OrderByDescending(p => Semver.SemVersion.Parse(p.Package.Version), Semver.SemVersion.PrecedenceComparer);

        if (query.VersionOverride is { } version)
        {
            var explicitMatch = orderedPackagesFromChannels.FirstOrDefault(p => p.Package.Version == version);
            if (explicitMatch.Package is not null)
            {
                return new TemplatePackageSelection(explicitMatch.Package, explicitMatch.Channel);
            }
        }

        if (VersionHelper.TryGetCurrentCliVersionMatch(
            orderedPackagesFromChannels,
            p => p.Package.Version,
            out var cliVersionMatch,
            channelName: channelName,
            hasPrHives: hasPrHives))
        {
            return new TemplatePackageSelection(cliVersionMatch.Package, cliVersionMatch.Channel);
        }

        // If channel was specified via --channel option or global setting (but no --version),
        // automatically select the highest version from that channel without prompting.
        if (hasChannelSetting)
        {
            var first = orderedPackagesFromChannels.First();
            return new TemplatePackageSelection(first.Package, first.Channel);
        }

        // In non-interactive mode, automatically select the highest version.
        if (!hostEnvironment.SupportsInteractiveInput)
        {
            var first = orderedPackagesFromChannels.First();
            return new TemplatePackageSelection(first.Package, first.Channel);
        }

        var prompted = await templateVersionPrompter.PromptForTemplatesVersionAsync(orderedPackagesFromChannels, cancellationToken);
        return new TemplatePackageSelection(prompted.Package, prompted.Channel);
    }

    /// <summary>
    /// Installs the resolved Aspire project templates package, generating a temporary NuGet.config from the channel mappings when the channel is explicit.
    /// </summary>
    /// <param name="selection">The template package + channel returned by <see cref="ResolveTemplatePackageAsync"/>.</param>
    /// <param name="runner">The .NET CLI runner used to invoke <c>dotnet new install</c>. Passed in (rather than injected) because the runner has a transient DI lifetime.</param>
    /// <param name="statusMessage">Status text shown while the install runs.</param>
    /// <param name="statusEmoji">Optional emoji prefix shown next to the status message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The install exit code, the parsed template version (if available), and the captured stdout/stderr lines.</returns>
    public async Task<TemplateInstallOutcome> InstallTemplatePackageAsync(
        TemplatePackageSelection selection,
        IDotNetCliRunner runner,
        string statusMessage,
        KnownEmoji? statusEmoji,
        CancellationToken cancellationToken)
    {
        // Whilst we install the templates - if we are using an explicit channel we need to
        // generate a temporary NuGet.config file to make sure we install the right package
        // from the right feed. If we are using an implicit channel then we just use the
        // ambient configuration (although we should still specify the source) because
        // the user would have selected it.
        //
        // The temporary config is disposed when this method returns. That is intentional —
        // only `dotnet new install` consumes the config; the subsequent `dotnet new <template>`
        // call (in DotNetTemplateFactory and InitCommand) operates against the already-installed
        // template hive and uses the ambient NuGet configuration.
        using var temporaryConfig = selection.Channel.Type == PackageChannelType.Explicit
            ? await TemporaryNuGetConfig.CreateAsync(selection.Channel.Mappings!)
            : null;

        var collector = new OutputCollector();

        var (exitCode, templateVersion) = await interactionService.ShowStatusAsync<(int ExitCode, string? TemplateVersion)>(
            statusMessage,
            async () =>
            {
                var options = new ProcessInvocationOptions
                {
                    StandardOutputCallback = collector.AppendOutput,
                    StandardErrorCallback = collector.AppendOutput,
                };

                return await runner.InstallTemplateAsync(
                    packageName: TemplatesPackageName,
                    version: selection.Package.Version,
                    nugetConfigFile: temporaryConfig?.ConfigFile,
                    nugetSource: selection.Package.Source,
                    force: true,
                    options: options,
                    cancellationToken: cancellationToken);
            },
            emoji: statusEmoji);

        return new TemplateInstallOutcome(exitCode, templateVersion, collector.GetLines().ToArray());
    }
}

/// <summary>
/// Inputs that control how <see cref="TemplateNuGetConfigService.ResolveTemplatePackageAsync"/> picks a channel and version.
/// </summary>
/// <param name="ChannelOverride">Optional channel name override (e.g. from <c>--channel</c>). When null, the global <c>channel</c> configuration is consulted.</param>
/// <param name="VersionOverride">Optional explicit template version (e.g. from <c>--version</c>).</param>
/// <param name="SourceOverride">Optional source override carried for symmetry with <see cref="TemplateInputs"/>; not consulted by resolution today.</param>
/// <param name="IncludePrHives">When true (e.g. for <c>aspire new</c>), local PR hive directories under <c>~/.aspire/hives</c> participate in channel discovery; when false (e.g. for <c>aspire init</c>), they are ignored.</param>
internal sealed record TemplatePackageQuery(
    string? ChannelOverride,
    string? VersionOverride,
    string? SourceOverride,
    bool IncludePrHives);

/// <summary>
/// The template package and channel selected by <see cref="TemplateNuGetConfigService.ResolveTemplatePackageAsync"/>.
/// </summary>
/// <param name="Package">The selected template package (id, version, source).</param>
/// <param name="Channel">The channel that produced <paramref name="Package"/>.</param>
internal sealed record TemplatePackageSelection(NuGetPackage Package, PackageChannel Channel);

/// <summary>
/// Result of <see cref="TemplateNuGetConfigService.InstallTemplatePackageAsync"/>.
/// </summary>
/// <param name="ExitCode">Exit code from <c>dotnet new install</c>.</param>
/// <param name="TemplateVersion">Parsed template version (when the install reported one).</param>
/// <param name="OutputLines">Captured stdout/stderr lines from the install process for diagnostic display by the caller.</param>
internal sealed record TemplateInstallOutcome(
    int ExitCode,
    string? TemplateVersion,
    IReadOnlyList<(Aspire.Cli.Utils.OutputLineStream Stream, string Line)> OutputLines);
