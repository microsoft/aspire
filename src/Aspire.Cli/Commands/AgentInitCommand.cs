// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand
{
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly IAspireSkillsInstaller _aspireSkillsInstaller;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ITelemetryHookConfigurator _telemetryHookConfigurator;
    private readonly IEnvironment _environment;

    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAspireSkillsInstaller aspireSkillsInstaller,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        ITelemetryHookConfigurator telemetryHookConfigurator,
        IEnvironment environment,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _aspireSkillsInstaller = aspireSkillsInstaller;
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _languageDiscovery = languageDiscovery;
        _telemetryHookConfigurator = telemetryHookConfigurator;
        _environment = environment;

        Options.Add(s_workspaceRootOption);
        Options.Add(s_skillLocationsOption);
        Options.Add(s_skillsOption);
        Options.Add(s_mcpsOption);
    }

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Skill).Select(static location => location.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", AgentAssetCatalog.GetFileAssets(AgentAssetKind.Skill).Select(static asset => asset.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_mcpsOption = new("--mcps")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_McpsOptionDescription,
            string.Join(",", AgentAssetCatalog.GetActionAssets(AgentAssetKind.Mcp).Select(static asset => asset.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    private static readonly FileAssetKindConfiguration s_skillAssets = new(
        AgentAssetKind.Skill,
        AgentCommandStrings.InitCommand_SelectSkillLocations,
        AgentCommandStrings.InitCommand_SelectSkills,
        AgentCommandStrings.InitCommand_FailedToInstallSkill,
        AgentCommandStrings.InitCommand_InstalledSkillsSummary,
        AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
        AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations,
        AgentCommandStrings.InitCommand_NoDetectedClientForSkills);

    private static readonly ActionAssetKindConfiguration s_mcpAssets = new(
        AgentAssetKind.Mcp,
        AgentCommandStrings.InitCommand_SelectMcpServers,
        AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments,
        AgentCommandStrings.InitCommand_NoDetectedClientForMcp,
        AgentCommandStrings.InitCommand_NoCompatibleClientForSelectedMcp);

    /// <summary>
    /// Public entry point for executing the init command.
    /// This allows McpInitCommand to delegate to this implementation.
    /// </summary>
    internal Task<CommandResult> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to run agent init after a successful command, then chains into agent init if accepted.
    /// Used by commands (e.g. <c>aspire init</c>, <c>aspire new</c>) to offer agent init as a follow-up step.
    /// When <paramref name="selectByDefault"/> is <see langword="null"/> every bundle-sourced skill is
    /// pre-selected, which is what <c>aspire init</c> wants because aspireify is the natural follow-up.
    /// Other callers (e.g. <c>aspire new</c>) can pass a predicate to additionally filter out skills that
    /// don't fit their context (such as one-time setup skills after a template has already produced the AppHost).
    /// Callers that expose <c>--skill-locations</c>, <c>--skills</c>, and <c>--mcps</c> can pass
    /// <paramref name="skillLocationsBinding"/>, <paramref name="skillsBinding"/>, and
    /// <paramref name="mcpsBinding"/> so the chained execution reuses the same non-interactive
    /// selection semantics as standalone <c>aspire agent init</c>.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> mcpsBinding,
        Func<AgentFileAssetDefinition, bool>? selectByDefault,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != CliExitCodes.Success)
        {
            return AgentInitExecutionResult.Empty(previousResultExitCode);
        }

        // Add a separating line between prompt and previous work in aspire new and aspire init.
        interactionService.DisplayEmptyLine();

        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(workspaceRoot, selectByDefault, skillLocationsBinding, skillsBinding, mcpsBinding, cancellationToken);
        }

        return AgentInitExecutionResult.Empty(CliExitCodes.Success);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        // Standalone `aspire agent init` is typically run against an existing project, so don't
        // pre-select the one-time aspireify wiring skill even though every other bundle skill
        // is default-on. Users can still opt into it from the prompt or via --skills.
        var skillLocationsBinding = PromptBinding.Create(parseResult, s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, s_skillsOption);
        var mcpsBinding = PromptBinding.Create(parseResult, s_mcpsOption);
        var result = await ExecuteAgentInitAsync(workspaceRoot, ExcludeOneTimeSetupAssetsFromDefaults, skillLocationsBinding, skillsBinding, mcpsBinding, cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    /// <summary>
    /// Names of bundle skills that perform one-time workspace setup and should NOT be
    /// pre-selected after a workspace was just produced by a template flow such as
    /// <c>aspire new</c> or after standalone <c>aspire agent init</c> (typically run
    /// against an existing project).
    /// </summary>
    /// <remarks>
    /// This is the single source of truth the CLI consults when filtering bundle skills out
    /// of the auto-preselection set. All bundle skills are default-on, so if the bundle ships
    /// a new wiring or bootstrap-style skill that should NOT auto-run in an already-bootstrapped
    /// workspace, add its name here.
    /// </remarks>
    internal static readonly IReadOnlySet<string> s_oneTimeSetupSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CommonAgentApplicators.AspireifySkillName,
    };

    /// <summary>
    /// Default-skill predicate used by flows that do not want one-time setup skills
    /// pre-selected — namely <c>aspire new</c> (template already created the AppHost) and
    /// standalone <c>aspire agent init</c> (typically run against an existing project).
    /// Skills filtered here remain available to opt into from the prompt or via <c>--skills</c>.
    /// </summary>
    internal static bool ExcludeOneTimeSetupAssetsFromDefaults(AgentFileAssetDefinition asset)
        => asset.IsDefault && !s_oneTimeSetupSkillNames.Contains(asset.Name);

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Try to discover the git repository root to use as the default workspace root
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;

        // Prompt the user for the workspace root
        var workspaceRootPath = await InteractionService.PromptForFilePathAsync(
            McpCommandStrings.InitCommand_WorkspaceRootPrompt,
            binding: PromptBinding.Create(parseResult, s_workspaceRootOption, defaultWorkspaceRoot.FullName),
            validator: path =>
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return ValidationResult.Error(McpCommandStrings.InitCommand_WorkspaceRootRequired);
                }

                if (!Directory.Exists(path))
                {
                    return ValidationResult.Error(string.Format(CultureInfo.InvariantCulture, McpCommandStrings.InitCommand_WorkspaceRootNotFound, path));
                }

                return ValidationResult.Success();
            },
            directory: true,
            cancellationToken: cancellationToken);

        return new DirectoryInfo(workspaceRootPath);
    }

    private async Task<AgentInitExecutionResult> ExecuteAgentInitAsync(
        DirectoryInfo workspaceRoot,
        Func<AgentFileAssetDefinition, bool>? selectByDefault,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> mcpsBinding,
        CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        var applicators = await InteractionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
            emoji: KnownEmojis.Robot);

        // Detect the AppHost language to determine which skills to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted skills are excluded.
        var detectedLanguage = await _languageDiscovery.DetectLanguageRecursiveAsync(workspaceRoot, cancellationToken);

        // Apply deprecated config migrations silently (these are fixes, not choices)
        var configUpdates = applicators.Where(a => a.PromptGroup == McpInitPromptGroup.ConfigUpdates).ToList();
        var userChoices = applicators.Where(a => a.PromptGroup != McpInitPromptGroup.ConfigUpdates).ToList();

        foreach (var update in configUpdates)
        {
            try
            {
                await update.ApplyAsync(cancellationToken);
                InteractionService.DisplayMessage(KnownEmojis.Wrench, update.Description);
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
            }
        }

        var skillSelection = await SelectFileAssetsAsync(
            s_skillAssets,
            context,
            detectedLanguage,
            skillLocationsBinding,
            skillsBinding,
            selectByDefault,
            ResolveAvailableSkillsAsync,
            cancellationToken);

        // Scanner-discovered applicators are the concrete targets for action-backed assets. They
        // remain separate from file locations and are not exposed as individual user choices.
        var mcpSelection = await SelectActionAssetsAsync(
            s_mcpAssets,
            context,
            userChoices,
            mcpsBinding,
            cancellationToken);

        var hasErrors = await ApplySkillAssetsAsync(
            workspaceRoot,
            context,
            skillSelection,
            cancellationToken);
        hasErrors |= mcpSelection.HasErrors;
        hasErrors |= await ApplyActionAssetsAsync(
            mcpSelection.Assets,
            mcpSelection.Applicators,
            cancellationToken);

        // Install agent telemetry hooks (default-on, parity with azure-skills).
        // Hooks are installed for every detected, supported client. Whether telemetry is actually
        // transmitted stays gated by the single ASPIRE_CLI_TELEMETRY_OPTOUT opt-out, which both the
        // hook scripts and the `aspire agent telemetry` command path re-check at runtime.
        await ConfigureTelemetryHooksAsync(context, cancellationToken);

        if (hasErrors)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.ConfigurationCompletedWithErrors);
        }
        else
        {
            InteractionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }

        return new(
            hasErrors ? CliExitCodes.InvalidCommand : CliExitCodes.Success,
            [.. context.DetectedClients],
            [
                skillSelection.ToResult(),
                mcpSelection.ToResult(),
            ]);
    }

    private async Task<SelectedFileAssets> SelectFileAssetsAsync(
        FileAssetKindConfiguration configuration,
        AgentEnvironmentScanContext context,
        LanguageId? detectedLanguage,
        PromptBinding<string?> locationsBinding,
        PromptBinding<string?> assetsBinding,
        Func<AgentFileAssetDefinition, bool>? selectByDefault,
        Func<LanguageId?, CancellationToken, Task<AvailableFileAssets>> resolveAvailableAssets,
        CancellationToken cancellationToken)
    {
        var availableLocations = AgentAssetLocation.GetLocations(configuration.AssetKind);
        if (availableLocations.Count == 0)
        {
            throw new InvalidOperationException($"File-backed agent asset kind '{configuration.AssetKind}' does not define any installation locations.");
        }

        var hasExplicitSelection =
            PromptBinding.Resolve(locationsBinding).WasProvided ||
            PromptBinding.Resolve(assetsBinding).WasProvided;
        if (!hasExplicitSelection && !HasDetectedClient(context))
        {
            DisplayNoDetectedClientWarning(configuration.NoDetectedClientWarning);
            return SelectedFileAssets.Empty(configuration.AssetKind);
        }

        var defaultLocations = AgentAssetLocation.GetDefaultLocations(
            configuration.AssetKind,
            context.DetectedClients);
        var defaultLocationIds = string.Join(
            ",",
            defaultLocations.Select(static location => location.Id));
        var selectedLocations = await InteractionService.PromptForSelectionsAsync(
            configuration.LocationSelectionPrompt,
            availableLocations,
            location => $"{location.DisplayName} — {location.Description}",
            preSelected: defaultLocations,
            optional: true,
            binding: locationsBinding.WithDefault(defaultLocationIds),
            echoSelected: false,
            cancellationToken: cancellationToken);
        if (selectedLocations.Count == 0)
        {
            return new(configuration.AssetKind, selectedLocations, [], Bundle: null);
        }

        var cliDefinedAssets = AgentAssetCatalog.GetFileAssets(configuration.AssetKind);
        AvailableFileAssets resolvedAssets;
        if (ShouldSkipBundleCatalogResolution(assetsBinding, cliDefinedAssets))
        {
            resolvedAssets = new(
                cliDefinedAssets
                    .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
                    .ToList(),
                Bundle: null,
                FailureMessage: null);
        }
        else
        {
            resolvedAssets = await resolveAvailableAssets(detectedLanguage, cancellationToken);
        }

        var availableAssets = resolvedAssets.Assets
            .OrderBy(static asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defaultAssets = GetDefaultFileAssets(availableAssets, selectByDefault);
        var defaultAssetNames = string.Join(",", defaultAssets.Select(static asset => asset.Name));
        var assetsBindingWithDefault = assetsBinding.WithDefault(defaultAssetNames);

        // An explicit bundle-only name would otherwise be reported merely as an invalid choice.
        // Preserve the resolver failure so the user sees why that asset is absent from the catalog.
        if (resolvedAssets.FailureMessage is not null)
        {
            var (wasProvided, requestedAssets, _) = PromptBinding.Resolve(assetsBindingWithDefault);
            if (wasProvided &&
                requestedAssets is not null &&
                HasUnknownBundleAssetCandidate(requestedAssets, availableAssets, cliDefinedAssets))
            {
                InteractionService.DisplayError(resolvedAssets.FailureMessage);
            }
        }

        var selectedAssets = await InteractionService.PromptForSelectionsAsync(
            configuration.AssetSelectionPrompt,
            availableAssets,
            asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
            preSelected: defaultAssets,
            optional: true,
            binding: assetsBindingWithDefault,
            echoSelected: false,
            cancellationToken: cancellationToken);

        return new(configuration.AssetKind, selectedLocations, selectedAssets, resolvedAssets.Bundle);
    }

    private async Task<SelectedActionAssets> SelectActionAssetsAsync(
        ActionAssetKindConfiguration configuration,
        AgentEnvironmentScanContext context,
        IReadOnlyList<AgentEnvironmentApplicator> userChoices,
        PromptBinding<string?> assetsBinding,
        CancellationToken cancellationToken)
    {
        var applicators = userChoices
            .Where(applicator => applicator.Asset?.AssetKind == configuration.AssetKind)
            .ToList();
        var availableAssets = AgentAssetCatalog.GetActionAssets(configuration.AssetKind);
        var (assetsWereProvided, _, _) = PromptBinding.Resolve(assetsBinding);
        var hasDetectedClient = HasDetectedClient(context);
        if (!hasDetectedClient && !assetsWereProvided)
        {
            DisplayNoDetectedClientWarning(configuration.NoDetectedClientWarning);
            return SelectedActionAssets.Empty(configuration.AssetKind);
        }

        if (applicators.Count == 0 && !assetsWereProvided)
        {
            return SelectedActionAssets.Empty(configuration.AssetKind);
        }

        var selectableAssets = applicators.Count > 0
            ? availableAssets
                .Where(asset => applicators.Any(applicator => ReferenceEquals(applicator.Asset, asset)))
                .ToList()
            : availableAssets;
        IReadOnlyList<AgentActionAssetDefinition> selectedAssets = [];
        if (selectableAssets.Count > 0)
        {
            selectedAssets = await InteractionService.PromptForSelectionsAsync(
                configuration.AssetSelectionPrompt,
                selectableAssets,
                asset => $"[bold]{asset.Description.EscapeMarkup()}[/] [dim]{configuration.TargetDescription.EscapeMarkup()}[/]",
                preSelected: [],
                optional: true,
                binding: assetsBinding.WithDefault(ConsoleInteractionService.NoneChoice),
                echoSelected: false,
                cancellationToken: cancellationToken);
        }

        var hasErrors = !hasDetectedClient && selectedAssets.Count > 0;
        if (hasErrors)
        {
            InteractionService.DisplayError(configuration.NoCompatibleClientError);
        }

        return new(configuration.AssetKind, selectedAssets, applicators, hasErrors);
    }

    private static bool HasDetectedClient(AgentEnvironmentScanContext context)
        => context.DetectedClients.Count > 0;

    private void DisplayNoDetectedClientWarning(string message)
    {
        InteractionService.DisplayMessage(KnownEmojis.Warning, message);
    }

    private async Task<bool> ApplySkillAssetsAsync(
        DirectoryInfo workspaceRoot,
        AgentEnvironmentScanContext context,
        SelectedFileAssets selection,
        CancellationToken cancellationToken)
    {
        foreach (var location in selection.Locations
            .Where(static location => location.Scopes.HasFlag(AgentAssetLocationScope.Workspace)))
        {
            context.AddSkillBaseDirectory(location.RelativeAssetDirectory);
        }

        var hasErrors = await ApplyFileAssetsAsync(
            s_skillAssets,
            workspaceRoot,
            selection,
            cancellationToken);
        var selectedSkillDirectories = selection.Locations
            .Where(static location => location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
            .Select(static location => location.RelativeAssetDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selection.Assets.Contains(AgentAssetCatalog.PlaywrightCli) && selectedSkillDirectories.Count > 0)
        {
            try
            {
                var (status, message) = await _playwrightCliInstaller.InstallAsync(
                    workspaceRoot.FullName,
                    selectedSkillDirectories,
                    cancellationToken);
                switch (status)
                {
                    case PlaywrightInstallStatus.Installed:
                        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, AgentCommandStrings.InitCommand_InstalledPlaywrightCli);
                        break;
                    case PlaywrightInstallStatus.InstalledWithWarnings:
                        InteractionService.DisplayMessage(KnownEmojis.Warning, message!);
                        break;
                    case PlaywrightInstallStatus.Failed:
                        InteractionService.DisplayError(message!);
                        hasErrors = true;
                        break;
                    case PlaywrightInstallStatus.Skipped:
                        InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_PlaywrightCliSkipped);
                        break;
                    default:
                        throw new UnreachableException($"Unexpected PlaywrightInstallStatus: {status}");
                }
            }
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
                hasErrors = true;
            }
        }

        return hasErrors;
    }

    private async Task<bool> ApplyFileAssetsAsync(
        FileAssetKindConfiguration configuration,
        DirectoryInfo workspaceRoot,
        SelectedFileAssets selection,
        CancellationToken cancellationToken)
    {
        if (selection.AssetKind != configuration.AssetKind)
        {
            throw new InvalidOperationException($"Selected asset kind '{selection.AssetKind}' does not match '{configuration.AssetKind}'.");
        }

        var hasErrors = false;
        var installedAssets = new List<InstalledAgentAssetSummaryItem>();
        foreach (var location in selection.Locations)
        {
            if (location.AssetKind != configuration.AssetKind)
            {
                throw new InvalidOperationException($"Agent asset location '{location.Id}' does not support '{configuration.AssetKind}'.");
            }

            foreach (var asset in selection.Assets)
            {
                if (asset.AssetKind != configuration.AssetKind)
                {
                    throw new InvalidOperationException($"Agent asset '{asset.Name}' does not have kind '{configuration.AssetKind}'.");
                }

                // Dedicated installers, such as Playwright, run outside the file-backed pipeline.
                if (!asset.HasInstallableFiles ||
                    asset.SourceKind is AgentFileAssetSourceKind.AspireSkillsBundle && selection.Bundle is null)
                {
                    continue;
                }

                if (location.Scopes.HasFlag(AgentAssetLocationScope.Workspace))
                {
                    var installResult = await InstallAgentAssetAsync(
                        configuration,
                        new(
                            workspaceRoot,
                            location.RelativeAssetDirectory,
                            GetDisplayAgentAssetDirectory(location.RelativeAssetDirectory)),
                        asset,
                        selection.Bundle,
                        cancellationToken);
                    hasErrors |= !installResult.Succeeded;
                    if (installResult.UpdatedAsset is not null)
                    {
                        installedAssets.Add(installResult.UpdatedAsset);
                    }
                }

                if (location.Scopes.HasFlag(AgentAssetLocationScope.User))
                {
                    var installResult = await InstallAgentAssetAsync(
                        configuration,
                        location.ResolveUserInstallTarget(ExecutionContext.HomeDirectory, _environment),
                        asset,
                        selection.Bundle,
                        cancellationToken);
                    hasErrors |= !installResult.Succeeded;
                    if (installResult.UpdatedAsset is not null)
                    {
                        installedAssets.Add(installResult.UpdatedAsset);
                    }
                }
            }
        }

        DisplayInstalledAgentAssetsSummary(configuration, installedAssets);

        return hasErrors;
    }

    private async Task<bool> ApplyActionAssetsAsync(
        IReadOnlyList<AgentActionAssetDefinition> selectedAssets,
        IReadOnlyList<AgentEnvironmentApplicator> applicators,
        CancellationToken cancellationToken)
    {
        var hasErrors = false;
        foreach (var selectedAsset in selectedAssets)
        {
            foreach (var applicator in applicators.Where(applicator => ReferenceEquals(applicator.Asset, selectedAsset)))
            {
                try
                {
                    await applicator.ApplyAsync(cancellationToken);
                    InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, applicator.Description);
                }
                // Apply each target independently so one malformed or unwritable client configuration
                // does not prevent the remaining compatible clients from being configured.
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    InteractionService.DisplayError(ex.Message);
                    if (ex.InnerException is JsonException)
                    {
                        InteractionService.DisplaySubtleMessage(
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, applicator.Description));
                    }
                    hasErrors = true;
                }
            }
        }

        return hasErrors;
    }

    private async Task ConfigureTelemetryHooksAsync(AgentEnvironmentScanContext context, CancellationToken cancellationToken)
    {
        TelemetryHookConfigurationResult result;
        try
        {
            result = await _telemetryHookConfigurator.ConfigureAsync(context.DetectedClients, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hook installation is best-effort transparency tooling; never fail `agent init` over it.
            // This deliberately catches everything except cancellation: besides file IO failures, a
            // corrupted CLI build could surface a missing embedded hook script as an
            // InvalidOperationException, and that must not abort the whole command either.
            InteractionService.DisplaySubtleMessage(ex.Message);
            return;
        }

        if (result.ConfiguredClients.Count > 0)
        {
            var clientNames = string.Join(", ", result.ConfiguredClients.Select(GetClientDisplayName));
            InteractionService.DisplayMessage(
                KnownEmojis.BarChart,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHooksInstalled, clientNames));
        }

        foreach (var skip in result.Skipped)
        {
            var clientName = GetClientDisplayName(skip.Client);
            var message = skip.Reason switch
            {
                TelemetryHookSkipReason.MalformedConfig => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedMalformedConfig, clientName),
                TelemetryHookSkipReason.UnexpectedConfigShape => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedUnexpectedShape, clientName),
                _ => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookWriteFailed, clientName),
            };

            // Skips are surfaced to the user but never treated as command failures: a user-owned
            // config we can't safely modify must not break `agent init`.
            InteractionService.DisplaySubtleMessage(message);
        }
    }

    private static string GetClientDisplayName(AgentClientKind client)
        => client switch
        {
            AgentClientKind.CopilotCli => "GitHub Copilot CLI",
            AgentClientKind.CopilotApp => "GitHub Copilot App",
            AgentClientKind.ClaudeCode => "Claude Code",
            AgentClientKind.VsCode => "VS Code",
            AgentClientKind.OpenCode => "OpenCode",
            _ => client.ToString(),
        };

    private async Task<AvailableFileAssets> ResolveAvailableSkillsAsync(
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        var skills = new List<AgentFileAssetDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
            skills.AddRange(bundle.GetAssetDefinitions().Where(static skill => !IsCliDefinedSkillName(skill.Name)));
        }
        else
        {
            // Preserve the install failure so the caller can surface it only when the user
            // passed an explicit --skills value that names a bundle-only skill. Happy-path
            // (interactive prompt with the embedded fallback) stays silent.
            failureMessage = result.Message;
        }

        // When the bundle is unavailable (network failure, version mismatch, etc.), fall back
        // silently to the CLI-defined skills. The installer already logs the underlying cause
        // at debug level, so the user is not interrupted with a warning they cannot act on.
        skills.AddRange(AgentAssetCatalog.GetFileAssets(AgentAssetKind.Skill));

        return new(
            skills
                .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
                .ToList(),
            bundle,
            failureMessage);
    }

    private static bool HasUnknownBundleAssetCandidate(
        string requestedAssets,
        IReadOnlyList<AgentFileAssetDefinition> availableAssets,
        IReadOnlyList<AgentFileAssetDefinition> cliDefinedAssets)
    {
        // Tokens like "all" / "none" don't name assets, so the "looks like a bundle asset but missing"
        // diagnostic doesn't apply — let the normal validation path handle them.
        if (string.IsNullOrWhiteSpace(requestedAssets) ||
            string.Equals(requestedAssets, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedAssets, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requested = requestedAssets.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in requested)
        {
            if (cliDefinedAssets.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!availableAssets.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                // A non-CLI name missing from the catalog is exactly the case the bundle would have provided.
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipBundleCatalogResolution(
        PromptBinding<string?> assetsBinding,
        IReadOnlyList<AgentFileAssetDefinition> cliDefinedAssets)
    {
        var (wasProvided, optionValue, _) = PromptBinding.Resolve(assetsBinding);
        if (!wasProvided)
        {
            return false;
        }

        if (string.Equals(optionValue, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(optionValue) ||
            string.Equals(optionValue, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedAssetNames = optionValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selectedAssetNames.Length > 0 &&
            selectedAssetNames.All(name => cliDefinedAssets.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsCliDefinedSkillName(string name)
    {
        return AgentAssetCatalog.GetFileAssets(AgentAssetKind.Skill)
            .Any(skill => skill.HasName(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the single short sentence from a skill description so the selection prompt
    /// stays readable.
    /// </summary>
    /// <remarks>
    /// Bundle manifest descriptions can include a bold skill-type prefix followed by a
    /// short tagline and additional usage guidance, for example:
    ///   "**WORKFLOW SKILL** - Top-level router for Aspire 13.4 distributed apps. Detects the AppHost. USE FOR: ..."
    /// This trims the prefix and returns only the first sentence. Inputs without the prefix
    /// or sentence terminator are returned trimmed-but-otherwise-unchanged so CLI-defined
    /// short descriptions are preserved as-is.
    /// </remarks>
    internal static string SimplifyDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var simplified = description.Trim();

        // Strip the leading bold "TYPE SKILL" prefix when present, and only then strip the
        // separator characters that typically follow it. Gating the separator strip on the
        // prefix match avoids silently mutating descriptions that legitimately start with
        // a dash, em-dash, or colon (e.g. "-mode flag explained" or ":memo notes").
        var strippedBoldPrefix = false;
        if (simplified.StartsWith("**", StringComparison.Ordinal))
        {
            var endBold = simplified.IndexOf("**", 2, StringComparison.Ordinal);
            if (endBold > 0)
            {
                simplified = simplified[(endBold + 2)..].TrimStart();
                strippedBoldPrefix = true;
            }
        }

        if (strippedBoldPrefix)
        {
            // Separators that typically follow the bold prefix (" - ", " — ", " – ", ": ").
            while (simplified.Length > 0 && simplified[0] is '-' or '\u2013' or '\u2014' or ':')
            {
                simplified = simplified[1..].TrimStart();
            }
        }

        // Return up to and including the first sentence-ending punctuation followed by
        // whitespace or end-of-string. This avoids splitting on inline punctuation such
        // as "13.4" or "github.com" inside the first sentence.
        for (var i = 0; i < simplified.Length; i++)
        {
            if (simplified[i] is '.' or '!' or '?'
                && (i + 1 >= simplified.Length || char.IsWhiteSpace(simplified[i + 1])))
            {
                return simplified[..(i + 1)];
            }
        }

        return simplified;
    }

    private static IReadOnlyList<AgentFileAssetDefinition> GetDefaultFileAssets(
        IEnumerable<AgentFileAssetDefinition> availableAssets,
        Func<AgentFileAssetDefinition, bool>? selectByDefault)
    {
        // When the caller doesn't customize default selection, fall back to each asset's IsDefault value.
        // Bundle-sourced skills are uniformly IsDefault=true; CLI-defined skills (playwright-cli,
        // dotnet-inspect) are IsDefault=false so they stay opt-in. Callers like `aspire new` pass
        // a predicate to additionally filter out skills that don't fit their flow.
        var predicate = selectByDefault ?? (static asset => asset.IsDefault);
        return availableAssets.Where(predicate).ToList();
    }

    /// <summary>
    /// Installs the files for an agent asset at the specified target.
    /// </summary>
    /// <returns>The install result, including the asset/location pair when files were updated.</returns>
    private async Task<AgentAssetInstallResult> InstallAgentAssetAsync(
        FileAssetKindConfiguration configuration,
        AgentAssetInstallTarget target,
        AgentFileAssetDefinition asset,
        AspireSkillsBundle? bundle,
        CancellationToken cancellationToken)
    {
        var relativeAssetPath = Path.Combine(target.RelativeAssetDirectory, asset.Name);
        var fullAssetDirectoryPath = Path.Combine(target.RootDirectory.FullName, relativeAssetPath);

        try
        {
            var assetFiles = await GetAgentAssetFilesAsync(asset, bundle, cancellationToken);
            var anyFileUpdated = false;

            foreach (var assetFile in assetFiles)
            {
                var fullPath = Path.Combine(target.RootDirectory.FullName, relativeAssetPath, assetFile.RelativePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(fullPath))
                {
                    var existingContent = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                    if (assetFile.ContentEquals(existingContent))
                    {
                        continue;
                    }
                }

                await File.WriteAllBytesAsync(fullPath, assetFile.Bytes, cancellationToken);
                anyFileUpdated = true;
            }

            if (!anyFileUpdated)
            {
                return new(Succeeded: true, UpdatedAsset: null);
            }

            return new(Succeeded: true, new InstalledAgentAssetSummaryItem(asset.Name, target.DisplayDirectory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            InteractionService.DisplayError(
                string.Format(
                    CultureInfo.CurrentCulture,
                    configuration.InstallFailureMessage,
                    asset.Name,
                    fullAssetDirectoryPath,
                    ex.Message));
            return new(Succeeded: false, UpdatedAsset: null);
        }
    }

    private void DisplayInstalledAgentAssetsSummary(
        FileAssetKindConfiguration configuration,
        IReadOnlyList<InstalledAgentAssetSummaryItem> installedAssets)
    {
        if (installedAssets.Count == 0)
        {
            return;
        }

        var assetNames = string.Join(", ", GetUniqueValues(installedAssets.Select(static asset => asset.AssetName)));
        var locations = string.Join(", ", GetUniqueValues(installedAssets.Select(static asset => asset.DisplayLocation)));
        var message = string.Join(Environment.NewLine,
            configuration.InstalledSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, configuration.InstalledAssetsSummary, assetNames)}",
            $"  {string.Format(CultureInfo.CurrentCulture, configuration.InstalledLocationsSummary, locations)}");

        InteractionService.DisplayMessage(KnownEmojis.Robot, message);
    }

    private static IReadOnlyList<string> GetUniqueValues(IEnumerable<string> values)
    {
        var uniqueValues = new List<string>();
        var seenValues = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in values)
        {
            if (seenValues.Add(value))
            {
                uniqueValues.Add(value);
            }
        }

        return uniqueValues;
    }

    private static string GetDisplayAgentAssetDirectory(string relativeAssetDirectory)
    {
        return relativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static async Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(
        AgentFileAssetDefinition asset,
        AspireSkillsBundle? bundle,
        CancellationToken cancellationToken)
    {
        if (asset.Files.Count > 0)
        {
            return asset.Files
                .Where(file => asset.ShouldInstallFile(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList();
        }

        if (asset.SourceKind is AgentFileAssetSourceKind.AspireSkillsBundle)
        {
            if (bundle is null)
            {
                throw new InvalidOperationException($"Aspire Skills bundle was not resolved for asset '{asset.Name}'.");
            }

            return await bundle.GetAssetFilesAsync(asset, cancellationToken);
        }

        throw new InvalidOperationException($"Agent asset '{asset.Name}' does not define installable files.");
    }

    private sealed record FileAssetKindConfiguration(
        AgentAssetKind AssetKind,
        string LocationSelectionPrompt,
        string AssetSelectionPrompt,
        string InstallFailureMessage,
        string InstalledSummary,
        string InstalledAssetsSummary,
        string InstalledLocationsSummary,
        string NoDetectedClientWarning);

    private sealed record ActionAssetKindConfiguration(
        AgentAssetKind AssetKind,
        string AssetSelectionPrompt,
        string TargetDescription,
        string NoDetectedClientWarning,
        string NoCompatibleClientError);

    private sealed record AvailableFileAssets(
        IReadOnlyList<AgentFileAssetDefinition> Assets,
        AspireSkillsBundle? Bundle,
        string? FailureMessage);

    private sealed record SelectedFileAssets(
        AgentAssetKind AssetKind,
        IReadOnlyList<AgentAssetLocation> Locations,
        IReadOnlyList<AgentFileAssetDefinition> Assets,
        AspireSkillsBundle? Bundle)
    {
        public static SelectedFileAssets Empty(AgentAssetKind assetKind) => new(assetKind, [], [], Bundle: null);

        public AgentAssetSelection ToResult() => new(AssetKind, Locations, Assets);
    }

    private sealed record SelectedActionAssets(
        AgentAssetKind AssetKind,
        IReadOnlyList<AgentActionAssetDefinition> Assets,
        IReadOnlyList<AgentEnvironmentApplicator> Applicators,
        bool HasErrors)
    {
        public static SelectedActionAssets Empty(AgentAssetKind assetKind) => new(assetKind, [], [], HasErrors: false);

        public AgentAssetSelection ToResult() => new(AssetKind, Locations: [], Assets);
    }

    private sealed record InstalledAgentAssetSummaryItem(string AssetName, string DisplayLocation);

    private readonly record struct AgentAssetInstallResult(bool Succeeded, InstalledAgentAssetSummaryItem? UpdatedAsset);
}

internal sealed record AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyCollection<AgentClientKind> DetectedClients,
    IReadOnlyList<AgentAssetSelection> Selections)
{
    public static AgentInitExecutionResult Empty(int exitCode) => new(exitCode, [], []);

    public IReadOnlyList<AgentAssetLocation> GetLocations(AgentAssetKind assetKind)
        => Selections.FirstOrDefault(selection => selection.AssetKind == assetKind)?.Locations ?? [];

    public IReadOnlyList<AgentAssetDefinition> GetAssets(AgentAssetKind assetKind)
        => Selections.FirstOrDefault(selection => selection.AssetKind == assetKind)?.Assets ?? [];
}

internal sealed record AgentAssetSelection(
    AgentAssetKind AssetKind,
    IReadOnlyList<AgentAssetLocation> Locations,
    IReadOnlyList<AgentAssetDefinition> Assets);
