// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
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
    private readonly IReadOnlyList<IAgentClient> _clients;
    private readonly PlaywrightCliInstaller _playwrightCliInstaller;
    private readonly IGitRepository _gitRepository;
    private readonly ITelemetryHookConfigurator _telemetryHookConfigurator;
    private readonly IEnvironment _environment;
    private readonly ICliHostEnvironment _hostEnvironment;
    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IEnumerable<IAgentClient> clients,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ITelemetryHookConfigurator telemetryHookConfigurator,
        IEnvironment environment,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _clients = clients.ToArray();
        AgentsOption = CreateAgentsOption(_clients);
        _playwrightCliInstaller = playwrightCliInstaller;
        _gitRepository = gitRepository;
        _telemetryHookConfigurator = telemetryHookConfigurator;
        _environment = environment;
        _hostEnvironment = services.HostEnvironment;
        Options.Add(s_workspaceRootOption);
        Options.Add(s_mcpOption);
        Options.Add(AgentsOption);
        Options.Add(s_scopeOption);
        Options.Add(s_playwrightOption);
        Options.Add(s_dotnetInspectOption);
        Options.Add(s_aspireSkillsOption);
    }

    internal Option<string?> AgentsOption { get; }
    internal static readonly Option<AgentConfigurationScope> s_scopeOption = CreateScopeOption();

    internal static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<AgentConfirmation?> s_playwrightOption = CreateConfirmationOption("--playwright", AgentCommandStrings.InitCommand_PlaywrightOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_dotnetInspectOption = CreateConfirmationOption("--dotnet-inspect", AgentCommandStrings.InitCommand_DotnetInspectOptionDescription);
    internal static readonly Option<AspireSkillsMode> s_aspireSkillsOption = CreateAspireSkillsOption();

    /// <summary>
    /// Confirms whether standalone <c>aspire agent init</c> configures the Aspire MCP server for
    /// selected agent environments. This option is only registered on this command — <c>aspire
    /// new</c> and <c>aspire init</c> do not add it, so MCP configuration is unavailable from
    /// those chained flows by construction. Omitting the option defaults to "no"; passing
    /// <c>--mcp</c> opts in, and <c>--mcp=false</c> is an explicit opt-out.
    /// </summary>
    internal static readonly Option<AgentConfirmation?> s_mcpOption = CreateConfirmationOption("--mcp", AgentCommandStrings.InitCommand_McpOptionDescription);

    private static Option<AgentConfirmation?> CreateConfirmationOption(string name, string description)
    {
        // System.CommandLine treats bool options as flags before invoking CustomParser,
        // leaving the required "--mcp n" / "--playwright y" arguments unmatched.
        var option = new Option<AgentConfirmation?>(name)
        {
            Description = description,
            Recursive = true,
            Arity = ArgumentArity.ZeroOrOne,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return AgentConfirmation.Yes;
                }
                var value = result.Tokens[0].Value;
                if (string.Equals(value, "y", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return AgentConfirmation.Yes;
                }
                if (string.Equals(value, "n", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return AgentConfirmation.No;
                }
                result.AddError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidAssetValue, name, value));
                return null;
            }
        };
        option.CompletionSources.Clear();
        option.CompletionSources.Add("y", "n", "true", "false");

        return option;
    }

    internal static PromptBinding<bool> CreateConfirmationBinding(ParseResult parseResult, Option<AgentConfirmation?> option)
        => new(parseResult, $"'{option.Name}'",
            result => (result.GetResult(option) is { Implicit: false }, result.GetValue(option) is AgentConfirmation.Yes),
            defaultValue: false, hasExplicitDefault: true);

    private static Option<AspireSkillsMode> CreateAspireSkillsOption()
    {
        var option = new Option<AspireSkillsMode>("--aspire-skills")
        {
            Description = AgentCommandStrings.InitCommand_AspireSkillsOptionDescription,
            Recursive = true,
            CustomParser = result =>
            {
                // Enum conversion also accepts "0" and "native,none"; only a named source is valid here.
                var value = result.Tokens.SingleOrDefault()?.Value;
                if (value is not null && Enum.GetNames<AspireSkillsMode>().Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return Enum.Parse<AspireSkillsMode>(value, ignoreCase: true);
                }
                result.AddError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidAspireSkillsMode, value));
                return AspireSkillsMode.None;
            }
        };
        option.CompletionSources.Clear();
        option.CompletionSources.Add("native", "none");

        return option;
    }

    private static Option<string?> CreateAgentsOption(IReadOnlyList<IAgentClient> clients)
    {
        var clientIds = clients.Select(static client => client.Id).ToArray();
        var supportedClients = string.Join(",", clientIds);
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
                var selected = ConsoleInteractionService.MatchChoices(value, clientIds, static id => id);
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
        option.CompletionSources.Add(_ =>
            [.. clientIds.Select(static id => new CompletionItem(id)),
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
    /// Chained flows never register <c>--mcp</c> and never offer MCP server configuration: this
    /// method always executes agent init with MCP configuration unavailable, so MCP remains an
    /// explicit opt-in reachable only through standalone <c>aspire agent init</c>.
    /// The agent, asset, and scope bindings reuse the standalone command's selection semantics.
    /// </summary>
    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        PromptBinding<string?> agentsBinding,
        PromptBinding<bool> playwrightBinding,
        PromptBinding<bool> dotnetInspectBinding,
        PromptBinding<AspireSkillsMode> aspireSkillsBinding,
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
                workspaceRoot, agentsBinding, playwrightBinding, dotnetInspectBinding, aspireSkillsBinding,
                scopeBinding, mcpBinding: null, cancellationToken);
        }

        return new(CliExitCodes.Success, [], []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        var result = await ExecuteAgentInitAsync(
            workspaceRoot,
            PromptBinding.Create(parseResult, AgentsOption),
            CreateConfirmationBinding(parseResult, s_playwrightOption),
            CreateConfirmationBinding(parseResult, s_dotnetInspectOption),
            PromptBinding.Create(parseResult, s_aspireSkillsOption, AspireSkillsMode.Native),
            PromptBinding.Create(parseResult, s_scopeOption, AgentConfigurationScope.Project),
            CreateConfirmationBinding(parseResult, s_mcpOption),
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
        PromptBinding<bool> playwrightBinding,
        PromptBinding<bool> dotnetInspectBinding,
        PromptBinding<AspireSkillsMode> aspireSkillsBinding,
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
        IReadOnlyList<IAgentClient> selectedClients;
        if (agentsProvided && requestedAgents is not null)
        {
            var matchedClients = ConsoleInteractionService.MatchChoices(requestedAgents, _clients, static client => client.Id);
            if (matchedClients is null)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
                    requestedAgents, string.Join(",", _clients.Select(static client => client.Id)),
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
            var defaultClients = _clients
                .Where(client => (client.Environments & detectedEnvironments.Value) != AgentEnvironment.None).ToArray();
            if (defaultClients.Length == 0)
            {
                defaultClients = [_clients.Single(client => client.Id == CopilotAgentClient.ClientId)];
            }

            selectedClients = await InteractionService.PromptForSelectionsAsync(
                AgentCommandStrings.InitCommand_SelectAgentsPrompt,
                _clients.OrderByDescending(client => defaultClients.Contains(client)),
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

        var playwright = await InteractionService.PromptConfirmAsync(
            McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt, binding: playwrightBinding, cancellationToken: cancellationToken);
        var dotnetInspect = await InteractionService.PromptConfirmAsync(
            AgentCommandStrings.InitCommand_ConfigureDotnetInspectPrompt, binding: dotnetInspectBinding, cancellationToken: cancellationToken);
        var (modeProvided, skillsMode) = aspireSkillsBinding.Resolve();
        if (!modeProvided)
        {
            skillsMode = !_hostEnvironment.SupportsInteractiveInput ? aspireSkillsBinding.DefaultValue
                : await InteractionService.PromptForSelectionAsync(
                    AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt,
                    new[] { AspireSkillsMode.Native, AspireSkillsMode.None },
                    mode => mode switch
                    {
                        AspireSkillsMode.Native => AgentCommandStrings.InitCommand_NativeSkillsChoice,
                        AspireSkillsMode.None => AgentCommandStrings.InitCommand_NoSkillsChoice,
                        _ => throw new UnreachableException()
                    },
                    cancellationToken: cancellationToken);
        }

        if (skillsMode is AspireSkillsMode.None && !playwright && !dotnetInspect && !configureMcp)
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NoConfigurationSelected);
            return new(CliExitCodes.Success, selectedClients, []);
        }

        var (scopeProvided, requestedScope) = scopeBinding.Resolve();
        var scope = scopeProvided ? requestedScope : scopeBinding.DefaultValue;

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

        try
        {
            string? conflict = null;
            if (skillsMode is AspireSkillsMode.Native)
            {
                var local = LocalAspireSkills.Find(ExecutionContext.WorkingDirectory, workspaceRoot,
                    selectedClients, cancellationToken);
                if (local.Errors.Count > 0 || local.Files.Count > 0)
                {
                    conflict = string.Join(System.Environment.NewLine, local.Errors.Concat(local.Files.Count > 0
                        ? [string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_LocalSkillsConflict,
                            string.Join(System.Environment.NewLine, local.Files))]
                        : []));
                }
            }
            if (conflict is not null)
            {
                InteractionService.DisplayError(conflict);
                return new(CliExitCodes.InvalidCommand, selectedClients, []);
            }
        }
        catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            InteractionService.DisplayError(ex.Message);
            return new(CliExitCodes.InvalidCommand, selectedClients, []);
        }

        List<SkillDefinition> selectedSkills = [];
        if (playwright)
        {
            selectedSkills.Add(SkillDefinition.PlaywrightCli);
        }
        if (dotnetInspect)
        {
            selectedSkills.Add(SkillDefinition.DotnetInspect);
        }
        if (selectedSkills.Count > 0 || configureMcp)
        {
            DisplayInstallationLocations(selectedClients, scope, workspaceRoot, selectedSkills.Count > 0, configureMcp);
        }

        // The selected clients determine destinations; defaults never fabricate detection.
        var selectedSkillDirs = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (selectedSkills.Count > 0)
        {
            selectedSkillDirs.UnionWith(selectedClients
                .Select(client => client.GetSkillBaseDirectory(scope, workspaceRoot)));
        }

        // --- Phase 3: Apply copied skill files at each unique destination ---
        // Each skill file write is fast (small markdown files), so sequential execution
        // is fine — parallelizing would complicate error handling for no meaningful gain.
        var hasErrors = false;
        IReadOnlyList<IAgentClient> registeredClients = [];
        if (skillsMode is AspireSkillsMode.Native)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.InitCommand_PluginRiskWarning);
            InteractionService.DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture,
                AgentCommandStrings.InitCommand_DirectSkillsAlternative, AspireSkillsConfiguration.RepositoryUrl));
            cancellationToken.ThrowIfCancellationRequested();
            var edits = selectedClients.Distinct().Select(client => client.CreateNativeSkillsEdit(workspaceRoot, scope)).ToArray();
            var results = await AgentConfigurationEditor.ApplyAsync(edits, cancellationToken);
            foreach (var result in results)
            {
                var format = result.Status switch
                {
                    AgentConfigurationStatus.Configured => AgentCommandStrings.InitCommand_RegisteredTarget,
                    AgentConfigurationStatus.Unchanged => AgentCommandStrings.InitCommand_UnchangedTarget,
                    AgentConfigurationStatus.Skipped => AgentCommandStrings.InitCommand_SkippedTarget,
                    AgentConfigurationStatus.Blocked => AgentCommandStrings.InitCommand_BlockedTarget,
                    AgentConfigurationStatus.Failed => AgentCommandStrings.InitCommand_FailedTarget,
                    _ => throw new UnreachableException()
                };
                var message = string.Format(CultureInfo.CurrentCulture, format,
                    AgentCommandStrings.InitCommand_AspireSkillsAsset,
                    string.Join(", ", result.Clients.Select(client => client.DisplayName)),
                    scope is AgentConfigurationScope.Project ? AgentCommandStrings.InitCommand_ProjectScope : AgentCommandStrings.InitCommand_GlobalScope,
                    result.TargetPath);
                if (result.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed)
                {
                    InteractionService.DisplayError(message);
                    hasErrors = true;
                }
                else
                {
                    InteractionService.DisplaySubtleMessage(message);
                }
                if (result.Message is not null)
                {
                    InteractionService.DisplaySubtleMessage(result.Message);
                }
            }
            registeredClients = results
                .Where(result => result.Status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged)
                .SelectMany(result => result.Clients)
                .Distinct()
                .ToArray();
            if (registeredClients.Count > 0)
            {
                InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_ClientAcquisitionNotice);
            }
        }

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

                var installResult = await InstallSkillAsync(
                    skillDirectory,
                    GetDisplayConfigurationPath(skillDirectory, scope, workspaceRoot),
                    skill,
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
            var edits = new List<AgentFileEdit>();
            foreach (var client in selectedClients)
            {
                edits.AddRange(await client.CreateMcpEditsAsync(workspaceRoot, scope, cancellationToken));
            }

            foreach (var result in await AgentConfigurationEditor.ApplyAsync(edits, cancellationToken))
            {
                if (result.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed)
                {
                    InteractionService.DisplayError(result.Error?.Message ?? result.Message!);
                    if (result.Error?.InnerException is JsonException)
                    {
                        var description = string.Format(CultureInfo.CurrentCulture,
                            AgentCommandStrings.InitCommand_McpConfigurationDescription, result.Clients[0].DisplayName, result.TargetPath);
                        InteractionService.DisplaySubtleMessage(
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, description));
                    }
                    hasErrors = true;
                    continue;
                }

                // Changes are only reported after the entire physical file is published.
                foreach (var change in result.Changes)
                {
                    if (change.Edit.Repaired)
                    {
                        InteractionService.DisplayMessage(KnownEmojis.Wrench,
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigUpdate_Description, change.Client.DisplayName));
                    }
                    if (change.Edit.Added)
                    {
                        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton,
                            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_McpConfigurationDescription, change.Client.DisplayName, change.Path));
                    }
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
            selectedSkills)
        {
            NativeRegisteredClients = registeredClients
        };
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

    /// <summary>
    /// Installs the files for a skill at the specified location, creating or updating them as needed.
    /// </summary>
    /// <returns>The install result, including the skill/location pair when files were updated.</returns>
    private async Task<SkillInstallResult> InstallSkillAsync(
        string skillBaseDirectory,
        string displayLocation,
        SkillDefinition skill,
        CancellationToken cancellationToken)
    {
        var fullSkillDirectoryPath = Path.Combine(skillBaseDirectory, skill.Name);

        try
        {
            var skillContent = skill.SkillContent
                ?? throw new InvalidOperationException($"Skill '{skill.Name}' does not define installable files.");
            var fullPath = Path.Combine(fullSkillDirectoryPath, "SKILL.md");
            if (!Directory.Exists(fullSkillDirectoryPath))
            {
                Directory.CreateDirectory(fullSkillDirectoryPath);
            }

            if (File.Exists(fullPath))
            {
                var existingContent = await File.ReadAllTextAsync(fullPath, cancellationToken);
                if (string.Equals(existingContent.ReplaceLineEndings("\n"), skillContent.ReplaceLineEndings("\n"), StringComparison.Ordinal))
                {
                    return new(Succeeded: true, UpdatedSkill: null);
                }
            }

            await File.WriteAllTextAsync(fullPath, skillContent, cancellationToken);
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

    private void DisplayInstallationLocations(IReadOnlyList<IAgentClient> clients, AgentConfigurationScope scope, DirectoryInfo workspaceRoot, bool installSkills, bool configureMcp)
    {
        var lines = new List<string> { AgentCommandStrings.InitCommand_InstallationLocations };
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var hasSharedLocation = false;
        if (installSkills)
        {
            var directories = clients.Select(client => client.GetSkillBaseDirectory(scope, workspaceRoot))
                .Distinct(pathComparer);
            foreach (var directory in directories)
            {
                AddLocation(AgentCommandStrings.InitCommand_SkillsLocationHint, directory,
                    [.. _clients.Where(client => pathComparer.Equals(directory,
                        client.GetSkillBaseDirectory(scope, workspaceRoot)))]);
            }
        }

        if (configureMcp)
        {
            var paths = clients.Select(client => client.GetMcpConfigPath(scope, workspaceRoot))
                .Distinct(pathComparer);
            foreach (var path in paths)
            {
                AddLocation(AgentCommandStrings.InitCommand_McpLocationHint, path,
                    [.. _clients.Where(client => pathComparer.Equals(path,
                        client.GetMcpConfigPath(scope, workspaceRoot)))]);
            }
        }

        if (hasSharedLocation)
        {
            lines.Add(AgentCommandStrings.InitCommand_SharedLocationNotice);
        }

        InteractionService.DisplaySubtleMessage(string.Join(Environment.NewLine, lines));

        void AddLocation(string format, string path, IReadOnlyList<IAgentClient> sharedClients)
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

    private sealed record InstalledSkillSummaryItem(string SkillName, string DisplayLocation);

    private readonly record struct SkillInstallResult(bool Succeeded, InstalledSkillSummaryItem? UpdatedSkill);
}

internal readonly record struct AgentInitExecutionResult(
    int ExitCode,
    IReadOnlyList<IAgentClient> SelectedClients,
    IReadOnlyList<SkillDefinition> SelectedSkills)
{
    public IReadOnlyList<IAgentClient> NativeRegisteredClients { get; init; } = [];
}

internal enum AspireSkillsMode
{
    Native,
    None
}

internal enum AgentConfirmation
{
    Yes,
    No
}
