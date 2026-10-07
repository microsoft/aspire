// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

using Aspire.Cli.Commands;
using Aspire.Cli.DotNet;
using Aspire.Cli.Exceptions;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Utils;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Templating;

/// <summary>
/// Creates template NuGet.config files and configures existing .NET AppHosts,
/// and provides channel-aware template package resolution and installation.
/// </summary>
internal sealed class TemplateNuGetConfigService(
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IPackagingService packagingService,
    ITemplateVersionPrompter templateVersionPrompter,
    ICliHostEnvironment hostEnvironment,
    BundleNuGetService nuGetService)
{
    /// <summary>
    /// The name of the NuGet package that ships the Aspire project templates.
    /// </summary>
    public const string TemplatesPackageName = "Aspire.ProjectTemplates";

    /// <summary>
    /// Creates initial template NuGet configuration when the selected channel requires it.
    /// </summary>
    public async Task PromptToCreateNuGetConfigAsync(PackageChannel channel, string outputPath, CancellationToken cancellationToken)
    {
        if (!channel.ShouldCreateNuGetConfig())
        {
            return;
        }

        var outputDir = new DirectoryInfo(outputPath);
        if (string.Equals(outputDir.FullName, executionContext.WorkingDirectory.FullName, StringComparisons.FileSystemPath) &&
            !await interactionService.PromptConfirmAsync(
                Resources.TemplatingStrings.CreateNugetConfigConfirmation,
                binding: PromptBinding.CreateDefault(true),
                cancellationToken: cancellationToken))
        {
            return;
        }

        await WriteNewAppHostNuGetConfigAsync(channel, sourceOverride: null, outputDir, cancellationToken);
        interactionService.DisplayMessage(KnownEmojis.Package, Resources.TemplatingStrings.NuGetConfigCreatedConfirmationMessage);
    }

    /// <summary>
    /// Resolves a channel name and creates its initial template NuGet configuration.
    /// </summary>
    public async Task PromptToCreateNuGetConfigAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
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

        await PromptToCreateNuGetConfigAsync(matchingChannel, outputPath, cancellationToken);
    }

    /// <summary>
    /// Applies a channel's shared NuGet policy to a .NET AppHost's local configuration without prompting.
    /// </summary>
    public async Task<bool> ConfigureDotNetAppHostNuGetConfigAsync(string? channelName, string outputPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(channelName))
        {
            return false;
        }

        var channels = await packagingService.GetChannelsAsync(cancellationToken, channelName);
        var matchingChannel = channels.FirstOrDefault(c =>
            string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        if (matchingChannel is null)
        {
            return false;
        }

        if (matchingChannel.Mappings is not { Length: > 0 })
        {
            return false;
        }

        // Stable uses ambient configuration. Only update an existing file to retire old
        // channel policy; do not create a redundant local config.
        // See: https://github.com/microsoft/aspire/issues/18124
        if (!matchingChannel.ShouldCreateNuGetConfig())
        {
            var targetDir = new DirectoryInfo(outputPath);
            if (!targetDir.Exists || !DotNetAppHostNuGetConfigMerger.TryFindNuGetConfigInDirectory(targetDir, out _))
            {
                return false;
            }
        }

        var targetDirectory = new DirectoryInfo(outputPath);
        var configuration = nuGetService.BuildChannelConfiguration(
            targetDirectory,
            AppHostWorkloadId.Create(targetDirectory.FullName),
            matchingChannel,
            packageSourceOverride: null,
            executionContext.NuGetServiceIndexOverride,
            cancellationToken);
        await new DotNetAppHostNuGetConfigMerger(nuGetService).CreateOrUpdateAsync(
            targetDirectory,
            configuration,
            matchingChannel.ShouldCreateNuGetConfig(),
            matchingChannel.ConfigureGlobalPackagesFolder ? CliPathHelper.StagingNuGetPackagesFolderName : null,
            confirmationCallback: null,
            cancellationToken);
        return true;
    }

    /// <summary>
    /// Creates initial template NuGet configuration for an explicit package source override.
    /// </summary>
    public async Task<bool> CreateNuGetConfigForSourceOverrideAsync(
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

        return await CreateNuGetConfigForSourceOverrideAsync(sourceOverride, matchingChannel, outputPath, cancellationToken);
    }

    /// <summary>
    /// Creates initial template NuGet configuration for an explicit package source override.
    /// </summary>
    public async Task<bool> CreateNuGetConfigForSourceOverrideAsync(
        string? sourceOverride,
        PackageChannel? channel,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceOverride))
        {
            return false;
        }
        if (PackageSourceOverrideMappings.HasCredentialMaterial(sourceOverride))
        {
            throw new ArgumentException("Credential-bearing HTTP sources cannot be persisted.", nameof(sourceOverride));
        }

        await WriteNewAppHostNuGetConfigAsync(channel, sourceOverride, new DirectoryInfo(outputPath), cancellationToken);
        return true;
    }

    private async Task WriteNewAppHostNuGetConfigAsync(
        PackageChannel? channel,
        string? sourceOverride,
        DirectoryInfo outputDirectory,
        CancellationToken cancellationToken)
    {
        outputDirectory.Create();
        var existingConfig = outputDirectory.EnumerateFiles()
            .FirstOrDefault(static file => string.Equals(file.Name, "nuget.config", StringComparison.OrdinalIgnoreCase));
        if (existingConfig is not null)
        {
            throw new IOException($"Cannot create template NuGet configuration because '{existingConfig.FullName}' already exists.");
        }

        var configuration = nuGetService.BuildChannelConfiguration(
            outputDirectory,
            AppHostWorkloadId.Create(outputDirectory.FullName),
            channel,
            sourceOverride,
            executionContext.NuGetServiceIndexOverride,
            cancellationToken);
        var content = await nuGetService.CreateNuGetConfigContentAsync(
            configuration,
            originalContent: null,
            channel?.ConfigureGlobalPackagesFolder == true ? CliPathHelper.StagingNuGetPackagesFolderName : null,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await using var stream = new FileInfo(Path.Combine(outputDirectory.FullName, "nuget.config")).Open(
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, CancellationToken.None);
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
        var allChannels = await packagingService.GetChannelsAsync(cancellationToken, query.RequestedChannel);
        var isUnqualifiedLocalResolution =
            query.IncludePrHives &&
            string.Equals(executionContext.IdentityChannel, PackageChannelNames.Local, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(query.RequestedChannel) &&
            string.IsNullOrWhiteSpace(query.VersionOverride) &&
            string.IsNullOrWhiteSpace(query.SourceOverride);

        // Honor PR hives only when the caller opts in. Init suppresses this so a developer
        // with stale ~/.aspire/hives/* doesn't get a different template than on a clean machine.
        // PR dogfood installs can discover a matching local-build channel outside the default
        // hives directory, so also treat an installed local-build source as a hive signal.
        //
        // An ASPIRE_CLI_PACKAGES / sidecar `packages` override is different from a stale hive: it
        // is a deliberate, per-invocation instruction to resolve Aspire.* from a local directory
        // (used to emulate a released/staging build entirely from locally built packages). Honor it
        // unconditionally — even when PR-hive discovery is suppressed (e.g. `init`) and regardless of
        // the emulated channel name (stable/daily/staging) — otherwise template resolution silently
        // falls back to nuget.org instead of the local packages. See docs/specs/cli-identity-sidecar.md.
        var hasLocalPackagesOverride = executionContext.IdentityPackagesDirectory is not null;
        var hasPrHives = hasLocalPackagesOverride ||
            (query.IncludePrHives &&
                (executionContext.GetHiveCount() > 0 ||
                    allChannels.Any(static c => c.Type is PackageChannelType.Explicit && HasInstalledLocalBuildPackageSource(c))));

        IEnumerable<PackageChannel> channels;
        if (isUnqualifiedLocalResolution)
        {
            channels = allChannels.Where(c =>
                c.IsBackedByLocalPackageDirectory &&
                string.Equals(c.Name, executionContext.IdentityChannel, StringComparison.OrdinalIgnoreCase));
        }
        else if (!string.IsNullOrEmpty(query.RequestedChannel))
        {
            var matchingChannel = allChannels.FirstOrDefault(c =>
                    string.Equals(c.Name, query.RequestedChannel, StringComparison.OrdinalIgnoreCase))
                ?? throw new ChannelNotFoundException(
                    $"No channel found matching '{query.RequestedChannel}'. Valid options are: " +
                    $"{string.Join(", ", allChannels.Select(c => c.Name))}");
            channels = [matchingChannel];
        }
        else if (!string.IsNullOrWhiteSpace(query.SourceOverride))
        {
            // Every channel would query the same explicit source, so querying PR/local channels as
            // well would attach identical results to whichever channel finishes first. Keep the
            // implicit channel as the deterministic owner unless the user requested a channel.
            channels = allChannels.Where(c => c.Type is PackageChannelType.Implicit);
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
                // Init and explicit source/version overrides historically enumerate the source
                // before this service selects a version. Keep pin filtering only for channel
                // resolution in `aspire new`; unqualified local resolution selects the exact
                // CLI identity version below from the complete candidate set.
                var filterLocalPackagesToPinnedVersion =
                    query.IncludePrHives &&
                    !isUnqualifiedLocalResolution &&
                    string.IsNullOrWhiteSpace(query.VersionOverride) &&
                    string.IsNullOrWhiteSpace(query.SourceOverride);
                var templatePackages = string.IsNullOrWhiteSpace(query.SourceOverride)
                    ? await channel.GetTemplatePackagesFromChannelAsync(
                        executionContext.WorkingDirectory,
                        filterLocalPackagesToPinnedVersion,
                        ct)
                    : await channel.GetTemplatePackagesAsync(
                        executionContext.WorkingDirectory,
                        PackageSourceOverrideMappings.CreateForSourceOnlyOperations(query.SourceOverride),
                        filterLocalPackagesToPinnedVersion,
                        ct);
                lock (resultsLock)
                {
                    results.AddRange(templatePackages.Select(p => (p, channel)));
                }
            });

            return results;
        });

        if (isUnqualifiedLocalResolution)
        {
            var localMatch = packagesFromChannels.FirstOrDefault(p =>
                string.Equals(p.Package.Version, executionContext.IdentitySdkVersion, StringComparison.OrdinalIgnoreCase));
            if (localMatch.Package is null)
            {
                throw new EmptyChoicesException(
                    string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.TemplatingStrings.NoMatchingLocalTemplatePackage,
                        executionContext.IdentitySdkVersion));
            }

            return new TemplatePackageSelection(localMatch.Package, localMatch.Channel);
        }

        var orderedPackagesFromChannels = packagesFromChannels.OrderByDescending(p => Semver.SemVersion.Parse(p.Package.Version), Semver.SemVersion.PrecedenceComparer);

        if (query.VersionOverride is { } version)
        {
            var explicitMatch = orderedPackagesFromChannels.FirstOrDefault(p =>
                string.Equals(p.Package.Version, version, StringComparison.OrdinalIgnoreCase));
            if (explicitMatch.Package is not null)
            {
                return new TemplatePackageSelection(explicitMatch.Package, explicitMatch.Channel);
            }

            throw new EmptyChoicesException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    Resources.TemplatingStrings.TemplateVersionNotFound,
                    version));
        }

        if (!packagesFromChannels.Any())
        {
            throw new EmptyChoicesException(Resources.TemplatingStrings.NoTemplateVersionsFound);
        }

        if (VersionHelper.TryGetCurrentCliVersionMatch(
            orderedPackagesFromChannels,
            p => p.Package.Version,
            executionContext.IdentitySdkVersion,
            out var cliVersionMatch,
            channelName: query.RequestedChannel,
            hasPrHives: hasPrHives))
        {
            return new TemplatePackageSelection(cliVersionMatch.Package, cliVersionMatch.Channel);
        }

        // If channel was specified via --channel option or per-project aspire.config.json
        // (but no --version), automatically select the highest version from that channel
        // without prompting.
        if (!string.IsNullOrEmpty(query.RequestedChannel))
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

    private static bool HasInstalledLocalBuildPackageSource(PackageChannel channel)
    {
        return VersionHelper.IsLocalBuildChannel(channel.Name) &&
            channel.Mappings?.Any(static mapping => mapping.IsAspireDirectoryMapping) == true;
    }

    /// <summary>
    /// Installs the resolved Aspire project templates package, generating a temporary NuGet.config from source-adjusted mappings when needed.
    /// </summary>
    /// <param name="selection">The template package + channel returned by <see cref="ResolveTemplatePackageAsync"/>.</param>
    /// <param name="sourceOverride">Optional package source override applied to Aspire packages for installation.</param>
    /// <param name="runner">The .NET CLI runner used to invoke <c>dotnet new install</c>. Passed in (rather than injected) because the runner has a transient DI lifetime.</param>
    /// <param name="statusMessage">Status text shown while the install runs.</param>
    /// <param name="statusEmoji">Optional emoji prefix shown next to the status message.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The install exit code, the parsed template version (if available), and the captured stdout/stderr lines.</returns>
    public async Task<TemplateInstallOutcome> InstallTemplatePackageAsync(
        TemplatePackageSelection selection,
        string? sourceOverride,
        IDotNetCliRunner runner,
        string statusMessage,
        KnownEmoji? statusEmoji,
        CancellationToken cancellationToken)
    {
        using var installationConfiguration = await selection.Channel.CreatePackageOperationConfigurationAsync(
            executionContext.WorkingDirectory,
            sourceOverride,
            cancellationToken);

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
                    // dotnet new install has no --configfile option. Running from the generated
                    // overlay directory lets NuGet discover the overlay and continue walking the
                    // original workspace hierarchy for ambient sources and credentials.
                    nugetConfigFile: installationConfiguration.ConfigurationFile,
                    nugetSource: string.IsNullOrWhiteSpace(sourceOverride) ? selection.Package.Source : sourceOverride,
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
/// <param name="RequestedChannel">
/// The user/project-side channel request — either from <c>--channel</c>, per-project
/// <c>aspire.config.json#channel</c>, or (for <c>aspire init</c> only) the running CLI's
/// <see cref="CliExecutionContext.IdentityChannel"/>. When null, channel selection falls
/// back to PR-hive discovery or implicit-only depending on <paramref name="IncludePrHives"/>.
/// </param>
/// <param name="VersionOverride">Optional explicit template version (e.g. from <c>--version</c>).</param>
/// <param name="SourceOverride">
/// Optional package source override used exclusively for template discovery and installation. Without
/// <paramref name="RequestedChannel"/>, the implicit channel owns the result so installed hives cannot
/// assign an unrelated channel identity to a package discovered from this source.
/// </param>
/// <param name="IncludePrHives">When true (e.g. for <c>aspire new</c>), local PR hive directories under <c>~/.aspire/hives</c> participate in channel discovery; when false (e.g. for <c>aspire init</c>), they are ignored.</param>
internal sealed record TemplatePackageQuery(
    string? RequestedChannel,
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
