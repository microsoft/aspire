// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Templating;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Commands;

internal sealed class NewCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    protected override bool UpdateNotificationsEnabled => true;

    private readonly INewCommandPrompter _prompter;
    private readonly ITemplateProvider _templateProvider;
    private readonly ITemplate[] _templates;
    private readonly IPackagingService _packagingService;
    private readonly AgentInitCommand _agentInitCommand;
    private readonly ICliHostEnvironment _hostEnvironment;

    internal static readonly Option<string?> s_nameOption = new("--name", "-n")
    {
        Description = NewCommandStrings.NameArgumentDescription,
        Recursive = true
    };
    internal static readonly Option<string?> s_outputOption = new("--output", "-o")
    {
        Description = NewCommandStrings.OutputArgumentDescription,
        Recursive = true
    };
    private static readonly Option<string?> s_sourceOption = new("--source", "-s")
    {
        Description = NewCommandStrings.SourceArgumentDescription,
        Recursive = true
    };
    private static readonly Option<string?> s_versionOption = new("--version")
    {
        Description = NewCommandStrings.VersionArgumentDescription,
        Recursive = true
    };

    internal static readonly Option<bool?> s_suppressAgentInitOption = new("--suppress-agent-init")
    {
        Description = SharedCommandStrings.AgentInitOptionDescription,
        Recursive = true
    };

    private readonly Option<string?> _channelOption;
    private readonly Option<string?> _languageOption;

    /// <summary>
    /// NewCommand prefetches both template and CLI package metadata.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => true;

    /// <summary>
    /// NewCommand prefetches CLI package metadata for update notifications.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => true;

    public NewCommand(
        INewCommandPrompter prompter,
        ITemplateProvider templateProvider,
        IPackagingService packagingService,
        AgentInitCommand agentInitCommand,
        ICliHostEnvironment hostEnvironment,
        IConfiguration configuration,
        CommonCommandServices services)
        : base("new", NewCommandStrings.Description, services)
    {
        _prompter = prompter;
        _templateProvider = templateProvider;
        _packagingService = packagingService;
        _agentInitCommand = agentInitCommand;
        _hostEnvironment = hostEnvironment;

        Options.Add(s_nameOption);
        Options.Add(s_outputOption);
        Options.Add(s_sourceOption);
        Options.Add(s_versionOption);
        Options.Add(s_suppressAgentInitOption);

        // Customize description based on whether staging channel is enabled
        var isStagingEnabled = KnownFeatures.IsStagingChannelEnabled(services.Features, configuration)
            || string.Equals(ExecutionContext.IdentityChannel, PackageChannelNames.Staging, StringComparisons.ChannelName);
        _channelOption = new Option<string?>("--channel")
        {
            Description = isStagingEnabled
                ? NewCommandStrings.ChannelOptionDescriptionWithStaging
                : NewCommandStrings.ChannelOptionDescription,
            Recursive = true
        };
        Options.Add(_channelOption);

        _languageOption = new Option<string?>("--language")
        {
            Description = NewCommandStrings.LanguageOptionDescription,
            Recursive = true
        };
        Options.Add(_languageOption);

        // Register template definitions as subcommands synchronously.
        // This uses GetTemplates() which returns template definitions without
        // performing any async I/O (e.g. SDK availability checks). Runtime
        // availability is checked in ExecuteAsync via GetTemplatesAsync().
        _templates = templateProvider.GetTemplates().ToArray();

        foreach (var template in _templates)
        {
            var templateCommand = new TemplateCommand(template, ExecuteAsync, services);
            Subcommands.Add(templateCommand);
        }
    }

    private string? ParseExplicitLanguageId(ParseResult parseResult)
    {
        var explicitLanguageId = parseResult.GetValue(_languageOption);
        return string.IsNullOrWhiteSpace(explicitLanguageId) ? null : NormalizeLanguageId(explicitLanguageId);
    }

    private static string NormalizeLanguageId(string languageId)
    {
        return languageId.Equals(KnownLanguageId.TypeScriptAlias, StringComparison.OrdinalIgnoreCase)
            ? KnownLanguageId.TypeScript
            : languageId;
    }

    private static string GetLanguageDisplayName(string languageId)
    {
        return NormalizeLanguageId(languageId) switch
        {
            KnownLanguageId.CSharp => KnownLanguageId.CSharpDisplayName,
            KnownLanguageId.TypeScript => "TypeScript (Node.js)",
            KnownLanguageId.Python => KnownLanguageId.PythonDisplayName,
            KnownLanguageId.Go => KnownLanguageId.GoDisplayName,
            KnownLanguageId.Java => KnownLanguageId.JavaDisplayName,
            KnownLanguageId.Rust => KnownLanguageId.RustDisplayName,
            _ => languageId
        };
    }

    private async Task<string> PromptForAppHostLanguageAsync(IReadOnlyList<string> selectableLanguages, CancellationToken cancellationToken)
    {
        var choices = selectableLanguages
            .Select(NormalizeLanguageId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static languageId => (LanguageId: languageId, DisplayName: GetLanguageDisplayName(languageId)))
            .ToArray();

        var selected = await InteractionService.PromptForSelectionAsync(
            "Which language would you like to use?",
            choices,
            choice => choice.DisplayName.EscapeMarkup(),
            cancellationToken: cancellationToken);

        return selected.LanguageId;
    }

    private static NuGetPackage? TryGetCurrentCliTemplateVersionPackage(PackageChannel selectedChannel, NuGetPackage[] packages, bool hasPrHives)
    {
        if (VersionHelper.TryGetCurrentCliVersionMatch(
            packages,
            p => p.Version,
            out var cliVersionPackage,
            channelName: selectedChannel.Name,
            hasPrHives: hasPrHives))
        {
            return cliVersionPackage;
        }

        if (packages.Length > 0 &&
            selectedChannel.Type is PackageChannelType.Explicit &&
            !string.Equals(selectedChannel.Name, PackageChannelNames.Stable, StringComparisons.ChannelName) &&
            !VersionHelper.IsLocalBuildChannel(selectedChannel.Name))
        {
            // Prerelease channels (daily, staging) filter the shipped stable package out of channel
            // search even when the channel's feed mappings can still restore it (they fall back to
            // nuget.org). For those channels, pinning to the running CLI's SDK version keeps the
            // bundled server and the restored Aspire packages in lock-step. Without this, `aspire new
            // --channel daily` on a shipped 13.4 CLI floats templates to a 13.5 daily preview, which
            // then breaks the bundled 13.4 AppHost server with `Aspire.TypeSystem, Version=13.5.0.0`
            // assembly load errors followed by `No language support found for: typescript/nodejs`.
            //
            // The stable channel is excluded here on purpose: it does not apply that filter, so a
            // "no exact match" outcome means the CLI version is genuinely not published on the stable
            // feed (the CLI is daily-shape, staging-shape, or PR-shape `13.4.0-pr.X.gY`). Forcing
            // it through would either contradict the user's explicit `--channel stable` request or
            // write an unpublishable version into `apphost.cs` that NuGet restore cannot satisfy.
            // Fall through to the OrderByDescending picker so the user gets the highest shipped
            // stable package they actually asked for.
            return new NuGetPackage
            {
                Id = TemplateNuGetConfigService.TemplatesPackageName,
                Version = VersionHelper.GetDefaultSdkVersion(),
                Source = selectedChannel.SourceDetails
            };
        }

        return null;
    }

    private async Task<(bool Success, string? LanguageId)> ResolveSelectedLanguageAsync(ITemplate template, ParseResult parseResult, CancellationToken cancellationToken)
    {
        var explicitLanguageId = ParseExplicitLanguageId(parseResult);

        if (template.SelectableAppHostLanguages.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(explicitLanguageId) && !template.SupportsLanguage(explicitLanguageId))
            {
                InteractionService.DisplayError($"Template '{template.Name}' does not support language '{explicitLanguageId}'.");
                return (false, null);
            }

            return (true, explicitLanguageId ?? template.LanguageId);
        }

        if (!string.IsNullOrWhiteSpace(explicitLanguageId))
        {
            var normalizedExplicitLanguageId = NormalizeLanguageId(explicitLanguageId);
            if (!template.SelectableAppHostLanguages.Any(l => l.Equals(normalizedExplicitLanguageId, StringComparison.OrdinalIgnoreCase)))
            {
                InteractionService.DisplayError($"Template '{template.Name}' does not support language '{explicitLanguageId}'.");
                return (false, null);
            }

            return (true, normalizedExplicitLanguageId);
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            return (true, NormalizeLanguageId(template.SelectableAppHostLanguages[0]));
        }

        var selectedLanguageId = await PromptForAppHostLanguageAsync(template.SelectableAppHostLanguages, cancellationToken);
        return (true, selectedLanguageId);
    }

    private ITemplate[] GetTemplatesForTemplateArgument(ITemplate[] availableTemplates, ParseResult parseResult)
    {
        var explicitLanguageId = ParseExplicitLanguageId(parseResult);
        var templates = availableTemplates.ToList();

        if (!string.IsNullOrWhiteSpace(explicitLanguageId))
        {
            templates = templates
                .Where(t => t.SupportsLanguage(explicitLanguageId))
                .ToList();
        }

        // Sort templates alphabetically by description, keeping empty templates at the end
        templates.Sort((a, b) =>
        {
            var aIsEmpty = a.IsEmpty;
            var bIsEmpty = b.IsEmpty;

            if (aIsEmpty != bIsEmpty)
            {
                return aIsEmpty ? 1 : -1;
            }

            return string.Compare(a.Description, b.Description, StringComparison.OrdinalIgnoreCase);
        });

        return templates.ToArray();
    }

    private ITemplate[] GetTemplatesForPrompt(ITemplate[] availableTemplates, ParseResult parseResult)
    {
        return GetTemplatesForTemplateArgument(availableTemplates, parseResult)
            .Where(static t => t.ShowInPrompt)
            .ToArray();
    }

    private async Task<ITemplate?> GetProjectTemplateAsync(ITemplate[] availableTemplates, ParseResult parseResult, CancellationToken cancellationToken)
    {
        // If a subcommand was matched (e.g., aspire new aspire-starter), find the template by command name
        if (parseResult.CommandResult.Command != this)
        {
            var subcommandTemplate = availableTemplates.SingleOrDefault(t => t.Name.Equals(parseResult.CommandResult.Command.Name, StringComparison.OrdinalIgnoreCase));
            if (subcommandTemplate is not null)
            {
                return subcommandTemplate;
            }

            // The template subcommand was parsed successfully but the template is
            // not available at runtime (e.g. .NET SDK is not installed).
            InteractionService.DisplayError($"Template '{parseResult.CommandResult.Command.Name}' is not available. Ensure the required runtime is installed.");
            return null;
        }

        var templatesForTemplateArgument = GetTemplatesForTemplateArgument(availableTemplates, parseResult);
        if (templatesForTemplateArgument.Length == 0)
        {
            InteractionService.DisplayError("No templates are available for the current environment.");
            return null;
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            InteractionService.DisplayError(NewCommandStrings.NonInteractiveTemplateRequired);
            var templateNames = string.Join(", ", templatesForTemplateArgument.Select(t => t.Name));
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveAvailableValues, templateNames));
            throw new NonInteractiveException("template");
        }

        var templatesForPrompt = GetTemplatesForPrompt(availableTemplates, parseResult);
        if (templatesForPrompt.Length == 0)
        {
            InteractionService.DisplayError("No templates are available for the current environment.");
            return null;
        }

        var result = await _prompter.PromptForTemplateAsync(templatesForPrompt, cancellationToken);

        return result;
    }

    private sealed class ResolveTemplateVersionResult
    {
        public string? Version { get; init; }

        public string? ChannelName { get; init; }

        [MemberNotNullWhen(true, nameof(Version))]
        [MemberNotNullWhen(false, nameof(ErrorMessage))]
        public bool Success => Version is not null;

        public string? ErrorMessage { get; init; }
    }

    private async Task<ResolveTemplateVersionResult> ResolveCliTemplateVersionAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await InteractionService.ShowStatusAsync(
            NewCommandStrings.ResolvingTemplateVersion,
            async () =>
            {
                var configuredChannelName = parseResult.GetValue(_channelOption);
                var channels = await _packagingService.GetChannelsAsync(cancellationToken, configuredChannelName);

                // When no --channel was passed, prefer the channel whose name matches the running
                // CLI's identity (CliExecutionContext.IdentityChannel — stable, staging, daily,
                // local, or pr-<N>) over the Implicit (nuget.org) channel. This keeps the
                // resolved template package and the channel pinned into aspire.config.json
                // mutually satisfiable: a daily CLI scaffolds a daily-channel project whose
                // prerelease SDK version is reachable through the daily channel's Package Source
                // Mapping (Aspire.* → dnceng), a stable CLI scaffolds a stable project whose
                // stable SDK version is reachable through nuget.org, and so on. The opposite
                // outcome — resolving against Implicit while pinning channel to the identity —
                // makes restore reject the prerelease/stable mismatch with "Unable to find a
                // stable package Aspire.Hosting with version (>= …)".
                //
                // Falls back to the Implicit channel when the identity doesn't match any
                // registered channel (e.g. typoed override, future identity name) so the
                // command stays useful while surfacing a deterministic version.
                PackageChannel? identityChannelMatch = null;
                if (string.IsNullOrWhiteSpace(configuredChannelName) &&
                    !string.IsNullOrWhiteSpace(ExecutionContext.IdentityChannel))
                {
                    identityChannelMatch = channels.FirstOrDefault(c =>
                        string.Equals(c.Name, ExecutionContext.IdentityChannel, StringComparisons.ChannelName));
                }

                var selectedChannel = string.IsNullOrWhiteSpace(configuredChannelName)
                    ? identityChannelMatch
                        ?? channels.FirstOrDefault(c => c.Type is PackageChannelType.Implicit)
                        ?? channels.FirstOrDefault()
                    : channels.FirstOrDefault(c => string.Equals(c.Name, configuredChannelName, StringComparisons.ChannelName));

                if (selectedChannel is null)
                {
                    string errorMessage;
                    if (string.IsNullOrWhiteSpace(configuredChannelName))
                    {
                        errorMessage = NewCommandStrings.NoPackageChannelsAvailable;
                    }
                    else if (string.Equals(configuredChannelName, PackageChannelNames.Staging, StringComparisons.ChannelName)
                        && _packagingService.GetStagingChannelUnavailableReason() is { } stagingReason)
                    {
                        // Surface the actionable packaging-service reason (e.g. "daily CLI cannot
                        // synthesize a staging channel; set overrideStagingFeed") instead of the
                        // generic channel list, mirroring UpdateCommand's behavior.
                        // See https://github.com/microsoft/aspire/issues/16652.
                        errorMessage = stagingReason;
                    }
                    else
                    {
                        errorMessage = string.Format(
                            CultureInfo.CurrentCulture,
                            NewCommandStrings.NoChannelFoundMatching,
                            configuredChannelName,
                            string.Join(", ", channels.Select(c => c.Name)));
                    }

                    return new ResolveTemplateVersionResult { ErrorMessage = errorMessage };
                }

                try
                {
                    var packages = (await selectedChannel.GetTemplatePackagesAsync(ExecutionContext.WorkingDirectory, cancellationToken))
                        .Where(p => Semver.SemVersion.TryParse(p.Version, Semver.SemVersionStyles.Strict, out _))
                        .ToArray();
                    var hasPrHives = ExecutionContext.GetHiveCount() > 0;

                    var package = TryGetCurrentCliTemplateVersionPackage(selectedChannel, packages, hasPrHives);

                    package ??= packages
                        .OrderByDescending(p => Semver.SemVersion.Parse(p.Version, Semver.SemVersionStyles.Strict), Semver.SemVersion.PrecedenceComparer)
                        .FirstOrDefault();

                    if (package is null)
                    {
                        return new ResolveTemplateVersionResult { ErrorMessage = $"No template versions found in channel '{selectedChannel.Name}'." };
                    }

                    // Only persist explicit channel names (e.g. local, daily) — implicit channels
                    // (stable/nuget.org) should not be written so aspire add uses its default behavior.
                    var channelName = selectedChannel.Type is PackageChannelType.Explicit ? selectedChannel.Name : null;

                    return new ResolveTemplateVersionResult { Version = package.Version, ChannelName = channelName };
                }
                catch (NuGetPackageCacheException ex)
                {
                    return new ResolveTemplateVersionResult { ErrorMessage = ex.Message };
                }
            });
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(this.Name);

        var source = parseResult.GetValue(s_sourceOption);
        if (!string.IsNullOrWhiteSpace(source) && PackageSourceOverrideMappings.HasCredentialMaterial(source))
        {
            InteractionService.DisplayError(NewCommandStrings.SourceWithCredentialsCannotBePersisted);
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        // Resolve which templates are actually available at runtime (performs
        // async checks like SDK availability). This may be a subset of the
        // templates registered as subcommands.
        var availableTemplates = (await _templateProvider.GetTemplatesAsync(cancellationToken)).ToArray();

        var template = await GetProjectTemplateAsync(availableTemplates, parseResult, cancellationToken);
        if (template is null)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        var (languageResolutionSuccess, selectedLanguageId) = await ResolveSelectedLanguageAsync(template, parseResult, cancellationToken);
        if (!languageResolutionSuccess)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand);
        }

        var version = parseResult.GetValue(s_versionOption);
        // Precedence for the channel written into TemplateInputs.Channel:
        //   1. Explicit --channel argument (user override always wins).
        //   2. Channel returned by ResolveCliTemplateVersionAsync (CLI-runtime templates).
        //   3. The running CLI's IdentityChannel, when it matches a registered Explicit
        //      channel — needed for TemplateRuntime.DotNet starters (aspire-starter,
        //      aspire-starter-csharp-typescript) which otherwise resolve
        //      Aspire.ProjectTemplates from the Implicit (nuget.org) channel regardless
        //      of CLI identity, and also for CLI-runtime templates invoked with --version
        //      which short-circuits the resolver below.
        string? resolvedChannelName = null;
        if (ShouldResolveCliTemplateVersion(template) &&
            string.IsNullOrWhiteSpace(version))
        {
            var resolveResult = await ResolveCliTemplateVersionAsync(parseResult, cancellationToken);
            if (!resolveResult.Success)
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, resolveResult.ErrorMessage);
            }

            version = resolveResult.Version;
            resolvedChannelName = resolveResult.ChannelName;
        }

        // Apply the channel precedence as a single coalesce. The identity fallback lives
        // here, not inside ResolveCliTemplateVersionAsync, because that resolver only runs
        // on the CLI-runtime / no-explicit-version branch above. The two paths that need
        // the identity hint are precisely the ones the resolver does NOT visit:
        //   * TemplateRuntime.DotNet templates (aspire-starter family) — the bug this fix
        //     addresses; without forwarding, DotNetTemplateFactory searches only the
        //     Implicit (nuget.org) channel regardless of CLI identity.
        //   * CLI-runtime templates invoked with --version, which short-circuits the
        //     resolver and would otherwise leave inputs.Channel null.
        // Keeping the fallback out of the resolver also keeps the resolver's role narrow:
        // it performs version negotiation across channels and reports the channel that won;
        // the identity hint is a different policy ("label the project with the CLI's own
        // channel") that should not influence version selection.
        resolvedChannelName = parseResult.GetValue(_channelOption)
            ?? resolvedChannelName
            ?? await ResolveIdentityChannelNameAsync(cancellationToken);

        var inputs = new TemplateInputs
        {
            Name = parseResult.GetValue(s_nameOption),
            Output = parseResult.GetValue(s_outputOption),
            Source = source,
            Version = version,
            Channel = resolvedChannelName,
            Language = selectedLanguageId
        };
        var templateResult = await template.ApplyTemplateAsync(inputs, parseResult, cancellationToken);

        var workspaceRoot = new DirectoryInfo(templateResult.OutputPath ?? ExecutionContext.WorkingDirectory.FullName);
        var agentInitBinding = PromptBinding.CreateInvertedBoolConfirm(parseResult, s_suppressAgentInitOption, defaultValue: true);
        // The template already produced the AppHost, so don't pre-select the one-time aspireify
        // wiring skill — users can still opt into it from the prompt.
        var agentInitResult = await _agentInitCommand.PromptAndChainAsync(InteractionService, templateResult.ExitCode, workspaceRoot, agentInitBinding, cancellationToken, AgentInitCommand.ExcludeOneTimeSetupSkillsFromDefaults);

        if (templateResult.OutputPath is not null && ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _))
        {
            extensionInteractionService.OpenEditor(templateResult.OutputPath);
        }

        return CommandResult.FromExitCode(agentInitResult.ExitCode);
    }

    private static bool ShouldResolveCliTemplateVersion(ITemplate template)
    {
        return template.Runtime is TemplateRuntime.Cli;
    }

    /// <summary>
    /// Resolves <see cref="CliExecutionContext.IdentityChannel"/> to a registered channel name
    /// from the packaging service. Returns the channel name when an Explicit channel matches the
    /// identity (e.g. <c>daily</c>, <c>staging</c>, <c>stable</c>, <c>pr-&lt;N&gt;</c>); returns
    /// <see langword="null"/> when there is no identity, when no Explicit channel matches, or
    /// when only the Implicit (nuget.org) channel is registered. A <see langword="null"/> result
    /// intentionally lets the downstream template path consult the Implicit channel and avoids
    /// writing a per-project channel pin into the new project's NuGet configuration.
    /// </summary>
    private async Task<string?> ResolveIdentityChannelNameAsync(CancellationToken cancellationToken)
    {
        var identity = ExecutionContext.IdentityChannel;
        if (string.IsNullOrWhiteSpace(identity))
        {
            return null;
        }

        var channels = await _packagingService.GetChannelsAsync(cancellationToken, identity);
        var match = channels.FirstOrDefault(c =>
            string.Equals(c.Name, identity, StringComparisons.ChannelName));

        // Only persist Explicit channel names — Implicit channels (the nuget.org fallback)
        // are deliberately left unpinned so `aspire add` and later restores use ambient
        // NuGet configuration. Mirrors the same rule applied at the end of
        // ResolveCliTemplateVersionAsync.
        return match is { Type: PackageChannelType.Explicit } ? match.Name : null;
    }
}

internal interface INewCommandPrompter
{
    Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken);
    Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken);
    Task<string> PromptForOutputPath(string v, ParseResult parseResult, Func<string, ValidationResult>? validator = null, CancellationToken cancellationToken = default, Func<string, string>? outputPathResolver = null);
}

internal interface ITemplateVersionPrompter
{
    /// <summary>
    /// Prompts the user to select a templates package version.
    /// </summary>
    /// <param name="candidatePackages">The available templates package candidates grouped across channels.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The selected templates package and channel.</returns>
    Task<(NuGetPackage Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(NuGetPackage Package, PackageChannel Channel)> candidatePackages, CancellationToken cancellationToken);
}

internal class NewCommandPrompter(IInteractionService interactionService) : INewCommandPrompter, ITemplateVersionPrompter
{
    public virtual async Task<(NuGetPackage Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(IEnumerable<(NuGetPackage Package, PackageChannel Channel)> candidatePackages, CancellationToken cancellationToken)
    {
        // Check if we should skip the channel selection prompt
        // Skip prompt if there are no explicit channels (only the implicit/default channel)
        var byChannel = candidatePackages
            .GroupBy(cp => cp.Channel)
            .ToArray();

        var implicitGroup = byChannel.FirstOrDefault(g => g.Key.Type is Packaging.PackageChannelType.Implicit);
        var explicitGroups = byChannel
            .Where(g => g.Key.Type is Packaging.PackageChannelType.Explicit)
            .ToArray();

        // If there are no explicit channels, automatically select from the implicit channel
        if (explicitGroups.Length == 0 && implicitGroup is not null)
        {
            // Return the highest version from the implicit channel
            return implicitGroup.OrderByDescending(p => Semver.SemVersion.Parse(p.Package.Version), Semver.SemVersion.PrecedenceComparer).First();
        }

        // Create a hierarchical selection experience:
        // - Top-level: all packages from the implicit channel (if any)
        // - Then: one entry per remaining channel that opens a sub-menu with that channel's packages

        // Local helpers
        static string FormatPackageLabel((NuGetPackage Package, PackageChannel Channel) item)
        {
            // Keep it concise: "Version (source)"
            return $"{item.Package.Version.EscapeMarkup()} ({item.Channel.SourceDetails.EscapeMarkup()})";
        }

        async Task<(NuGetPackage Package, PackageChannel Channel)> PromptForChannelPackagesAsync(
            PackageChannel channel,
            IEnumerable<(NuGetPackage Package, PackageChannel Channel)> items,
            CancellationToken ct)
        {
            // Show a sub-menu for this channel's packages
            var packageChoices = items
                .Select(i => (
                    Label: FormatPackageLabel(i),
                    Result: i
                ))
                .ToArray();

            var selection = await interactionService.PromptForSelectionAsync(
                NewCommandStrings.SelectATemplateVersion,
                packageChoices,
                c => c.Label,
                cancellationToken: ct);

            return selection.Result;
        }

        // Build the root menu as tuples of (label, action)
        var rootChoices = new List<(string Label, Func<CancellationToken, Task<(NuGetPackage, PackageChannel)>> Action)>();

        if (implicitGroup is not null)
        {
            // Add each implicit package directly to the root
            foreach (var item in implicitGroup)
            {
                var captured = item; // avoid modified-closure issues
                rootChoices.Add((
                    Label: FormatPackageLabel((captured.Package, captured.Channel)),
                    Action: ct => Task.FromResult((captured.Package, captured.Channel))
                ));
            }
        }

        // Add a submenu entry for each explicit channel
        foreach (var channelGroup in explicitGroups)
        {
            var channel = channelGroup.Key;
            var items = channelGroup.ToArray();

            rootChoices.Add((
                Label: channel.Name.EscapeMarkup(),
                Action: ct => PromptForChannelPackagesAsync(channel, items, ct)
            ));
        }

        // If for some reason we have no choices, fall back to the first candidate
        if (rootChoices.Count == 0)
        {
            return candidatePackages.First();
        }

        // Prompt user for the top-level selection
        var topSelection = await interactionService.PromptForSelectionAsync(
            NewCommandStrings.SelectATemplateVersion,
            rootChoices,
            c => c.Label,
            cancellationToken: cancellationToken);

        return await topSelection.Action(cancellationToken);
    }

    public virtual async Task<string> PromptForOutputPath(string path, ParseResult parseResult, Func<string, ValidationResult>? validator = null, CancellationToken cancellationToken = default, Func<string, string>? outputPathResolver = null)
    {
        var resolvedValidator = validator;
        if (validator is not null && outputPathResolver is not null)
        {
            resolvedValidator = candidatePath => validator(outputPathResolver(candidatePath));
        }

        var outputPath = await interactionService.PromptForFilePathAsync(
            NewCommandStrings.EnterTheOutputPath,
            validator: resolvedValidator,
            binding: PromptBinding.Create(parseResult, NewCommand.s_outputOption, path),
            directory: true,
            cancellationToken: cancellationToken
            );

        return outputPathResolver?.Invoke(outputPath) ?? outputPath;
    }

    public virtual async Task<string> PromptForProjectNameAsync(string defaultName, ParseResult parseResult, CancellationToken cancellationToken)
    {
        return await interactionService.PromptForStringAsync(
            NewCommandStrings.EnterTheProjectName,
            binding: PromptBinding.Create(parseResult, NewCommand.s_nameOption, defaultName),
            validator: name => ProjectNameValidator.IsProjectNameValid(name)
                ? ValidationResult.Success()
                : ValidationResult.Error(NewCommandStrings.InvalidProjectName),
            cancellationToken: cancellationToken);
    }

    public virtual async Task<ITemplate> PromptForTemplateAsync(ITemplate[] validTemplates, CancellationToken cancellationToken)
    {
        return await interactionService.PromptForSelectionAsync(
            NewCommandStrings.SelectAProjectTemplate,
            validTemplates,
            t => t.Description.EscapeMarkup(),
            cancellationToken: cancellationToken
        );
    }
}

internal static partial class ProjectNameValidator
{
    // Regex for project name validation:
    // - Can be any characters except path separators (/ and \)
    // - Length: 1-254 characters
    // - Must not be empty or whitespace only
    [GeneratedRegex(@"^[^/\\]{1,254}$", RegexOptions.Compiled)]
    internal static partial Regex GetProjectNameRegex();

    public static bool IsProjectNameValid(string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return false;
        }

        var regex = GetProjectNameRegex();
        return regex.IsMatch(projectName);
    }
}
