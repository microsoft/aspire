// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
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
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes configuration and copied skills for selected agents.
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
    private readonly ICliHostEnvironment _hostEnvironment;
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
        _hostEnvironment = services.HostEnvironment;
        Options.Add(s_workspaceRootOption);
        Options.Add(s_mcpOption);
        Options.Add(s_agentsOption);
        Options.Add(s_scopeOption);
        Options.Add(s_skillsOption);
    }

    internal static readonly Option<string?> s_agentsOption = CreateAgentsOption();
    internal static readonly Option<AgentConfigurationScope> s_scopeOption = CreateScopeOption();

    internal static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<string?> s_skillsOption = new("--skills")
    {
        Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_SkillsOptionDescription,
            string.Join(",", SkillDefinition.CliDefined.Select(s => s.Name)),
            ConsoleInteractionService.AllChoice,
            ConsoleInteractionService.NoneChoice),
        Recursive = true
    };

    /// <summary>
    /// Confirms whether standalone <c>aspire agent init</c> configures the Aspire MCP server for
    /// selected agent environments. This option is only registered on this command — <c>aspire
    /// new</c> and <c>aspire init</c> do not add it, so MCP configuration is unavailable from
    /// those chained flows by construction. Omitting the option defaults to "no"; passing
    /// <c>--mcp</c> opts in, and <c>--mcp=false</c> is an explicit opt-out.
    /// </summary>
    internal static readonly Option<bool?> s_mcpOption = new("--mcp")
    {
        Description = AgentCommandStrings.InitCommand_McpOptionDescription,
        Recursive = true
    };

    private static Option<string?> CreateAgentsOption()
    {
        var supportedClients = string.Join(",", AgentClient.All.Select(static client => client.Id));
        var option = new Option<string?>("--agents")
        {
            HelpName = "agents",
            Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_EnvironmentsOptionDescription,
                supportedClients, ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice),
            Recursive = true,
            Arity = ArgumentArity.ExactlyOne,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var value = result.Tokens[0].Value;
                var selected = ConsoleInteractionService.MatchChoices(value, AgentClient.All, static client => client.Id);
                if (selected is null || selected.Count == 0 &&
                    !string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
                {
                    result.AddError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
                        value, supportedClients, ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice));
                }

                return value;
            }
        };
        option.CompletionSources.Clear();
        option.CompletionSources.Add(static _ =>
            [.. AgentClient.All.Select(static client => new CompletionItem(client.Id)),
                new CompletionItem(ConsoleInteractionService.AllChoice), new CompletionItem(ConsoleInteractionService.NoneChoice)]);
        return option;
    }

    private static Option<AgentConfigurationScope> CreateScopeOption()
    {
        var option = new Option<AgentConfigurationScope>("--scope")
        {
            HelpName = "project|global",
            Description = AgentCommandStrings.InitCommand_ScopeOptionDescription,
            Recursive = true,
            Arity = ArgumentArity.ExactlyOne
        };
        // Native enum parsing also accepts numeric and composite values; scope is one named choice.
        option.Validators.Add(result =>
        {
            if (result.Tokens.Count == 1 &&
                Enum.TryParse<AgentConfigurationScope>(result.Tokens[0].Value, ignoreCase: true, out _) &&
                !Enum.GetNames<AgentConfigurationScope>().Contains(result.Tokens[0].Value, StringComparer.OrdinalIgnoreCase))
            {
                result.AddError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidScope, result.Tokens[0].Value));
            }
        });
        option.CompletionSources.Clear();
        option.CompletionSources.Add(static _ => [new CompletionItem("project"), new CompletionItem("global")]);

        return option;
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
    /// Every bundle-sourced skill is pre-selected, including aspireify.
    /// Chained flows never register <c>--mcp</c> and never offer MCP server configuration: this
    /// method always executes agent init with MCP configuration unavailable, so MCP remains an
    /// explicit opt-in reachable only through standalone <c>aspire agent init</c>.
    /// The agent, skill, and scope bindings reuse the standalone command's non-interactive selection semantics.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> agentsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<AgentConfigurationScope> scopeBinding,
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
            return await ExecuteAgentInitAsync(
                workspaceRoot, agentsBinding, skillsBinding, scopeBinding, mcpBinding: null, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        var result = await ExecuteAgentInitAsync(
            workspaceRoot,
            PromptBinding.Create(parseResult, s_agentsOption),
            PromptBinding.Create(parseResult, s_skillsOption),
            PromptBinding.Create(parseResult, s_scopeOption, AgentConfigurationScope.Project),
            PromptBinding.CreateBoolConfirm(parseResult, s_mcpOption, defaultValue: false),
            cancellationToken);
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
        PromptBinding<string?> agentsBinding,
        PromptBinding<string?> skillsBinding,
        PromptBinding<AgentConfigurationScope> scopeBinding,
        PromptBinding<bool>? mcpBinding,
        CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        AgentEnvironment? detectedEnvironments = null;

        var (agentsProvided, requestedAgents) = agentsBinding.Resolve();
        IReadOnlyList<AgentClient> selectedClients;
        if (agentsProvided && requestedAgents is not null)
        {
            var matchedClients = ConsoleInteractionService.MatchChoices(requestedAgents, AgentClient.All, static client => client.Id);
            if (matchedClients is null)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
                    requestedAgents, string.Join(",", AgentClient.All.Select(static client => client.Id)),
                    ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice));
                return new(CliExitCodes.InvalidCommand, [], []);
            }

            selectedClients = matchedClients;
        }
        else
        {
            detectedEnvironments = await InteractionService.ShowStatusAsync(
                McpCommandStrings.InitCommand_DetectingAgentEnvironments,
                async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
                emoji: KnownEmojis.Robot);

            // Shared discovery does not authorize configuring another agent. Defaults
            // express detected agent use, independently of assets and their destinations.
            var defaultClients = AgentClient.All
                .Where(client => client.Includes(detectedEnvironments.Value)).ToArray();
            if (defaultClients.Length == 0)
            {
                defaultClients = [AgentClient.Copilot];
            }

            selectedClients = await InteractionService.PromptForSelectionsAsync(
                AgentCommandStrings.InitCommand_SelectAgentsPrompt,
                AgentClient.All.OrderByDescending(client => defaultClients.Contains(client)),
                static client => client.DisplayName.EscapeMarkup(),
                preSelected: defaultClients,
                optional: true,
                binding: agentsBinding.WithDefault(string.Join(",", defaultClients.Select(static client => client.Id))),
                echoSelected: false,
                cancellationToken: cancellationToken);
        }

        // Both explicit and interactive opt-outs stop before acquiring the skill catalog.
        if (selectedClients.Count == 0)
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NoConfigurationSelected);
            return new(CliExitCodes.Success, [], []);
        }

        // Detect the AppHost language to determine which skills to offer.
        // When no language is detected (e.g., standalone `aspire agent init`), language-restricted skills are excluded.
        var detectedLanguage = await _languageDiscovery.DetectLanguageRecursiveAsync(workspaceRoot, cancellationToken);

        AspireSkillsBundle? aspireSkillsBundle = null;
        string? bundleInstallFailureMessage = null;

        IReadOnlyList<SkillDefinition> availableSkills;
        if (ShouldSkipBundleCatalogResolution(skillsBinding))
        {
            availableSkills = SkillDefinition.CliDefined
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

        var selectedSkills = await InteractionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_SelectSkills,
            availableSkills,
            skill => $"{skill.Name.EscapeMarkup()} — {SimplifyDescription(skill.Description).EscapeMarkup()}",
            preSelected: defaultSkills,
            optional: true,
            binding: skillsBindingWithDefault,
            echoSelected: false,
            cancellationToken: cancellationToken);

        // `mcpBinding` is only supplied by standalone `aspire agent init` (see s_mcpOption).
        // Chained flows (`aspire new`, `aspire init`) never construct a binding for it, so MCP
        // configuration is unreachable from those flows by construction rather than by a runtime
        // visibility flag. The Aspire MCP server is the only server the CLI configures today, so
        // this is a plain confirmation rather than a selection; the confirmed choice is applied to
        // every selected configuration target.
        var configureMcp = false;
        if (mcpBinding is not null)
        {
            configureMcp = await InteractionService.PromptConfirmAsync(
                AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
                binding: mcpBinding,
                cancellationToken: cancellationToken);
        }

        if (selectedSkills.Count == 0 && !configureMcp)
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NoConfigurationSelected);
            return new(CliExitCodes.Success, selectedClients, []);
        }

        var (scopeProvided, requestedScope) = scopeBinding.Resolve();
        var scope = scopeProvided ? requestedScope : scopeBinding.DefaultValue;
        var installSkills = selectedSkills.Count > 0;

        if (!scopeProvided && _hostEnvironment.SupportsInteractiveInput)
        {
            scope = await InteractionService.PromptForSelectionAsync(
                AgentCommandStrings.InitCommand_SelectScopePrompt,
                new[] { AgentConfigurationScope.Project, AgentConfigurationScope.Global },
                value => value is AgentConfigurationScope.Project
                    ? AgentCommandStrings.InitCommand_ProjectScope
                    : AgentCommandStrings.InitCommand_GlobalScope,
                cancellationToken: cancellationToken);
        }

        if (scope is AgentConfigurationScope.Global && detectedEnvironments is null)
        {
            // Capture hook evidence before configuration can create new detection markers.
            detectedEnvironments = await InteractionService.ShowStatusAsync(
                McpCommandStrings.InitCommand_DetectingAgentEnvironments,
                async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
                emoji: KnownEmojis.Robot);
        }

        DisplayInstallationLocations(selectedClients, scope, workspaceRoot, installSkills, configureMcp);

        // The selected clients determine destinations; defaults never fabricate detection.
        var selectedSkillDirs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (selectedSkills.Count > 0)
        {
            selectedSkillDirs.UnionWith(selectedClients
                .Select(client => client.GetSkillBaseDirectory(scope, workspaceRoot, ExecutionContext, _environment)));
        }

        // --- Phase 3: Apply copied skill files at each unique destination ---
        // Each skill file write is fast (small markdown files), so sequential execution
        // is fine — parallelizing would complicate error handling for no meaningful gain.
        var hasErrors = false;

        var installedSkills = new List<InstalledSkillSummaryItem>();

        foreach (var skillDirectory in selectedSkillDirs)
        {
            foreach (var skill in selectedSkills)
            {
                // Playwright CLI is installed via PlaywrightCliInstaller, not as a static skill file
                if (!skill.HasInstallableFiles)
                {
                    continue;
                }

                if (skill.SourceKind is SkillSourceKind.AspireSkillsBundle && aspireSkillsBundle is null)
                {
                    continue;
                }

                var installResult = await InstallSkillAsync(
                    skillDirectory,
                    GetDisplayConfigurationPath(skillDirectory, scope, workspaceRoot),
                    skill,
                    aspireSkillsBundle,
                    cancellationToken);
                hasErrors |= !installResult.Succeeded;
                if (installResult.UpdatedSkill is not null)
                {
                    installedSkills.Add(installResult.UpdatedSkill);
                }
            }
        }

        DisplayInstalledSkillsSummary(installedSkills);

        // --- Phase 4: Handle Playwright CLI (installs binary + mirrors skill files to registered directories) ---
        if (selectedSkills.Contains(SkillDefinition.PlaywrightCli))
        {
            try
            {
                var (status, message) = await _playwrightCliInstaller.InstallSelectedAsync(selectedSkillDirs, cancellationToken);
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
        if (configureMcp)
        {
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            // Aliased paths can use distinct server keys; coalesce file I/O, not client formats.
            var targetsByPath = selectedClients.SelectMany(client =>
                client.GetMcpConfigPaths(scope, workspaceRoot, ExecutionContext, _environment)
                    .Select(target => (Client: client, target.Path, target.ServersPropertyName, target.ConfigureServer)))
                .GroupBy(static target => target.Path, pathComparer);
            foreach (var targets in targetsByPath)
            {
                var firstTarget = targets.First();
                var description = string.Format(CultureInfo.CurrentCulture,
                    AgentCommandStrings.InitCommand_McpConfigurationDescription, firstTarget.Client.DisplayName, targets.Key);
                try
                {
                    var (config, configFilePath) = await McpConfigFileHelper.ReadSelectedConfigAsync(
                        targets.Key, firstTarget.Client, scope, _environment, cancellationToken);
                    var notifications = new List<(KnownEmoji Emoji, string Message)>();
                    // Legacy repairs share the same selection, scope, and consent as new configuration.
                    foreach (var (client, _, serversPropertyName, configureServer) in targets)
                    {
                        var updated = McpConfigFileHelper.UpdateDeprecatedCommand(config, serversPropertyName);
                        var configured = configureServer && McpConfigFileHelper.ConfigureMcpServer(
                            config, client, scope, configFilePath);

                        if (updated)
                        {
                            notifications.Add((KnownEmojis.Wrench,
                                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigUpdate_Description, client.DisplayName)));
                        }

                        if (configured)
                        {
                            notifications.Add((KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture,
                                AgentCommandStrings.InitCommand_McpConfigurationDescription, client.DisplayName, configFilePath)));
                        }
                    }

                    if (notifications.Count > 0)
                    {
                        await ConfigurationHelper.WriteSettingsFileAsync(configFilePath, config, cancellationToken);
                    }

                    foreach (var (emoji, message) in notifications)
                    {
                        InteractionService.DisplayMessage(emoji, message);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    InteractionService.DisplayError(ex.Message);
                    if (ex.InnerException is JsonException)
                    {
                        InteractionService.DisplaySubtleMessage(
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, description));
                    }
                    hasErrors = true;
                }
            }
        }

        // --- Phase 6: Install agent telemetry hooks (default-on, parity with azure-skills) ---
        // Global setup instruments selected, detected supported environments only. Whether telemetry is actually
        // transmitted stays gated by the single ASPIRE_CLI_TELEMETRY_OPTOUT opt-out, which both the
        // hook scripts and the `aspire agent telemetry` command path re-check at runtime.
        if (scope is AgentConfigurationScope.Global)
        {
            var selectedEnvironments = selectedClients.Aggregate(AgentEnvironment.None,
                static (environments, client) => environments | client.Environments);
            await ConfigureTelemetryHooksAsync(detectedEnvironments.GetValueOrDefault() & selectedEnvironments, cancellationToken);
        }

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
            selectedClients,
            selectedSkills);
    }

    private async Task ConfigureTelemetryHooksAsync(AgentEnvironment detectedEnvironments, CancellationToken cancellationToken)
    {
        TelemetryHookConfigurationResult result;
        try
        {
            result = await _telemetryHookConfigurator.ConfigureAsync(detectedEnvironments, cancellationToken);
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

        if (result.ConfiguredEnvironments != AgentEnvironment.None)
        {
            var environmentNames = string.Join(", ", Enum.GetValues<AgentEnvironment>()
                .Where(environment => environment != AgentEnvironment.None && result.ConfiguredEnvironments.HasFlag(environment))
                .Select(GetEnvironmentDisplayName));
            InteractionService.DisplayMessage(
                KnownEmojis.BarChart,
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHooksInstalled, environmentNames));
        }

        foreach (var skip in result.Skipped)
        {
            var environmentName = GetEnvironmentDisplayName(skip.Environment);
            var message = skip.Reason switch
            {
                TelemetryHookSkipReason.MalformedConfig => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedMalformedConfig, environmentName),
                TelemetryHookSkipReason.UnexpectedConfigShape => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookSkippedUnexpectedShape, environmentName),
                _ => string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_TelemetryHookWriteFailed, environmentName),
            };

            // Skips are surfaced to the user but never treated as command failures: a user-owned
            // config we can't safely modify must not break `agent init`.
            InteractionService.DisplaySubtleMessage(message);
        }
    }

    private static string GetEnvironmentDisplayName(AgentEnvironment environment)
        => environment switch
        {
            AgentEnvironment.CopilotCli => "GitHub Copilot CLI",
            AgentEnvironment.CopilotApp => "GitHub Copilot App",
            AgentEnvironment.ClaudeCode => "Claude Code",
            AgentEnvironment.VsCode => "VS Code",
            AgentEnvironment.OpenCode => "OpenCode",
            _ => environment.ToString(),
        };

    private async Task<(IReadOnlyList<SkillDefinition> Skills, AspireSkillsBundle? Bundle, string? FailureMessage)> ResolveAvailableSkillsAsync(LanguageId? detectedLanguage, CancellationToken cancellationToken)
    {
        var skills = new List<SkillDefinition>();
        AspireSkillsBundle? bundle = null;
        string? failureMessage = null;

        var result = await _aspireSkillsInstaller.InstallAsync(cancellationToken);
        if (result.Status is AspireSkillsInstallStatus.Installed)
        {
            bundle = result.Bundle ?? throw new InvalidOperationException("Aspire skills installer returned an installed result without a bundle.");
            skills.AddRange(bundle.GetSkillDefinitions().Where(static skill => !IsCliDefinedSkillName(skill.Name)));
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
        skills.AddRange(SkillDefinition.CliDefined);

        return (skills
            .Where(s => s.IsApplicableToLanguage(detectedLanguage))
            .ToList(), bundle, failureMessage);
    }

    private static bool HasUnknownBundleSkillCandidate(string requestedSkills, IReadOnlyList<SkillDefinition> availableSkills)
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
        return SkillDefinition.CliDefined.Any(skill => skill.HasName(name, StringComparison.OrdinalIgnoreCase));
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
        string skillBaseDirectory,
        string displayLocation,
        SkillDefinition skill,
        AspireSkillsBundle? aspireSkillsBundle,
        CancellationToken cancellationToken)
    {
        var fullSkillDirectoryPath = Path.Combine(skillBaseDirectory, skill.Name);

        try
        {
            var skillFiles = await GetSkillFilesAsync(skill, aspireSkillsBundle, cancellationToken);
            var anyFileUpdated = false;

            foreach (var skillFile in skillFiles)
            {
                var fullPath = Path.Combine(fullSkillDirectoryPath, skillFile.RelativePath);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(fullPath))
                {
                    var existingContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                    if (string.Equals(existingContent.ReplaceLineEndings("\n"), skillFile.Content.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                    {
                        continue;
                    }
                }

                await File.WriteAllTextAsync(fullPath, skillFile.Content, cancellationToken);
                anyFileUpdated = true;
            }

            if (!anyFileUpdated)
            {
                return new(Succeeded: true, UpdatedSkill: null);
            }

            return new(Succeeded: true, new InstalledSkillSummaryItem(skill.Name, displayLocation));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
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

    private string GetDisplayConfigurationPath(string path, AgentConfigurationScope scope, DirectoryInfo workspaceRoot)
    {
        var root = scope is AgentConfigurationScope.Project ? workspaceRoot : ExecutionContext.HomeDirectory;
        var relativePath = Path.GetRelativePath(root.FullName, path);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return path;
        }

        var displayRelativePath = relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        return scope is AgentConfigurationScope.Global ? $"~/{displayRelativePath}" : displayRelativePath;
    }

    private void DisplayInstallationLocations(IReadOnlyList<AgentClient> clients, AgentConfigurationScope scope, DirectoryInfo workspaceRoot, bool installSkills, bool configureMcp)
    {
        var lines = new List<string> { AgentCommandStrings.InitCommand_InstallationLocations };
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hasSharedLocation = false;
        if (installSkills)
        {
            var directories = clients.Select(client => client.GetSkillBaseDirectory(scope, workspaceRoot, ExecutionContext, _environment))
                .Distinct(pathComparer);
            foreach (var directory in directories)
            {
                AddLocation(AgentCommandStrings.InitCommand_SkillsLocationHint, directory,
                    [.. AgentClient.All.Where(client => pathComparer.Equals(directory,
                        client.GetSkillBaseDirectory(scope, workspaceRoot, ExecutionContext, _environment)))]);
            }
        }

        if (configureMcp)
        {
            var paths = clients.Select(client => client.GetMcpConfigPath(scope, workspaceRoot, ExecutionContext, _environment))
                .Distinct(pathComparer);
            foreach (var path in paths)
            {
                AddLocation(AgentCommandStrings.InitCommand_McpLocationHint, path,
                    [.. AgentClient.All.Where(client => pathComparer.Equals(path,
                        client.GetMcpConfigPath(scope, workspaceRoot, ExecutionContext, _environment)))]);
            }
        }

        if (hasSharedLocation)
        {
            lines.Add(AgentCommandStrings.InitCommand_SharedLocationNotice);
        }

        InteractionService.DisplaySubtleMessage(string.Join(Environment.NewLine, lines));

        void AddLocation(string format, string path, IReadOnlyList<AgentClient> sharedClients)
        {
            var location = GetDisplayConfigurationPath(path, scope, workspaceRoot);
            if (sharedClients.Count > 1)
            {
                hasSharedLocation = true;
                location = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_SharedLocation,
                    location, string.Join(", ", sharedClients.Select(static client => client.DisplayName)));
            }

            lines.Add($"  {string.Format(CultureInfo.CurrentCulture, format, location)}");
        }
    }

    private static async Task<IReadOnlyList<SkillAssetFile>> GetSkillFilesAsync(SkillDefinition skill, AspireSkillsBundle? aspireSkillsBundle, CancellationToken cancellationToken)
    {
        if (skill.SkillContent is not null)
        {
            return [new SkillAssetFile("SKILL.md", skill.SkillContent)];
        }

        if (skill.SourceKind is SkillSourceKind.AspireSkillsBundle)
        {
            if (aspireSkillsBundle is null)
            {
                throw new InvalidOperationException($"Aspire skills bundle was not resolved for skill '{skill.Name}'.");
            }

            return await aspireSkillsBundle.GetSkillFilesAsync(skill, cancellationToken);
        }

        throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
    }

    private sealed record InstalledSkillSummaryItem(string SkillName, string DisplayLocation);

    private readonly record struct SkillInstallResult(bool Succeeded, InstalledSkillSummaryItem? UpdatedSkill);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<AgentClient> SelectedClients,
    IReadOnlyList<SkillDefinition> SelectedSkills);
