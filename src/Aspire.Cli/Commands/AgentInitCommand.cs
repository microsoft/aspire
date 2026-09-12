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
            string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Skill).Select(l => l.Id)),
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
            string.Join(",", AgentAssetLocation.GetLocations(AgentAssetKind.Extension).Select(l => l.Id)),
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

        // --- Phase 1: Skill location selection ---
        var skillLocations = AgentAssetLocation.GetLocations(AgentAssetKind.Skill);
        var defaultLocationIds = string.Join(",", skillLocations.Where(l => l.IsDefault).Select(l => l.Id));
        var skillLocationsBindingWithDefault = skillLocationsBinding.WithDefault(defaultLocationIds);

        var selectedLocations = await InteractionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectSkillLocations,
            skillLocations,
            loc => $"{loc.DisplayName} — {loc.Description}",
            preSelected: skillLocations.Where(l => l.IsDefault),
            optional: true,
            binding: skillLocationsBindingWithDefault,
            echoSelected: false,
            cancellationToken: cancellationToken);

        // --- Phase 2: Skill selection (only if locations were selected) ---
        IReadOnlyList<AgentAssetDefinition> selectedSkills = [];
        AspireSkillsBundle? aspireSkillsBundle = null;
        string? bundleInstallFailureMessage = null;

        if (selectedLocations.Count > 0)
        {
            IReadOnlyList<AgentAssetDefinition> availableSkills;
            if (ShouldSkipBundleCatalogResolution(skillsBinding))
            {
                availableSkills = AgentAssetDefinition.CliDefined
                    .Where(s => s.IsApplicableToLanguage(detectedLanguage))
                    .ToList();
            }
            else
            {
                (availableSkills, aspireSkillsBundle, bundleInstallFailureMessage) = await ResolveAvailableSkillsAsync(detectedLanguage, cancellationToken);
            }

            // Order the merged catalog deterministically by name so the prompt is stable
            // regardless of manifest order. OrdinalIgnoreCase matches the case-insensitive
            // --skills parsing used elsewhere.
            availableSkills = [.. availableSkills.OrderBy(static s => s.Name, StringComparer.OrdinalIgnoreCase)];

            var defaultSkills = availableSkills.Where(static s => s.IsDefault).ToList();
            var defaultSkillNames = string.Join(",", defaultSkills.Select(s => s.Name));
            var skillsBindingWithDefault = skillsBinding.WithDefault(defaultSkillNames);

            // When the bundle failed to install and the caller passed an explicit --skills value
            // that names a bundle-only skill, the upcoming MatchChoicesOrThrow will reject the
            // value as "not a valid choice" with no hint that the underlying cause was the
            // bundle. Surface the install failure first so users can see why the catalog is short.
            // We only do this when the value contains a name that is not in the available catalog
            // and not a CLI-defined skill, so happy-path runs stay silent.
            if (bundleInstallFailureMessage is not null)
            {
                var (wasProvided, requestedSkills, _) = PromptBinding.Resolve(skillsBindingWithDefault);
                if (wasProvided && requestedSkills is not null && HasUnknownBundleSkillCandidate(requestedSkills, availableSkills))
                {
                    InteractionService.DisplayError(bundleInstallFailureMessage);
                }
            }

            selectedSkills = await InteractionService.PromptForSelectionsAsync(
                AgentCommandStrings.InitCommand_SelectSkills,
                availableSkills,
                skill => $"{skill.Name.EscapeMarkup()} — {SimplifyDescription(skill.Description).EscapeMarkup()}",
                preSelected: defaultSkills,
                optional: true,
                binding: skillsBindingWithDefault,
                echoSelected: false,
                cancellationToken: cancellationToken);
        }

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
        // Each skill file write is fast (small markdown files), so sequential execution
        // is fine — parallelizing would complicate error handling for no meaningful gain.
        var hasErrors = false;

        var installedSkills = new List<InstalledSkillSummaryItem>();

        foreach (var location in selectedLocations)
        {
            context.AddSkillBaseDirectory(location.RelativeAssetDirectory);

            foreach (var skill in selectedSkills)
            {
                // Playwright CLI is installed via PlaywrightCliInstaller, not as a static skill file
                if (!skill.HasInstallableFiles)
                {
                    continue;
                }

                if (skill.SourceKind is AgentAssetSourceKind.AspireSkillsBundle && aspireSkillsBundle is null)
                {
                    continue;
                }

                var installResult = await InstallSkillAsync(
                    workspaceRoot,
                    location.RelativeAssetDirectory,
                    skill,
                    aspireSkillsBundle,
                    isUserLevel: false,
                    cancellationToken);
                hasErrors |= !installResult.Succeeded;
                if (installResult.UpdatedSkill is not null)
                {
                    installedSkills.Add(installResult.UpdatedSkill);
                }

                if (location.Scopes.HasFlag(AgentAssetLocationScope.User))
                {
                    installResult = await InstallSkillAsync(
                        ExecutionContext.HomeDirectory,
                        location.RelativeAssetDirectory,
                        skill,
                        aspireSkillsBundle,
                        isUserLevel: true,
                        cancellationToken);
                    hasErrors |= !installResult.Succeeded;
                    if (installResult.UpdatedSkill is not null)
                    {
                        installedSkills.Add(installResult.UpdatedSkill);
                    }
                }
            }
        }

        DisplayInstalledSkillsSummary(installedSkills);

        hasErrors |= await InstallExtensionsAsync(
            context, workspaceRoot, extensionLocationsBinding, extensionsBinding, detectedLanguage, cancellationToken);

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        var selectedSkillDirs = selectedLocations.Select(l => l.RelativeAssetDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedSkills.Contains(AgentAssetDefinition.PlaywrightCli) && selectedLocations.Count > 0)
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

    private async Task<(IReadOnlyList<AgentAssetDefinition> Skills, AspireSkillsBundle? Bundle, string? FailureMessage)> ResolveAvailableSkillsAsync(LanguageId? detectedLanguage, CancellationToken cancellationToken)
    {
        var skills = new List<AgentAssetDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(AgentAssetKind.Skill, cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
            skills.AddRange(bundle.Assets.Where(static skill => !IsCliDefinedSkillName(skill.Name)));
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
        skills.AddRange(AgentAssetDefinition.CliDefined);

        return (skills
            .Where(s => s.IsApplicableToLanguage(detectedLanguage))
            .ToList(), bundle, failureMessage);
    }

    private static bool HasUnknownBundleSkillCandidate(string requestedSkills, IReadOnlyList<AgentAssetDefinition> availableSkills)
    {
        // Tokens like "all" / "none" don't name skills, so the "looks like a bundle skill but missing"
        // diagnostic doesn't apply — let the normal validation path handle them.
        if (string.IsNullOrWhiteSpace(requestedSkills) ||
            string.Equals(requestedSkills, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(requestedSkills, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requested = requestedSkills.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in requested)
        {
            if (IsCliDefinedSkillName(name))
            {
                continue;
            }

            if (!availableSkills.Any(s => s.HasName(name, StringComparison.OrdinalIgnoreCase)))
            {
                // A non-CLI name that isn't in the catalog is exactly the case the bundle would have provided.
                return true;
            }
        }

        return false;
    }

    private static bool ShouldSkipBundleCatalogResolution(PromptBinding<string?> skillsBinding)
    {
        var (wasProvided, optionValue, _) = PromptBinding.Resolve(skillsBinding);
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

        var selectedSkillNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return selectedSkillNames.Length > 0 &&
               selectedSkillNames.All(static name => IsCliDefinedSkillName(name));
    }

    private static bool IsCliDefinedSkillName(string name)
    {
        return AgentAssetDefinition.CliDefined.Any(skill => skill.HasName(name, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Installs the files for a skill at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns>The install result, including the skill/location pair when files were updated.</returns>
    private async Task<SkillInstallResult> InstallSkillAsync(
        DirectoryInfo rootDirectory,
        string relativeSkillDirectory,
        AgentAssetDefinition skill,
        AspireSkillsBundle? aspireSkillsBundle,
        bool isUserLevel,
        CancellationToken cancellationToken)
    {
        var relativeSkillPath = Path.Combine(relativeSkillDirectory, skill.Name);
        var fullSkillDirectoryPath = Path.Combine(rootDirectory.FullName, relativeSkillPath);

        try
        {
            var skillFiles = await GetSkillFilesAsync(skill, aspireSkillsBundle, cancellationToken);
            var anyFileUpdated = await AgentAssetFileInstaller.InstallAsync(
                rootDirectory, relativeSkillDirectory, skill, skillFiles, cancellationToken);

            if (!anyFileUpdated)
            {
                return new(Succeeded: true, UpdatedSkill: null);
            }

            var displayLocation = GetDisplaySkillDirectory(relativeSkillDirectory, isUserLevel);
            return new(Succeeded: true, new InstalledSkillSummaryItem(skill.Name, displayLocation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
        {
            InteractionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_FailedToInstallSkill, skill.Name, fullSkillDirectoryPath, ex.Message));
            return new(Succeeded: false, UpdatedSkill: null);
        }
    }

    private void DisplayInstalledSkillsSummary(IReadOnlyList<InstalledSkillSummaryItem> installedSkills)
    {
        if (installedSkills.Count == 0)
        {
            return;
        }

        var skillNames = string.Join(", ", GetUniqueValues(installedSkills.Select(static installedSkill => installedSkill.SkillName)));
        var locations = string.Join(", ", GetUniqueValues(installedSkills.Select(static installedSkill => installedSkill.DisplayLocation)));
        var message = string.Join(Environment.NewLine,
            AgentCommandStrings.InitCommand_InstalledSkillsSummary,
            $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledSkillsSummarySkills, skillNames)}",
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

    private static string GetDisplaySkillDirectory(string relativeSkillDirectory, bool isUserLevel)
    {
        var displayRelativeSkillDirectory = relativeSkillDirectory
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return isUserLevel ? $"~/{displayRelativeSkillDirectory}" : displayRelativeSkillDirectory;
    }

    private static async Task<IReadOnlyList<AgentAssetFile>> GetSkillFilesAsync(AgentAssetDefinition skill, AspireSkillsBundle? aspireSkillsBundle, CancellationToken cancellationToken)
    {
        if (skill.Files.Count > 0)
        {
            return skill.Files.Where(file => skill.ShouldInstallFile(file.RelativePath)).ToList();
        }

        if (skill.SourceKind is AgentAssetSourceKind.AspireSkillsBundle)
        {
            if (aspireSkillsBundle is null)
            {
                throw new InvalidOperationException($"Aspire skills bundle was not resolved for skill '{skill.Name}'.");
            }

            return await aspireSkillsBundle.GetAssetFilesAsync(skill, cancellationToken);
        }

        throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
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

        // Only Copilot App currently loads these extensions. Explicit options still allow
        // provisioning a workspace for another machine without requiring that client locally.
        if (!context.DetectedClients.Contains(AgentClientKind.CopilotApp))
        {
            if (!locationsProvided && !extensionsProvided)
            {
                return false;
            }

            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.InitCommand_NoCompatibleClientForExplicitExtensions);
        }

        var locations = AgentAssetLocation.GetLocations(AgentAssetKind.Extension);
        var defaultLocations = locations.Where(l => l.IsDefault).ToList();
        var selectedLocations = await InteractionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectExtensionLocations,
            locations,
            location => $"{location.DisplayName} — {location.Description}",
            preSelected: defaultLocations,
            optional: true,
            binding: locationsBinding.WithDefault(string.Join(",", defaultLocations.Select(l => l.Id))),
            echoSelected: false,
            cancellationToken: cancellationToken);
        if (selectedLocations.Count == 0)
        {
            return false;
        }

        var result = await _aspireSkillsInstaller.InstallAsync(AgentAssetKind.Extension, cancellationToken);
        if (result.Status is not AspireSkillsInstallStatus.Installed)
        {
            InteractionService.DisplayError(result.Message ?? AgentCommandStrings.InitCommand_ExtensionBundleUnavailable);
            return true;
        }

        var bundle = result.Bundle ?? throw new InvalidOperationException("Extension installer returned an installed result without a bundle.");
        var extensions = bundle.Assets
            .Where(asset => asset.IsApplicableToLanguage(detectedLanguage))
            .OrderBy(asset => asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var defaultExtensions = extensions.Where(asset => asset.IsDefault).ToList();
        var selectedExtensions = await InteractionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectExtensions,
            extensions,
            asset => $"{asset.Name.EscapeMarkup()} — {SimplifyDescription(asset.Description).EscapeMarkup()}",
            preSelected: defaultExtensions,
            optional: true,
            binding: extensionsBinding.WithDefault(string.Join(",", defaultExtensions.Select(asset => asset.Name))),
            echoSelected: false,
            cancellationToken: cancellationToken);

        var installed = new List<InstalledSkillSummaryItem>();
        var hasErrors = false;
        foreach (var location in selectedLocations)
        {
            var target = location.Scopes.HasFlag(AgentAssetLocationScope.Workspace)
                ? new AgentAssetInstallTarget(workspaceRoot, location.RelativeAssetDirectory, GetDisplaySkillDirectory(location.RelativeAssetDirectory, isUserLevel: false))
                : location.ResolveUserInstallTarget(ExecutionContext.HomeDirectory, _environment);
            foreach (var extension in selectedExtensions)
            {
                try
                {
                    var files = await bundle.GetAssetFilesAsync(extension, cancellationToken);
                    if (await AgentAssetFileInstaller.InstallAsync(target.RootDirectory, target.RelativeAssetDirectory, extension, files, cancellationToken))
                    {
                        installed.Add(new(extension.Name, target.DisplayDirectory));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or AggregateException)
                {
                    InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture,
                        AgentCommandStrings.InitCommand_FailedToInstallExtension, extension.Name,
                        Path.Combine(target.RootDirectory.FullName, target.RelativeAssetDirectory, extension.Name), ex.Message));
                    hasErrors = true;
                }
            }
        }

        if (installed.Count > 0)
        {
            InteractionService.DisplayMessage(KnownEmojis.Robot, string.Join(Environment.NewLine,
                AgentCommandStrings.InitCommand_InstalledExtensionsSummary,
                $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledExtensionsSummaryExtensions, string.Join(", ", GetUniqueValues(installed.Select(asset => asset.SkillName))))}",
                $"  {string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InstalledExtensionsSummaryLocations, string.Join(", ", GetUniqueValues(installed.Select(asset => asset.DisplayLocation))))}"));
        }

        return hasErrors;
    }

    private sealed record InstalledSkillSummaryItem(string SkillName, string DisplayLocation);

    private readonly record struct SkillInstallResult(bool Succeeded, InstalledSkillSummaryItem? UpdatedSkill);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<AgentAssetLocation> SelectedLocations,
    IReadOnlyList<AgentAssetDefinition> SelectedSkills);
