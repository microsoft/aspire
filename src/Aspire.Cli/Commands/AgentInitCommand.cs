// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text;
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
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.All.Where(l => l.AgentAssetKind == AgentAssetKind.Skill).Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", AgentAssetDefinition.CliDefined.Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionLocationsOption = new("--extension-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_ExtensionLocationsOptionDescription,
            string.Join(",", AgentAssetLocation.All.Where(l => l.AgentAssetKind == AgentAssetKind.Extension).Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionsOption = new("--extensions")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_ExtensionsOptionDescription,
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    /// <summary>
    /// Creates the asset and location prompt bindings for each supported asset kind.
    /// </summary>
    internal static IReadOnlyDictionary<AgentAssetKind, AgentAssetPromptBindings> CreateAgentAssetPromptBindings(ParseResult parseResult)
    {
        return new Dictionary<AgentAssetKind, AgentAssetPromptBindings>
        {
            [AgentAssetKind.Skill] = new(
                PromptBinding.Create(parseResult, s_skillLocationsOption),
                PromptBinding.Create(parseResult, s_skillsOption)),
            [AgentAssetKind.Extension] = new(
                PromptBinding.Create(parseResult, s_extensionLocationsOption),
                PromptBinding.Create(parseResult, s_extensionsOption)),
        };
    }

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
    /// Callers that expose asset and location options can pass <paramref name="assetPromptBindings"/> so the chained
    /// execution reuses the same non-interactive selection semantics as standalone <c>aspire agent init</c>.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        IReadOnlyDictionary<AgentAssetKind, AgentAssetPromptBindings> assetPromptBindings,
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
            return await ExecuteAgentInitAsync(workspaceRoot, selectByDefault, assetPromptBindings, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        // Standalone `aspire agent init` is typically run against an existing project, so don't
        // pre-select the one-time aspireify wiring skill even though every other bundle skill
        // is default-on. Users can still opt into it from the prompt or via --skills.
        var assetPromptBindings = CreateAgentAssetPromptBindings(parseResult);
        var result = await ExecuteAgentInitAsync(workspaceRoot, ExcludeOneTimeSetupAssetsFromDefaults, assetPromptBindings, cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    /// <summary>
    /// Names of bundle assets that perform one-time workspace setup and should NOT be
    /// pre-selected after a workspace was just produced by a template flow such as
    /// <c>aspire new</c> or after standalone <c>aspire agent init</c> (typically run
    /// against an existing project).
    /// </summary>
    /// <remarks>
    /// This is the single source of truth the CLI consults when filtering bundle assets out
    /// of the auto-preselection set. All bundle assets are default-on, so if the bundle ships
    /// a new wiring or bootstrap-style skill that should NOT auto-run in an already-bootstrapped
    /// workspace, add its name here.
    /// </remarks>
    internal static readonly IReadOnlySet<string> s_oneTimeSetupAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        CommonAgentApplicators.AspireifyName,
    };

    /// <summary>
    /// Default-asset predicate used by flows that do not want one-time setup assets
    /// pre-selected — namely <c>aspire new</c> (template already created the AppHost) and
    /// standalone <c>aspire agent init</c> (typically run against an existing project).
    /// Assets filtered here remain available to opt into from the prompt or via <c>--skills</c>.
    /// </summary>
    internal static bool ExcludeOneTimeSetupAssetsFromDefaults(AgentAssetDefinition asset)
        => asset.IsDefault && !s_oneTimeSetupAssetNames.Contains(asset.Name);

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
        IReadOnlyDictionary<AgentAssetKind, AgentAssetPromptBindings> assetPromptBindings,
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

        var supportedAssetKinds = context.DetectedClients
            .SelectMany(static client => client.SupportedAssetKinds)
            .ToHashSet();
        if (context.DetectedClients.Count > 0 && assetPromptBindings.Any(binding =>
            !supportedAssetKinds.Contains(binding.Key) && WasExplicitlyRequested(binding.Value)))
        {
            InteractionService.DisplayError(AgentCommandStrings.InitCommand_UnsupportedAssetsRequested);
            return new(CliExitCodes.InvalidCommand, [], []);
        }

        // Detect the AppHost language to determine which assets to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted assets are excluded.
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

        var installedAssetsSummary = new List<InstalledAgentAssetSummaryItem>();
        var selectedAssetLocations = new List<AgentAssetLocation>();
        var selectedAgentAssets = new List<AgentAssetDefinition>();
        var hasErrors = false;
        foreach (var assetKind in assetPromptBindings.Keys)
        {
            if (context.DetectedClients.Count > 0 && !supportedAssetKinds.Contains(assetKind))
            {
                continue;
            }

            var promptBindings = assetPromptBindings[assetKind];
            var promptStrings = GetAssetPromptStrings(assetKind);
            var cliDefinedAssets = AgentAssetDefinition.CliDefined
                .Where(a => a.IsApplicableToLanguage(detectedLanguage) && a.AssetKind == assetKind)
                .ToList();

            // --- Phase 1: Asset location selection ---
            var assetLocationsBinding = promptBindings.Locations;
            var assetsBinding = promptBindings.Assets;
            var defaultLocationIds = string.Join(",", AgentAssetLocation.All.Where(l => l.AgentAssetKind == assetKind && l.IsDefault).Select(l => l.Id));
            var assetLocationsBindingWithDefault = assetLocationsBinding.WithDefault(defaultLocationIds);

            var selectedLocations = await InteractionService.PromptForSelectionsAsync(
                promptStrings.SelectLocations,
                AgentAssetLocation.All.Where(l => l.AgentAssetKind == assetKind),
                loc => $"{loc.DisplayName} — {loc.Description}",
                preSelected: AgentAssetLocation.All.Where(l => l.AgentAssetKind == assetKind && l.IsDefault),
                optional: true,
                binding: assetLocationsBindingWithDefault,
                echoSelected: false,
                cancellationToken: cancellationToken);

            // --- Phase 2: Asset selection (only if locations were selected) ---
            IReadOnlyList<AgentAssetDefinition> selectedAssets = [];
            AspireSkillsBundle? aspireSkillsBundle = null;
            string? bundleInstallFailureMessage = null;
            AgentEnvironmentApplicator? combinedMcpApplicator = null;
            var mcpApplicators = userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList();

            if (selectedLocations.Count > 0)
            {
                IReadOnlyList<AgentAssetDefinition> availableAssets;
                if (ShouldSkipBundleCatalogResolution(assetsBinding))
                {
                    availableAssets = cliDefinedAssets;
                }
                else
                {
                    (availableAssets, aspireSkillsBundle, bundleInstallFailureMessage) = await ResolveAvailableAssetsAsync(assetKind, detectedLanguage, cancellationToken);
                }

                // Order the merged catalog deterministically by name so the prompt is stable
                // regardless of manifest order. OrdinalIgnoreCase matches the case-insensitive
                // options parsing (eg --skills) used elsewhere.
                availableAssets = [.. availableAssets.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)];

                if (availableAssets.Count == 0 && !IsExplicitSelection(assetsBinding))
                {
                    continue;
                }

                // Build prompt items: skills first, then MCP as a separate non-default item
                var assetChoices = new List<object>();
                assetChoices.AddRange(availableAssets);

                if (mcpApplicators.Count > 0 && assetKind == AgentAssetKind.Skill)
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
                    assetChoices.Add(combinedMcpApplicator);
                }

                var preSelectedItems = new List<object>();
                var defaultAssets = GetDefaultAssets(availableAssets, selectByDefault);
                preSelectedItems.AddRange(defaultAssets);
                // MCP is intentionally NOT pre-selected

                var defaultAssetNames = string.Join(",", defaultAssets.Select(s => s.Name));
                var assetsBindingWithDefault = assetsBinding.WithDefault(defaultAssetNames);

                // When the bundle failed to install and the caller passed an explicit --skills value
                // that names a bundle-only asset, the upcoming MatchChoicesOrThrow will reject the
                // value as "not a valid choice" with no hint that the underlying cause was the
                // bundle. Surface the install failure first so users can see why the catalog is short.
                // We only do this when the value contains a name that is not in the available catalog
                // and not a CLI-defined skill, so happy-path runs stay silent.
                if (bundleInstallFailureMessage is not null)
                {
                    var (wasProvided, requestedAssets, _) = PromptBinding.Resolve(assetsBindingWithDefault);
                    if (wasProvided && requestedAssets is not null && HasUnknownBundleAssetCandidate(requestedAssets, availableAssets))
                    {
                        InteractionService.DisplayError(bundleInstallFailureMessage);
                    }
                }

                var selectedItems = await InteractionService.PromptForSelectionsAsync(
                    promptStrings.SelectAssets,
                    assetChoices,
                    item => item switch
                    {
                        AgentAssetDefinition asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
                        AgentEnvironmentApplicator app => $"[bold]{app.Description}[/] [dim]{AgentCommandStrings.InitCommand_ConfiguresDetectedAgentEnvironments}[/]",
                        _ => item.ToString()!
                    },
                    preSelected: preSelectedItems,
                    optional: true,
                    binding: assetsBindingWithDefault,
                    // The MCP applicator participates in the interactive multi-select prompt for UX,
                    // but it is not a skill and must not be addressable via `--skills`. Restrict
                    // non-interactive validation to the actual AgentAssetDefinition catalog.
                    bindingChoices: availableAssets.Cast<object>(),
                    echoSelected: false,
                    cancellationToken: cancellationToken);

                selectedAssets = selectedItems.OfType<AgentAssetDefinition>().ToList();

                // Clear MCP applicator if it was not selected by the user.
                if (combinedMcpApplicator is not null && !selectedItems.Contains(combinedMcpApplicator))
                {
                    combinedMcpApplicator = null;
                }
            }

            selectedAssetLocations.AddRange(selectedLocations);
            selectedAgentAssets.AddRange(selectedAssets);

            // --- Phase 3: Apply asset files for selected asset locations × assets ---
            // Each asset file write is fast (small markdown files), so sequential execution
            // is fine — parallelizing would complicate error handling for no meaningful gain.

            foreach (var location in selectedLocations)
            {
                context.AddAgentAssetBaseDirectory(assetKind, location.RelativeDirectory);

                foreach (var asset in selectedAssets)
                {
                    // Playwright CLI is installed via PlaywrightCliInstaller, not as a static asset file
                    if (!asset.HasInstallableFiles)
                    {
                        continue;
                    }

                    if (asset.SourceKind is AgentAssetSourceKind.AspireSkillsBundle && aspireSkillsBundle is null)
                    {
                        continue;
                    }

                    if ((location.Scopes & AgentAssetLocationScope.Workspace) != 0)
                    {
                        var installResult = await InstallAgentAssetAsync(
                            workspaceRoot,
                            location.RelativeDirectory,
                            asset,
                            aspireSkillsBundle,
                            isUserLevel: false,
                            cancellationToken);
                        hasErrors |= !installResult.Succeeded;
                        if (installResult.UpdatedAsset is not null)
                        {
                            installedAssetsSummary.Add(installResult.UpdatedAsset);
                        }
                    }

                    if ((location.Scopes & AgentAssetLocationScope.User) != 0)
                    {
                        var installResult = await InstallAgentAssetAsync(
                            ExecutionContext.HomeDirectory,
                            location.RelativeDirectory,
                            asset,
                            aspireSkillsBundle,
                            isUserLevel: true,
                            cancellationToken);
                        hasErrors |= !installResult.Succeeded;
                        if (installResult.UpdatedAsset is not null)
                        {
                            installedAssetsSummary.Add(installResult.UpdatedAsset);
                        }
                    }
                }
                
            }

            // --- Phase 4: Handle Playwright CLI (installs binary + mirrors asset files to registered directories) ---
            var selectedAssetDirs = selectedLocations.Select(l => l.RelativeDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selectedAssets.Contains(AgentAssetDefinition.PlaywrightCli) && selectedLocations.Count > 0)
            {
                try
                {
                    var (status, message) = await _playwrightCliInstaller.InstallAsync(workspaceRoot.FullName, selectedAssetDirs, cancellationToken);
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
        }

        // --- Phase 6: Install agent telemetry hooks (default-on, parity with azure-skills) ---
        // Hooks are installed for every detected, supported client. Whether telemetry is actually
        // transmitted stays gated by the single ASPIRE_CLI_TELEMETRY_OPTOUT opt-out, which both the
        // hook scripts and the `aspire agent telemetry` command path re-check at runtime.
        await ConfigureTelemetryHooksAsync(context, cancellationToken);

        DisplayInstalledAgentAssetsSummary(installedAssetsSummary);

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
            selectedAssetLocations,
            selectedAgentAssets);
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
            var clientName = skip.Client.Name;
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

    private async Task<(IReadOnlyList<AgentAssetDefinition> Assets, AspireSkillsBundle? Bundle, string? FailureMessage)> ResolveAvailableAssetsAsync(AgentAssetKind assetKind, LanguageId? detectedLanguage, CancellationToken cancellationToken)
    {
        var assets = new List<AgentAssetDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(assetKind, cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
            assets.AddRange(bundle.GetAgentAssetDefinitions().Where(static asset => !IsCliDefinedAssetName(asset.Name)));
        }
        else
        {
            // Preserve the install failure so the caller can surface it only when the user
            // passed an explicit --skills value that names a bundle-only skill. Happy-path
            // (interactive prompt with the embedded fallback) stays silent.
            failureMessage = result.Message;
        }

        // When the bundle is unavailable (network failure, version mismatch, etc.), fall back
        // silently to the CLI-defined assets. The installer already logs the underlying cause
        // at debug level, so the user is not interrupted with a warning they cannot act on.
        assets.AddRange(AgentAssetDefinition.CliDefined);

        return (assets
            .Where(a => a.IsApplicableToLanguage(detectedLanguage) && a.AssetKind == assetKind)
            .ToList(), bundle, failureMessage);
    }

    private static bool HasUnknownBundleAssetCandidate(string requestedAssets, IReadOnlyList<AgentAssetDefinition> availableAssets)
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

    private static bool WasExplicitlyRequested(AgentAssetPromptBindings bindings)
    {
        return IsExplicitSelection(bindings.Locations) || IsExplicitSelection(bindings.Assets);
    }

    private static bool IsExplicitSelection(PromptBinding<string?> binding)
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        return wasProvided &&
            !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase);
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

    private static IReadOnlyList<AgentAssetDefinition> GetDefaultAssets(IEnumerable<AgentAssetDefinition> availableAssets, Func<AgentAssetDefinition, bool>? selectByDefault)
    {
        // When the caller doesn't customize default selection, fall back to AgentAssetDefinition.IsDefault.
        // Bundle-sourced assets are uniformly IsDefault=true; CLI-defined assets (playwright-cli,
        // dotnet-inspect) are IsDefault=false so they stay opt-in. Callers like `aspire new` pass
        // a predicate to additionally filter out assets that don't fit their flow.
        var predicate = selectByDefault ?? (static asset => asset.IsDefault);
        return availableAssets.Where(predicate).ToList();
    }

    /// <summary>
    /// Installs the files for an asset at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns>The install result, including the asset/location pair when files were updated.</returns>
    private async Task<AgentAssetInstallResult> InstallAgentAssetAsync(
        DirectoryInfo rootDirectory,
        string relativeDirectory,
        AgentAssetDefinition asset,
        AspireSkillsBundle? aspireSkillsBundle,
        bool isUserLevel,
        CancellationToken cancellationToken)
    {
        var relativePath = Path.Combine(relativeDirectory, asset.Name);
        var fullDirectoryPath = Path.Combine(rootDirectory.FullName, relativePath);

        try
        {
            var assetFiles = await GetAgentAssetFilesAsync(asset, aspireSkillsBundle, cancellationToken);
            var anyFileUpdated = false;

            foreach (var assetFile in assetFiles)
            {
                var fullPath = Path.Combine(rootDirectory.FullName, relativePath, assetFile.RelativePath);
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

            var displayLocation = GetDisplayAgentAssetDirectory(relativeDirectory, isUserLevel);
            return new(Succeeded: true, new InstalledAgentAssetSummaryItem(asset.Name, asset.AssetKind, displayLocation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var failureFormat = asset.AssetKind switch
            {
                AgentAssetKind.Skill => AgentCommandStrings.InitCommand_FailedToInstallSkill,
                AgentAssetKind.Extension => AgentCommandStrings.InitCommand_FailedToInstallExtension,
                _ => throw new UnreachableException($"Unexpected agent asset kind: {asset.AssetKind}"),
            };
            InteractionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, failureFormat, asset.Name, fullDirectoryPath, ex.Message));
            return new(Succeeded: false, UpdatedAsset: null);
        }
    }

    private void DisplayInstalledAgentAssetsSummary(IReadOnlyList<InstalledAgentAssetSummaryItem> installedAssets)
    {
        if (installedAssets.Count == 0)
        {
            return;
        }

        var assetsByType = installedAssets.GroupBy(static installedAsset => installedAsset.AssetKind);
        StringBuilder messageBuilder = new StringBuilder();
        foreach (var group in assetsByType)
        {
            if (messageBuilder.Length > 0)
            {
                messageBuilder.AppendLine();
            }

            var summaryStrings = group.Key switch
            {
                AgentAssetKind.Skill => (
                    Heading: AgentCommandStrings.InitCommand_InstalledSkillsSummary,
                    Assets: AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                    Locations: AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations),
                AgentAssetKind.Extension => (
                    Heading: AgentCommandStrings.InitCommand_InstalledExtensionsSummary,
                    Assets: AgentCommandStrings.InitCommand_InstalledExtensionsSummaryExtensions,
                    Locations: AgentCommandStrings.InitCommand_InstalledExtensionsSummaryLocations),
                _ => throw new UnreachableException($"Unexpected agent asset kind: {group.Key}"),
            };

            var assetNames = string.Join(", ", GetUniqueValues(group.Select(static installedAsset => installedAsset.AssetName)));
            var locations = string.Join(", ", GetUniqueValues(group.Select(static installedAsset => installedAsset.DisplayLocation)));
            messageBuilder.AppendLine(summaryStrings.Heading);
            messageBuilder.AppendLine(CultureInfo.CurrentCulture, $"  {string.Format(CultureInfo.CurrentCulture, summaryStrings.Assets, assetNames)}");
            messageBuilder.Append(CultureInfo.CurrentCulture, $"  {string.Format(CultureInfo.CurrentCulture, summaryStrings.Locations, locations)}");
        }
        InteractionService.DisplayMessage(KnownEmojis.Robot, messageBuilder.ToString());
    }

    private static (string SelectLocations, string SelectAssets) GetAssetPromptStrings(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => (
                AgentCommandStrings.InitCommand_SelectSkillLocations,
                AgentCommandStrings.InitCommand_SelectSkills),
            AgentAssetKind.Extension => (
                AgentCommandStrings.InitCommand_SelectExtensionLocations,
                AgentCommandStrings.InitCommand_SelectExtensions),
            _ => throw new UnreachableException($"Unexpected agent asset kind: {assetKind}"),
        };
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

    private static string GetDisplayAgentAssetDirectory(string relativeDirectory, bool isUserLevel)
    {
        var displayRelativeDirectory = relativeDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return isUserLevel ? $"~/{displayRelativeDirectory}" : displayRelativeDirectory;
    }

    private static async Task<IReadOnlyList<AgentAssetFile>> GetAgentAssetFilesAsync(AgentAssetDefinition asset, AspireSkillsBundle? aspireSkillsBundle, CancellationToken cancellationToken)
    {
        if (asset.AssetContent is not null && asset.AssetKind == AgentAssetKind.Skill)
        {
            return [new AgentAssetFile("SKILL.md", asset.AssetContent)];
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

    private sealed record InstalledAgentAssetSummaryItem(string AssetName, AgentAssetKind AssetKind, string DisplayLocation);

    private readonly record struct AgentAssetInstallResult(bool Succeeded, InstalledAgentAssetSummaryItem? UpdatedAsset);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<AgentAssetLocation> SelectedLocations,
    IReadOnlyList<AgentAssetDefinition> SelectedAgentAssets);

internal readonly record struct AgentAssetPromptBindings(
    PromptBinding<string?> Locations,
    PromptBinding<string?> Assets);
