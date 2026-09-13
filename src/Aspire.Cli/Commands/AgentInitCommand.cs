// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
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
    private readonly IAgentAssetCatalogProvider _assetCatalogProvider;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ILanguageDiscovery _languageDiscovery;
    private readonly ITelemetryHookConfigurator _telemetryHookConfigurator;
    private readonly IEnvironment _environment;

    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAgentAssetCatalogProvider assetCatalogProvider,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        ITelemetryHookConfigurator telemetryHookConfigurator,
        IEnvironment environment,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _assetCatalogProvider = assetCatalogProvider;
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _languageDiscovery = languageDiscovery;
        _telemetryHookConfigurator = telemetryHookConfigurator;
        _environment = environment;

        Options.Add(s_workspaceRootOption);
        Options.Add(s_skillLocationsOption);
        Options.Add(s_skillsOption);
        Options.Add(s_extensionLocationsOption);
        Options.Add(s_extensionsOption);
        Options.Add(s_mcpOption);
    }

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<string?> s_skillLocationsOption = new("--skill-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillLocationsOptionDescription,
            string.Join(",", SkillCatalog.KnownLocations.Select(l => l.Id)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", SkillCatalog.CliDefined.Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    internal static readonly Option<string?> s_extensionLocationsOption = new("--extension-locations")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_ExtensionLocationsOptionDescription,
            string.Join(",", ExtensionCatalog.KnownLocations.Select(l => l.Id)),
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
    /// Confirms whether standalone <c>aspire agent init</c> configures the Aspire MCP server for
    /// detected agent environments. This option is only registered on this command — <c>aspire
    /// new</c> and <c>aspire init</c> do not add it, so MCP configuration is unavailable from
    /// those chained flows by construction. Omitting the option defaults to "no"; passing
    /// <c>--mcp</c> opts in, and <c>--mcp=false</c> is an explicit opt-out.
    /// </summary>
    internal static readonly Option<bool?> s_mcpOption = new("--mcp")
    {
        Description = AgentCommandStrings.InitCommand_McpOptionDescription,
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
    /// Every bundle-sourced skill is pre-selected, including aspireify.
    /// Chained flows never register <c>--mcp</c> and never offer MCP server configuration: this
    /// method always executes agent init with MCP configuration unavailable, so MCP remains an
    /// explicit opt-in reachable only through standalone <c>aspire agent init</c>.
    /// Callers pass bindings for skill and extension options so the chained execution reuses
    /// the same non-interactive selection semantics as standalone <c>aspire agent init</c>.
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
            return await ExecuteAgentInitAsync(workspaceRoot, skillLocationsBinding, skillsBinding,
                extensionLocationsBinding, extensionsBinding, mcpBinding: null, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        var skillLocationsBinding = PromptBinding.Create(parseResult, s_skillLocationsOption);
        var skillsBinding = PromptBinding.Create(parseResult, s_skillsOption);
        var extensionLocationsBinding = PromptBinding.Create(parseResult, s_extensionLocationsOption);
        var extensionsBinding = PromptBinding.Create(parseResult, s_extensionsOption);
        var mcpBinding = PromptBinding.CreateBoolConfirm(parseResult, s_mcpOption, defaultValue: false);
        var result = await ExecuteAgentInitAsync(workspaceRoot, skillLocationsBinding, skillsBinding,
            extensionLocationsBinding, extensionsBinding, mcpBinding, cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

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
        PromptBinding<string?> skillLocationsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<string?> extensionLocationsBinding,
        PromptBinding<string?> extensionsBinding,
        PromptBinding<bool>? mcpBinding,
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

        // --- Phases 1 and 2: Skill location and asset selection ---
        var skillSelection = await SelectAssetsAsync(
            AgentAssetKind.Skill,
            skillLocationsBinding,
            skillsBinding,
            detectedLanguage,
            AgentCommandStrings.InitCommand_SelectSkillLocations,
            AgentCommandStrings.InitCommand_SelectSkills,
            cancellationToken);
        var selectedLocations = skillSelection.Locations;
        var selectedSkills = skillSelection.Assets;

        // --- Phase 2b: MCP server configuration ---
        // `mcpBinding` is only supplied by standalone `aspire agent init` (see s_mcpOption).
        // Chained flows (`aspire new`, `aspire init`) never construct a binding for it, so MCP
        // configuration is unreachable from those flows by construction rather than by a runtime
        // visibility flag. The Aspire MCP server is the only server the CLI configures today, so
        // this is a plain confirmation rather than a selection; the confirmed choice is applied to
        // every detected configuration target in Phase 5.
        var configureMcp = false;
        var mcpConfigurationTargets = mcpBinding is not null
            ? userChoices.Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments).ToList()
            : [];

        if (mcpBinding is not null && mcpConfigurationTargets.Count > 0)
        {
            configureMcp = await InteractionService.PromptConfirmAsync(
                AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
                binding: mcpBinding,
                cancellationToken: cancellationToken);
        }

        // --- Phase 3: Apply skill files for selected locations × skills ---
        foreach (var location in selectedLocations)
        {
            context.AddSkillBaseDirectory(location.RelativeAssetDirectory);
        }

        var hasErrors = skillSelection.HasErrors;
        hasErrors |= await InstallAssetsAsync(
            skillSelection,
            workspaceRoot,
            new AssetInstallationMessages(
                AgentCommandStrings.InitCommand_FailedToInstallSkill,
                AgentCommandStrings.InitCommand_InstalledSkillsSummary,
                AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills,
                AgentCommandStrings.InitCommand_InstalledSkillsSummaryLocations),
            cancellationToken);

        hasErrors |= await InstallExtensionsAsync(
            context, workspaceRoot, extensionLocationsBinding, extensionsBinding, detectedLanguage, cancellationToken);

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        var selectedSkillDirs = selectedLocations.Select(l => l.RelativeAssetDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSkills.Contains(SkillCatalog.PlaywrightCli) && selectedLocations.Count > 0)
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

        // --- Phase 5: Apply MCP server configuration if the user confirmed ---
        // Apply the confirmed configuration to every detected target exactly once.
        if (configureMcp)
        {
            foreach (var target in mcpConfigurationTargets)
            {
                try
                {
                    await target.ApplyAsync(cancellationToken);
                    InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, target.Description);
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
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, target.Description));
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
            selectedSkills);
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

    private async Task<AgentAssetSelection> SelectAssetsAsync(
        AgentAssetKind assetKind,
        PromptBinding<string?> locationsBinding,
        PromptBinding<string?> assetsBinding,
        LanguageId? detectedLanguage,
        string locationsPrompt,
        string assetsPrompt,
        CancellationToken cancellationToken)
    {
        var catalog = _assetCatalogProvider.GetCatalog(assetKind);
        var defaultLocations = catalog.Locations.Where(static location => location.IsDefault).ToList();
        var selectedLocations = await InteractionService.PromptForSelectionsAsync(
            locationsPrompt,
            catalog.Locations,
            location => $"{location.DisplayName} — {location.Description}",
            preSelected: defaultLocations,
            optional: true,
            binding: locationsBinding.WithDefault(string.Join(",", defaultLocations.Select(static location => location.Id))),
            echoSelected: false,
            cancellationToken: cancellationToken);
        if (selectedLocations.Count == 0)
        {
            return new(catalog, selectedLocations, [], HasErrors: false);
        }

        var (wasProvided, requestedAssets, _) = PromptBinding.Resolve(assetsBinding);
        var result = await _assetCatalogProvider.ResolveAsync(
            assetKind, wasProvided ? requestedAssets : null, detectedLanguage, cancellationToken);
        if (result.DiagnosticMessage is { } message)
        {
            InteractionService.DisplayError(message);
        }

        if (result.IsFailure)
        {
            return new(catalog, selectedLocations, [], HasErrors: true);
        }

        var defaultAssets = result.Assets.Where(static asset => asset.IsDefault).ToList();
        var selectedAssets = await InteractionService.PromptForSelectionsAsync(
            assetsPrompt,
            result.Assets,
            asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
            preSelected: defaultAssets,
            optional: true,
            binding: assetsBinding.WithDefault(string.Join(",", defaultAssets.Select(static asset => asset.Name))),
            echoSelected: false,
            cancellationToken: cancellationToken);
        return new(catalog, selectedLocations, selectedAssets, HasErrors: false);
    }

    /// <summary>
    /// Extracts the single short sentence from an asset description so the selection prompt
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

    /// <summary>
    /// Installs selected file-backed assets and reports only changed destinations.
    /// </summary>
    private async Task<bool> InstallAssetsAsync(
        AgentAssetSelection selection,
        DirectoryInfo workspaceRoot,
        AssetInstallationMessages messages,
        CancellationToken cancellationToken)
    {
        // External installers (such as Playwright CLI) are handled by their own flow.
        var fileBackedAssets = selection.Assets.Where(static asset => asset.HasInstallableFiles).ToArray();
        var installedAssets = new List<InstalledAssetSummaryItem>();
        var hasErrors = false;

        // Preserve location -> asset -> workspace/user ordering, including the first-seen
        // order used by summaries and failure messages.
        foreach (var location in selection.Locations)
        {
            foreach (var asset in fileBackedAssets)
            {
                foreach (var target in selection.Catalog.ResolveInstallTargets(
                    location, workspaceRoot, ExecutionContext.HomeDirectory, _environment))
                {
                    try
                    {
                        if (await selection.Catalog.FileInstaller.InstallAsync(
                            target.RootDirectory, target.RelativeAssetDirectory, asset.Name, asset.Files, cancellationToken))
                        {
                            installedAssets.Add(new(asset.Name, target.DisplayDirectory));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
                    {
                        InteractionService.DisplayError(
                            string.Format(CultureInfo.CurrentCulture, messages.FailureFormat, asset.Name,
                                Path.Combine(target.RootDirectory.FullName, target.RelativeAssetDirectory, asset.Name), ex.Message));
                        hasErrors = true;
                    }
                }
            }
        }

        DisplayInstalledAssetsSummary(installedAssets, messages);
        return hasErrors;
    }

    private void DisplayInstalledAssetsSummary(
        IReadOnlyList<InstalledAssetSummaryItem> installedAssets,
        AssetInstallationMessages messages)
    {
        if (installedAssets.Count == 0)
        {
            return;
        }

        var assetNames = string.Join(", ", GetUniqueValues(installedAssets.Select(static asset => asset.AssetName)));
        var locations = string.Join(", ", GetUniqueValues(installedAssets.Select(static asset => asset.DisplayLocation)));
        var message = string.Join(Environment.NewLine,
            messages.SummaryHeading,
            $"  {string.Format(CultureInfo.CurrentCulture, messages.AssetNamesFormat, assetNames)}",
            $"  {string.Format(CultureInfo.CurrentCulture, messages.LocationsFormat, locations)}");

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

    private async Task<bool> InstallExtensionsAsync(
        AgentEnvironmentScanContext context,
        DirectoryInfo workspaceRoot,
        PromptBinding<string?> locationsBinding,
        PromptBinding<string?> extensionsBinding,
        LanguageId? detectedLanguage,
        CancellationToken cancellationToken)
    {
        var (locationsProvided, requestedLocations, _) = PromptBinding.Resolve(locationsBinding);
        var (extensionsProvided, requestedExtensions, _) = PromptBinding.Resolve(extensionsBinding);
        if (string.Equals(requestedLocations, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedExtensions, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Explicit options allow provisioning a workspace for another machine without
        // requiring a compatible client locally.
        var catalog = _assetCatalogProvider.GetCatalog(AgentAssetKind.Extension);
        if (!catalog.IsCompatibleWith(context.DetectedClients))
        {
            if (!locationsProvided && !extensionsProvided)
            {
                return false;
            }

            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.InitCommand_NoCompatibleClientForExplicitExtensions);
        }

        var selection = await SelectAssetsAsync(
            AgentAssetKind.Extension,
            locationsBinding,
            extensionsBinding,
            detectedLanguage,
            AgentCommandStrings.InitCommand_SelectExtensionLocations,
            AgentCommandStrings.InitCommand_SelectExtensions,
            cancellationToken);

        return selection.HasErrors || await InstallAssetsAsync(
            selection,
            workspaceRoot,
            new AssetInstallationMessages(
                AgentCommandStrings.InitCommand_FailedToInstallExtension,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummary,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummaryExtensions,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummaryLocations),
            cancellationToken);
    }

    private sealed record InstalledAssetSummaryItem(string AssetName, string DisplayLocation);

    private sealed record AgentAssetSelection(
        IAgentAssetCatalog Catalog,
        IReadOnlyList<AgentAssetLocation> Locations,
        IReadOnlyList<AgentAssetDefinition> Assets,
        bool HasErrors);

    private sealed record AssetInstallationMessages(
        string FailureFormat,
        string SummaryHeading,
        string AssetNamesFormat,
        string LocationsFormat);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<AgentAssetLocation> SelectedLocations,
    IReadOnlyList<AgentAssetDefinition> SelectedSkills);
