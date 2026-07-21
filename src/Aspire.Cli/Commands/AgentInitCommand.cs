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
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly IAspireSkillsInstaller _aspireSkillsInstaller;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ITelemetryHookConfigurator _telemetryHookConfigurator;

    /// <summary>
    /// AgentInitCommand does not need template package metadata prefetching.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => false;

    /// <summary>
    /// AgentInitCommand does not need CLI package metadata prefetching.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => false;

    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAspireSkillsInstaller aspireSkillsInstaller,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        ITelemetryHookConfigurator telemetryHookConfigurator,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _aspireSkillsInstaller = aspireSkillsInstaller;
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _languageDiscovery = languageDiscovery;
        _telemetryHookConfigurator = telemetryHookConfigurator;

        Options.Add(s_workspaceRootOption);
        Options.Add(s_skillLocationsOption);
        Options.Add(s_skillsOption);
        Options.Add(s_extensionLocationsOption);
        Options.Add(s_extensionsOption);
    }

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_AssetLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.All.Where(l => l.AssetType == AgentAssetKind.Skill).Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_AssetsOptionDescription,
            string.Join(",", AgentAssetDefinition.CliDefined.Where(s => s.AssetType == AgentAssetKind.Skill).Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionLocationsOption = new("--extension-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_AssetLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.All.Where(l => l.AssetType == AgentAssetKind.Extension).Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionsOption = new("--extensions")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_AssetsOptionDescription,
            string.Join(",", AgentAssetDefinition.CliDefined.Where(s => s.AssetType == AgentAssetKind.Extension).Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

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
    /// Callers that expose <c>--skill-locations</c> and <c>--skills</c> can pass
    /// <paramref name="skillLocationsBinding"/> and <paramref name="skillsBinding"/> so the chained
    /// execution reuses the same non-interactive selection semantics as standalone <c>aspire agent init</c>.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionLocationsBinding,
        PromptBinding<string?> extensionsBinding,
        Func<AgentAssetDefinition, bool>? selectByDefault,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != CliExitCodes.Success)
        {
            return new(previousResultExitCode, [], []);
        }

        // Add a separating line between prompt and previous work in aspire new and aspire init.
        interactionService.DisplayEmptyLine();

        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(workspaceRoot, selectByDefault, skillLocationsBinding, skillsBinding, extensionLocationsBinding, extensionsBinding, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        // Standalone `aspire agent init` is typically run against an existing project, so don't
        // pre-select the one-time aspireify wiring skill even though every other bundle skill
        // is default-on. Users can still opt into it from the prompt or via --skills.
        var skillLocationsBinding = PromptBinding.Create(parseResult, s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, s_skillsOption);
        var extensionLocationsBinding = PromptBinding.Create(parseResult, s_extensionLocationsOption);
        var extensionsBinding = PromptBinding.Create(parseResult, s_extensionsOption);
        var result = await ExecuteAgentInitAsync(workspaceRoot, ExcludeOneTimeSetupAgentAssetsFromDefaults, skillLocationsBinding, skillsBinding, extensionLocationsBinding, extensionsBinding, cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    /// <summary>
    /// Names of bundle agent assets that perform one-time workspace setup and should NOT be
    /// pre-selected after a workspace was just produced by a template flow such as
    /// <c>aspire new</c> or after standalone <c>aspire agent init</c> (typically run
    /// against an existing project).
    /// </summary>
    /// <remarks>
    /// This is the single source of truth the CLI consults when filtering bundle agent assets out
    /// of the auto-preselection set. All bundle agent assets are default-on, so if the bundle ships
    /// a new wiring or bootstrap-style agent asset that should NOT auto-run in an already-bootstrapped
    /// workspace, add its name here.
    /// </remarks>
    internal static readonly IReadOnlySet<string> s_oneTimeSetupAgentAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CommonAgentApplicators.AspireifySkillName,
    };

    /// <summary>
    /// Default-agent-asset predicate used by flows that do not want one-time setup agent assets
    /// pre-selected — namely <c>aspire new</c> (template already created the AppHost) and
    /// standalone <c>aspire agent init</c> (typically run against an existing project).
    /// Agent assets filtered here remain available to opt into from the prompt or via <c>--skills</c>
    /// or <c>--extensions</c>.
    /// </summary>
    internal static bool ExcludeOneTimeSetupAgentAssetsFromDefaults(AgentAssetDefinition asset)
        => asset.IsDefault && !s_oneTimeSetupAgentAssetNames.Contains(asset.Name);

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
        Func<AgentAssetDefinition, bool>? selectByDefault,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionLocationsBinding,
        PromptBinding<string?> extensionsBinding,
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

        var supportedAssetKinds = GetEffectiveSupportedAssetTypes(context).ToList();
        var selectedLocations = new List<AgentAssetLocation>();
        var selectedAssets = new List<AgentAssetDefinition>();
        List<AspireSkillsBundle>? aspireSkillsBundles = null;
        string? bundleInstallFailureMessage = null;
        AgentEnvironmentApplicator? combinedMcpApplicator = null;
        var mcpApplicators = userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList();

        // --- Phase 1: Agent asset and location selection ---
        // Prompt each supported asset kind as an independent pair so users see:
        // skills -> skill locations, then extensions -> extension locations.
        if (supportedAssetKinds.Count > 0)
        {
            var (availableAssets, bundles, failureMessage) = await ResolveAvailableAgentAssetsAsync(
                supportedAssetKinds,
                assetKind => GetAssetsBinding(assetKind, skillsBinding, extensionsBinding),
                detectedLanguage,
                cancellationToken);
            aspireSkillsBundles = bundles;
            bundleInstallFailureMessage = failureMessage;

            // Order the merged catalog deterministically by kind and name so the prompt is stable
            // regardless of manifest order. OrdinalIgnoreCase matches the case-insensitive
            // --skills and --extensions parsing used elsewhere.
            availableAssets = [.. availableAssets
                .OrderBy(static s => s.AssetType, Comparer<AgentAssetKind>.Default)
                .ThenBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)];

            if (mcpApplicators.Count > 0)
            {
                combinedMcpApplicator = new AgentEnvironmentApplicator(
                    AgentCommandStrings.InitCommand_ConfigureMcpServer,
                    async ct =>
                    {
                        foreach (var mcp in mcpApplicators)
                        {
                            await mcp.ApplyAsync(ct);
                            InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, mcp.Description);
                        }
                    },
                    promptGroup: McpInitPromptGroup.AdditionalOptions);
            }

            var mcpApplicatorWasOffered = false;

            foreach (var assetKind in supportedAssetKinds)
            {
                var availableAssetsForKind = availableAssets
                    .Where(asset => asset.AssetType == assetKind)
                    .ToList();
                var assetChoices = new List<object>(availableAssetsForKind);

                var mcpApplicatorIncluded = !mcpApplicatorWasOffered && combinedMcpApplicator is not null;
                if (mcpApplicatorIncluded)
                {
                    assetChoices.Add(combinedMcpApplicator!);
                    mcpApplicatorWasOffered = true;
                }

                if (assetChoices.Count == 0)
                {
                    continue;
                }

                var defaultAssets = GetDefaultAgentAssets(availableAssetsForKind, selectByDefault);
                var defaultAssetNames = string.Join(",", defaultAssets.Select(s => s.Name));
                var assetsBindingWithDefault = GetAssetsBinding(assetKind, skillsBinding, extensionsBinding)
                    .WithDefault(defaultAssetNames);

                // When the bundle failed to install and the caller passed an explicit --skills
                // or --extensions value that names a bundle-only asset, the upcoming
                // MatchChoicesOrThrow will reject the value as "not a valid choice" with no hint
                // that the underlying cause was the bundle. Surface the install failure first so
                // users can see why the catalog is short. We only do this when the value contains
                // a name that is not in the available catalog and not a CLI-defined asset, so
                // happy-path runs stay silent.
                if (bundleInstallFailureMessage is not null)
                {
                    var (wasProvided, requestedAssets, _) = PromptBinding.Resolve(assetsBindingWithDefault);
                    if (wasProvided && requestedAssets is not null && HasUnknownBundleAgentAssetCandidate(requestedAssets, availableAssetsForKind))
                    {
                        InteractionService.DisplayError(bundleInstallFailureMessage);
                    }
                }

                var selectedItems = await InteractionService.PromptForSelectionsAsync(
                    GetAssetPromptText(assetKind),
                    assetChoices,
                    item => item switch
                    {
                        AgentAssetDefinition asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
                        AgentEnvironmentApplicator app => $"[bold]{app.Description}[/] [dim]{AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments}[/]",
                        _ => item.ToString()!
                    },
                    preSelected: defaultAssets.Cast<object>(),
                    optional: true,
                    binding: assetsBindingWithDefault,
                    // The MCP applicator participates in the interactive multi-select prompt for UX,
                    // but it is not an asset and must not be addressable via --skills or --extensions.
                    bindingChoices: availableAssetsForKind.Cast<object>(),
                    echoSelected: false,
                    cancellationToken: cancellationToken);

                var selectedAssetsForKind = selectedItems.OfType<AgentAssetDefinition>().ToList();
                selectedAssets.AddRange(selectedAssetsForKind);
                if (selectedAssetsForKind.Count > 0)
                {
                    selectedLocations.AddRange(await PromptForAgentAssetLocationsAsync(
                        assetKind,
                        GetLocationsBinding(assetKind, skillLocationsBinding, extensionLocationsBinding),
                        cancellationToken));
                }

                // Clear MCP applicator if it was offered and not selected by the user.
                if (mcpApplicatorIncluded && !selectedItems.Contains(combinedMcpApplicator))
                {
                    combinedMcpApplicator = null;
                }
            }
        }

        // --- Phase 3: Apply asset files for selected locations × assets ---
        // Each asset file write is fast (small markdown files), so sequential execution
        // is fine — parallelizing would complicate error handling for no meaningful gain.
        var hasErrors = false;

        var installedAssets = new List<InstalledAgentAssetSummaryItem>();

        foreach (var location in selectedLocations)
        {
            context.AddAssetBaseDirectory(location.AssetType, location.RelativeAgentAssetDirectory);

            foreach (var asset in selectedAssets)
            {
                // Playwright CLI is installed via PlaywrightCliInstaller, not as a static asset file
                if (!asset.HasInstallableFiles)
                {
                    continue;
                }

                if (asset.AssetType != location.AssetType)
                {
                    continue;
                }

                var assetBundle = aspireSkillsBundles?.FirstOrDefault(bundle => bundle.AssetType == asset.AssetType);
                if (asset.SourceKind is AgentAssetSourceKind.AspireSkillsBundle && assetBundle is null)
                {
                    continue;
                }

                var installResult = await InstallAgentAssetAsync(
                    workspaceRoot,
                    location.RelativeAgentAssetDirectory,
                    asset,
                    assetBundle,
                    isUserLevel: false,
                    cancellationToken);
                hasErrors |= !installResult.Succeeded;
                if (installResult.UpdatedAsset is not null)
                {
                    installedAssets.Add(installResult.UpdatedAsset);
                }

                if ((location.InstallLocation & InstallLocation.User) != 0)
                {
                    installResult = await InstallAgentAssetAsync(
                        ExecutionContext.HomeDirectory,
                        location.UserRelativeAgentAssetDirectory,
                        asset,
                        assetBundle,
                        isUserLevel: true,
                        cancellationToken);
                    hasErrors |= !installResult.Succeeded;
                    if (installResult.UpdatedAsset is not null)
                    {
                        installedAssets.Add(installResult.UpdatedAsset);
                    }
                }
            }
        }

        DisplayInstalledAssetsSummary(installedAssets);

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        var selectedSkillLocations = selectedLocations.Where(l => l.AssetType == AgentAssetKind.Skill).ToList();
        var selectedSkillDirs = selectedSkillLocations.Select(l => l.RelativeAgentAssetDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedAssets.Contains(AgentAssetDefinition.PlaywrightCli) && selectedSkillLocations.Count > 0)
        {
            try
            {
                var (status, message) = await _playwrightCliInstaller.InstallAsync(workspaceRoot.FullName, selectedSkillDirs, cancellationToken);
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
                        // npm is not available — not an error, just informational.
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

        // --- Phase 5: Apply MCP server configuration if selected ---
        if (combinedMcpApplicator is not null)
        {
            try
            {
                await combinedMcpApplicator.ApplyAsync(cancellationToken);
            }
            // InvalidOperationException is thrown by scanner-generated applicators
            // (e.g., MCP config writers) when the underlying operation fails.
            // JsonException as InnerException indicates a malformed config file
            // (e.g., invalid JSON in .copilot/mcp-config.json or .vscode/mcp.json).
            catch (InvalidOperationException ex)
            {
                InteractionService.DisplayError(ex.Message);
                if (ex.InnerException is JsonException)
                {
                    InteractionService.DisplaySubtleMessage(
                        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, combinedMcpApplicator.Description));
                }
                hasErrors = true;
            }
        }

        // --- Phase 6: Install agent telemetry hooks (default-on, parity with azure-skills) ---
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
            selectedLocations,
            selectedAssets);
    }

    private static IReadOnlyCollection<AgentAssetKind> GetEffectiveSupportedAssetTypes(AgentEnvironmentScanContext context)
    {
        // No detected client means the command is being run in a generic workspace. Preserve the
        // historical skills-only behavior, but do not surface extension options unless a detected
        // client explicitly supports them.
        return context.DetectedClients.Count == 0
            ? [AgentAssetKind.Skill]
            : context.SupportedAssetTypes;
    }

    private async Task<IReadOnlyList<AgentAssetLocation>> PromptForAgentAssetLocationsAsync(
        AgentAssetKind assetKind,
        PromptBinding<string?> locationsBinding,
        CancellationToken cancellationToken)
    {
        var choices = AgentAssetLocation.All
            .Where(location => location.AssetType == assetKind)
            .ToList();
        if (choices.Count == 0)
        {
            return [];
        }

        var defaultLocationIds = string.Join(",", choices.Where(location => location.IsDefault).Select(location => location.Id));
        var locations = await InteractionService.PromptForSelectionsAsync(
            GetLocationPromptText(assetKind),
            choices,
            loc => $"{loc.DisplayName} — {loc.Description}",
            preSelected: choices.Where(location => location.IsDefault),
            optional: true,
            binding: locationsBinding.WithDefault(defaultLocationIds),
            echoSelected: false,
            cancellationToken: cancellationToken);

        return locations;
    }

    private static string GetLocationPromptText(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => AgentCommandStrings.InitCommand_SelectSkillLocations,
            AgentAssetKind.Extension => AgentCommandStrings.InitCommand_SelectExtensionLocations,
            _ => throw new InvalidOperationException($"Unsupported agent asset kind '{assetKind}'.")
        };
    }

    private static string GetAssetPromptText(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => AgentCommandStrings.InitCommand_SelectSkills,
            AgentAssetKind.Extension => AgentCommandStrings.InitCommand_SelectExtensions,
            _ => throw new InvalidOperationException($"Unsupported agent asset kind '{assetKind}'.")
        };
    }

    private static PromptBinding<string?> GetLocationsBinding(
        AgentAssetKind assetKind,
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> extensionLocationsBinding)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => skillLocationsBinding,
            AgentAssetKind.Extension => extensionLocationsBinding,
            _ => throw new InvalidOperationException($"Unsupported agent asset kind '{assetKind}'.")
        };
    }

    private static PromptBinding<string?> GetAssetsBinding(
        AgentAssetKind assetKind,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionsBinding)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => skillsBinding,
            AgentAssetKind.Extension => extensionsBinding,
            _ => throw new InvalidOperationException($"Unsupported agent asset kind '{assetKind}'.")
        };
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
            var clientNames = string.Join(", ", result.ConfiguredClients.Select(c => c.Name));
            InteractionService.DisplayMessage(
                KnownEmojis.BarChart,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHooksInstalled, clientNames));
        }

        foreach (var skip in result.Skipped)
        {
            var message = skip.Reason switch
            {
                TelemetryHookSkipReason.MalformedConfig => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedMalformedConfig, skip.Client.Name),
                TelemetryHookSkipReason.UnexpectedConfigShape => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedUnexpectedShape, skip.Client.Name),
                _ => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookWriteFailed, skip.Client.Name, skip.Reason),
            };

            // Skips are surfaced to the user but never treated as command failures: a user-owned
            // config we can't safely modify must not break `agent init`.
            InteractionService.DisplaySubtleMessage(message);
        }
    }

    private async Task<(IReadOnlyList<AgentAssetDefinition> Assets, List<AspireSkillsBundle>? Bundles, string? FailureMessage)> ResolveAvailableAgentAssetsAsync(
        IReadOnlyCollection<AgentAssetKind> supportedAssetTypes,
        Func<AgentAssetKind, PromptBinding<string?>> assetsBindingProvider,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        var assets = new List<AgentAssetDefinition>();
        List<AspireSkillsBundle>? bundles = null;
        string? failureMessage = null;

        var bundleAssetTypes = supportedAssetTypes
            .Where(assetType => !ShouldSkipBundleCatalogResolution(assetsBindingProvider(assetType)))
            .ToList();
        var results = await InstallSupportedBundlesAsync(bundleAssetTypes, cancellationToken);
        if (results.Count > 0)
        {
            bundles = new List<AspireSkillsBundle>();
            foreach (var result in results)
            {
                if (result.Status is AspireSkillsInstallStatus.Installed)
                {
                    var bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
                    bundles.Add(bundle);
                    assets.AddRange(bundle.GetAgentAssetDefinitions().Where(asset => !IsCliDefinedAssetName(asset.Name)));
                }
                else
                {
                    // Preserve the install failure so the caller can surface it only when the user
                    // passed an explicit --skills or --extensions value that names a bundle-only skill.
                    // Happy-path (interactive prompt with the embedded fallback) stays silent.
                    failureMessage = result.Message;
                }
            }
        }

        // When the bundle is unavailable (network failure, version mismatch, etc.), fall back
        // silently to the CLI-defined skills. The installer already logs the underlying cause
        // at debug level, so the user is not interrupted with a warning they cannot act on.
        assets.AddRange(AgentAssetDefinition.CliDefined.Where(asset => supportedAssetTypes.Contains(asset.AssetType)));

        return (assets
            .Where(s => s.IsApplicableToLanguage(detectedLanguage))
            .ToList(), bundles, failureMessage);
    }

    private async Task<List<AspireSkillsInstallResult>> InstallSupportedBundlesAsync(IReadOnlyCollection<AgentAssetKind> supportedAssetTypes, CancellationToken cancellationToken)
    {
        var results = new List<AspireSkillsInstallResult>();
        foreach (var assetType in supportedAssetTypes.Distinct())
        {
            if (assetType == AgentAssetKind.Skill)
            {
                results.Add(await _aspireSkillsInstaller.InstallAsync(AgentAssetKind.Skill, cancellationToken).ConfigureAwait(false));
            }
            else if (assetType == AgentAssetKind.Extension)
            {
                results.Add(await _aspireSkillsInstaller.InstallAsync(AgentAssetKind.Extension, cancellationToken).ConfigureAwait(false));
            }
        }

        return results;
    }

    private static bool HasUnknownBundleAgentAssetCandidate(string requestedAssets, IReadOnlyList<AgentAssetDefinition> availableAssets)
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
            if (IsCliDefinedAssetName(name))
            {
                continue;
            }

            if (!availableAssets.Any(s => s.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                // A non-CLI name that isn't in the catalog is exactly the case the bundle would have provided.
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipBundleCatalogResolution(PromptBinding<string?> assetsBinding)
    {
        var (wasProvided, optionValue, _) = PromptBinding.Resolve(assetsBinding);
        if (!wasProvided)
        {
            return false;
        }

        return ShouldSkipBundleCatalogResolution(optionValue);
    }

    private static bool ShouldSkipBundleCatalogResolution(string? value)
    {
        if (string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var selectedAssetNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selectedAssetNames.Length > 0 &&
               selectedAssetNames.All(static name => IsCliDefinedAssetName(name));
    }

    private static bool IsCliDefinedAssetName(string name)
    {
        return AgentAssetDefinition.CliDefined.Any(asset => asset.HasName(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extracts the single short sentence from an asset description so the selection prompt
    /// stays readable.
    /// </summary>
    /// <remarks>
    /// Bundle manifest descriptions can include a bold asset-type prefix followed by a
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

    private static IReadOnlyList<AgentAssetDefinition> GetDefaultAgentAssets(IEnumerable<AgentAssetDefinition> availableAssets, Func<AgentAssetDefinition, bool>? selectByDefault)
    {
        // When the caller doesn't customize default selection, fall back to AgentAssetDefinition.IsDefault.
        // Bundle-sourced assets are uniformly IsDefault=true; CLI-defined assets (playwright-cli,
        // dotnet-inspect) are IsDefault=false so they stay opt-in. Callers like `aspire new` pass
        // a predicate to additionally filter out assets that don't fit their flow.
        var predicate = selectByDefault ?? (static asset => asset.IsDefault);
        return availableAssets.Where(predicate).ToList();
    }

    /// <summary>
    /// Installs the files for an agent asset at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns>The install result, including the asset/location pair when files were updated.</returns>
    private async Task<AgentAssetInstallResult> InstallAgentAssetAsync(
        DirectoryInfo rootDirectory,
        string relativeAssetDirectory,
        AgentAssetDefinition asset,
        AspireSkillsBundle? aspireSkillsBundle,
        bool isUserLevel,
        CancellationToken cancellationToken)
    {
        var relativeAssetPath = Path.Combine(relativeAssetDirectory, asset.Name);
        var fullAssetDirectoryPath = Path.Combine(rootDirectory.FullName, relativeAssetPath);

        try
        {
            var assetFiles = await GetAgentAssetFilesAsync(asset, aspireSkillsBundle, cancellationToken);
            var anyFileUpdated = false;

            foreach (var assetFile in assetFiles)
            {
                var fullPath = Path.Combine(rootDirectory.FullName, relativeAssetPath, assetFile.RelativePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(fullPath))
                {
                    var existingContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    if (string.Equals(existingContent.ReplaceLineEndings("\n"), assetFile.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                await File.WriteAllTextAsync(fullPath, assetFile.Content, cancellationToken);
                anyFileUpdated = true;
            }

            if (!anyFileUpdated)
            {
                return new(Succeeded: true, UpdatedAsset: null);
            }

            var displayLocation = GetDisplayAssetDirectory(relativeAssetDirectory, isUserLevel);
            return new(Succeeded: true, new InstalledAgentAssetSummaryItem(asset.Name, displayLocation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            InteractionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, asset.Name, fullAssetDirectoryPath, ex.Message));
            return new(Succeeded: false, UpdatedAsset: null);
        }
    }

    private void DisplayInstalledAssetsSummary(IReadOnlyList<InstalledAgentAssetSummaryItem> installedAssets)
    {
        if (installedAssets.Count == 0)
        {
            return;
        }

        var assetNames = string.Join(", ", GetUniqueValues(installedAssets.Select(static installedAsset => installedAsset.AssetName)));
        var locations = string.Join(", ", GetUniqueValues(installedAssets.Select(static installedAsset => installedAsset.DisplayLocation)));
        var message = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills, assetNames)}",
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations, locations)}");

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

    private static string GetDisplayAssetDirectory(string relativeAssetDirectory, bool isUserLevel)
    {
        var displayRelativeAssetDirectory = relativeAssetDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return isUserLevel ? $"~/{displayRelativeAssetDirectory}" : displayRelativeAssetDirectory;
    }

    private static async Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(AgentAssetDefinition asset, AspireSkillsBundle? aspireSkillsBundle, CancellationToken cancellationToken)
    {
        if (asset.AssetContent is not null)
        {
            if (asset.AssetType == AgentAssetKind.Skill)
            {
                return [new AgentAssetFile("SKILL.md", asset.AssetContent)];
            }

            if (asset.AssetType == AgentAssetKind.Extension)
            {
                return [new AgentAssetFile("extension.mjs", asset.AssetContent)];
            }

            throw new InvalidOperationException($"Asset '{asset.Name}' does not define installable files.");
        }

        if (asset.SourceKind is AgentAssetSourceKind.AspireSkillsBundle)
        {
            if (aspireSkillsBundle is null)
            {
                throw new InvalidOperationException($"Aspire skills bundle was not resolved for asset '{asset.Name}'.");
            }

            return await aspireSkillsBundle.GetAgentAssetFilesAsync(asset, cancellationToken);
        }

        throw new InvalidOperationException($"Asset '{asset.Name}' does not define installable files.");
    }

    private sealed record InstalledAgentAssetSummaryItem(string AssetName, string DisplayLocation);

    private readonly record struct AgentAssetInstallResult(bool Succeeded, InstalledAgentAssetSummaryItem? UpdatedAsset);

}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<AgentAssetLocation> SelectedLocations,
    IReadOnlyList<AgentAssetDefinition> SelectedAssets);
