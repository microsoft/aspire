// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Selects independent assets and configuration environments before applying changes.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand
{
    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<AgentConfirmation?> s_mcpOption = CreateAssetOption("--mcp", AgentCommandStrings.InitCommand_McpOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_playwrightOption = CreateAssetOption("--playwright", AgentCommandStrings.InitCommand_PlaywrightOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_dotnetInspectOption = CreateAssetOption("--dotnet-inspect", AgentCommandStrings.InitCommand_DotnetInspectOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_aspireSkillsOption = CreateAssetOption("--aspire-skills", AgentCommandStrings.InitCommand_AspireSkillsOptionDescription);

    private readonly IReadOnlyList<IAgentEnvironmentScanner> _environmentScanners;
    private readonly AgentConfigurationWriter _configurationWriter;
    private readonly IAgentSkillInstaller _skillInstaller;
    private readonly ITelemetryHookConfigurator _hooks;
    private readonly IGitRepository _gitRepository;
    private readonly IEnvironment _environment;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly Option<string?> _environmentsOption;

    public AgentInitCommand(
        IEnumerable<IAgentEnvironmentScanner> environmentScanners,
        AgentConfigurationWriter configurationWriter,
        IAgentSkillInstaller skillInstaller,
        ITelemetryHookConfigurator hooks,
        IGitRepository gitRepository,
        IEnvironment environment,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _environmentScanners = Array.AsReadOnly(environmentScanners.ToArray());
        _configurationWriter = configurationWriter;
        _skillInstaller = skillInstaller;
        _hooks = hooks;
        _gitRepository = gitRepository;
        _environment = environment;
        _hostEnvironment = services.HostEnvironment;
        _environmentsOption = CreateEnvironmentsOption();

        AddOptions(this, includeMcp: true, includeWorkspaceRoot: true);
    }

    internal void AddOptions(Command command, bool includeMcp, bool includeWorkspaceRoot)
    {
        if (includeWorkspaceRoot)
        {
            command.Options.Add(s_workspaceRootOption);
        }

        if (includeMcp)
        {
            command.Options.Add(s_mcpOption);
        }

        command.Options.Add(s_playwrightOption);
        command.Options.Add(s_dotnetInspectOption);
        command.Options.Add(s_aspireSkillsOption);
        command.Options.Add(_environmentsOption);
    }

    internal AgentInitPromptBindings CreateBindings(ParseResult parseResult, bool includeMcp) => new(
        includeMcp ? CreateAssetBinding(parseResult, s_mcpOption, defaultValue: false) : null,
        CreateAssetBinding(parseResult, s_playwrightOption, defaultValue: false),
        CreateAssetBinding(parseResult, s_dotnetInspectOption, defaultValue: false),
        CreateAssetBinding(parseResult, s_aspireSkillsOption, defaultValue: true),
        PromptBinding.Create(parseResult, _environmentsOption));

    internal Task<CommandResult> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    internal async Task<AgentInitExecutionResult> PromptAndChainAsync(
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        PromptBinding<bool> agentInitBinding,
        AgentInitPromptBindings bindings,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != CliExitCodes.Success)
        {
            return new(previousResultExitCode, []);
        }

        interactionService.DisplayEmptyLine();
        var runAgentInit = await interactionService.PromptConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            binding: agentInitBinding,
            cancellationToken: cancellationToken);

        // Chained new/init flows never offer MCP, even if called with a standalone binding.
        return runAgentInit
            ? await ExecuteAgentInitAsync(workspaceRoot, bindings with { Mcp = null }, cancellationToken)
            : new(CliExitCodes.Success, []);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(parseResult, cancellationToken);
        var result = await ExecuteAgentInitAsync(workspaceRoot, CreateBindings(parseResult, includeMcp: true), cancellationToken);
        return CommandResult.FromExitCode(result.ExitCode);
    }

    private static PromptBinding<bool> CreateAssetBinding(ParseResult parseResult, Option<AgentConfirmation?> option, bool defaultValue)
    {
        return new PromptBinding<bool>(
            parseResult,
            $"'{option.Name}'",
            result => (result.GetResult(option) is { Implicit: false }, result.GetValue(option) is AgentConfirmation.Yes),
            defaultValue,
            hasExplicitDefault: true);
    }

    private static Option<AgentConfirmation?> CreateAssetOption(string name, string description)
    {
        // System.CommandLine tokenizes bool options before CustomParser: "--mcp n"
        // becomes a bare true flag and an unmatched "n". A typed confirmation accepts
        // the requested y/n tokens while the prompt binding still exposes a bool.
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

                // Accept both "--mcp n" and "--mcp=false"; omitted flags remain unset
                // so the same option can drive interactive prompts and unattended defaults.
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
        option.CompletionSources.Add(static _ =>
        [
            new CompletionItem("y"),
            new CompletionItem("n"),
            new CompletionItem("true"),
            new CompletionItem("false")
        ]);

        return option;
    }

    private Option<string?> CreateEnvironmentsOption()
    {
        var clientIds = _environmentScanners.Select(static scanner => scanner.Id).ToArray();
        var supportedClients = string.Join(",", clientIds);

        return new Option<string?>("--environments")
        {
            Description = string.Format(CultureInfo.InvariantCulture, AgentCommandStrings.InitCommand_EnvironmentsOptionDescription,
                supportedClients, ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice),
            Recursive = true,
            CustomParser = result =>
            {
                if (result.Tokens.Count == 0)
                {
                    return null;
                }

                var value = result.Tokens[0].Value;
                if (string.Equals(value, ConsoleInteractionService.AllChoice, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, ConsoleInteractionService.NoneChoice, StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }

                var requestedClients = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (requestedClients.Length == 0 ||
                    requestedClients.Any(client => !clientIds.Contains(client, StringComparer.OrdinalIgnoreCase)))
                {
                    result.AddError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
                        value, supportedClients, ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice));
                }

                return value;
            }
        };
    }

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;
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
        AgentInitPromptBindings bindings,
        CancellationToken cancellationToken)
    {
        var mcp = bindings.Mcp is not null && await InteractionService.PromptConfirmAsync(
            AgentCommandStrings.InitCommand_ConfigureMcpServerPrompt,
            binding: bindings.Mcp,
            cancellationToken: cancellationToken);
        var playwright = await InteractionService.PromptConfirmAsync(
            McpCommandStrings.InitCommand_ConfigurePlaywrightPrompt,
            binding: bindings.Playwright,
            cancellationToken: cancellationToken);
        var dotnetInspect = await InteractionService.PromptConfirmAsync(
            AgentCommandStrings.InitCommand_ConfigureDotnetInspectPrompt,
            binding: bindings.DotnetInspect,
            cancellationToken: cancellationToken);
        var aspireSkills = await InteractionService.PromptConfirmAsync(
            AgentCommandStrings.InitCommand_ConfigureAspireSkillsPrompt,
            binding: bindings.AspireSkills,
            cancellationToken: cancellationToken);
        var assets = new AgentAssetSelection(mcp, playwright, dotnetInspect, aspireSkills);

        if (!assets.HasAssets)
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NoConfigurationSelected);
            return new(CliExitCodes.Success, []);
        }

        var detectedEnvironments = new HashSet<IAgentEnvironmentScanner>();
        var detections = await InteractionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var found = new List<AgentClientDetection>();
                foreach (var scanner in _environmentScanners)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var context = new AgentEnvironmentScanContext(ExecutionContext.WorkingDirectory, workspaceRoot);
                    await scanner.ScanAsync(context, cancellationToken);
                    if (context.DetectedClients.Count > 0)
                    {
                        detectedEnvironments.Add(scanner);
                        found.AddRange(context.DetectedClients);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Array.AsReadOnly(found.Distinct().ToArray());
            },
            emoji: KnownEmojis.Robot);

        var defaults = _environmentScanners.Where(detectedEnvironments.Contains).ToArray();
        InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_EnvironmentSelectionNotice);
        var (wasProvided, requestedEnvironments) = bindings.Environments.Resolve();
        IReadOnlyList<IAgentEnvironmentScanner> clients;
        if (wasProvided && requestedEnvironments is not null)
        {
            // Resolve IDs without preparing destination descriptions for unselected environments.
            var selected = ConsoleInteractionService.MatchChoices(requestedEnvironments, _environmentScanners, static scanner => scanner.Id);
            if (selected is null)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_InvalidEnvironments,
                    requestedEnvironments, string.Join(",", _environmentScanners.Select(static scanner => scanner.Id)),
                    ConsoleInteractionService.AllChoice, ConsoleInteractionService.NoneChoice));
                return new(CliExitCodes.InvalidCommand, []);
            }

            clients = selected;
        }
        else if (!_hostEnvironment.SupportsInteractiveInput)
        {
            clients = defaults;
        }
        else
        {
            var previewRequest = new AgentInitRequest(workspaceRoot, assets, _environmentScanners, detections);
            var descriptions = new Dictionary<IAgentEnvironmentScanner, string>();
            clients = await InteractionService.PromptForSelectionsAsync(
                McpCommandStrings.InitCommand_AgentConfigurationSelectPrompt,
                _environmentScanners,
                scanner => descriptions.TryGetValue(scanner, out var description)
                    ? description
                    : descriptions[scanner] = DescribeEnvironment(scanner, previewRequest),
                preSelected: defaults,
                optional: true,
                cancellationToken: cancellationToken);
        }

        if (clients.Count == 0)
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NoConfigurationSelected);
            return new(CliExitCodes.Success, []);
        }

        using var activity = Telemetry.StartReportedActivity("AgentInit.Configure");
        activity?.SetTag("aspire.agent.clients", string.Join(",", clients.Select(static client => client.Id)));
        activity?.SetTag("aspire.agent.mcp", mcp);
        activity?.SetTag("aspire.agent.playwright", playwright);
        activity?.SetTag("aspire.agent.dotnet_inspect", dotnetInspect);
        activity?.SetTag("aspire.agent.aspire_skills", aspireSkills);

        var result = await ConfigureAsync(
            new AgentInitRequest(workspaceRoot, assets, clients.Distinct().ToArray(), detections),
            _configurationWriter,
            _skillInstaller,
            _hooks,
            cancellationToken);

        DisplayResults(result);
        activity?.SetTag("aspire.agent.configuration_failed", result.HasErrors);
        activity?.SetTag("aspire.agent.configuration_warnings", result.HasWarnings);
        if (result.HasErrors)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        return new(result.HasErrors ? CliExitCodes.InvalidCommand : CliExitCodes.Success, result.RegisteredEnvironments);
    }

    /// <summary>
    /// Applies native configuration and detected hooks together, then installs selected managed skills.
    /// </summary>
    internal static async Task<AgentInitResult> ConfigureAsync(
        AgentInitRequest request,
        AgentConfigurationWriter writer,
        IAgentSkillInstaller skillInstaller,
        ITelemetryHookConfigurator hooks,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Assets.HasAssets || request.Environments.Count == 0)
        {
            return new AgentInitResult([]);
        }

        IEnumerable<AgentConfigurationTarget> nativeTargets = request.Assets.Mcp || request.Assets.AspireSkills
            ? request.Environments.Distinct().SelectMany(environment => environment.GetTargets(request))
            : [];
        // Native settings and hooks can share a file and must be committed together.
        var results = new List<AgentTargetResult>(
            await writer.ApplyAsync(nativeTargets.Concat(hooks.Plan(request)), cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        if (request.Assets.Playwright || request.Assets.DotnetInspect)
        {
            results.AddRange(await skillInstaller.InstallAsync(request, cancellationToken));
        }

        return new AgentInitResult(results);
    }

    private string DescribeEnvironment(IAgentEnvironmentScanner scanner, AgentInitRequest request)
    {
        try
        {
            // Show both standalone and shared locations: Copilot can reuse Claude's .mcp.json.
            // Targets are deferred edits; enumerating them never applies configuration.
            var ownTargets = scanner.GetTargets(request with { Environments = [scanner] }).ToArray();
            var sharedTargets = scanner.GetTargets(request).ToArray();
            var sharedScopes = sharedTargets
                .Where(target => !ownTargets.Any(own => own.Scope == target.Scope && AgentPath.Comparer.Equals(own.Path, target.Path)))
                .Select(target => target.Scope).ToHashSet();
            var locations = ownTargets.Concat(sharedTargets)
                .Select(target => (target.Scope, target.Path))
                .ToList();
            foreach (var scope in new[] { AgentConfigurationScope.Project, AgentConfigurationScope.User })
            {
                foreach (var asset in new[] { AgentAssetKind.Playwright, AgentAssetKind.DotnetInspect })
                {
                    if ((asset is AgentAssetKind.Playwright && request.Assets.Playwright) ||
                        (asset is AgentAssetKind.DotnetInspect && request.Assets.DotnetInspect))
                    {
                        locations.Add((scope, Path.Combine(
                            AgentSkillInstaller.GetSkillBaseDirectory(scanner, scope, request.WorkspaceRoot, ExecutionContext, _environment),
                            AgentSkillInstaller.GetSkillName(asset))));
                    }
                }
            }

            var lines = new List<string> { scanner.DisplayName.EscapeMarkup() };
            foreach (var group in locations.GroupBy(location => location.Scope).OrderBy(group => group.Key))
            {
                var scope = group.Key is AgentConfigurationScope.Project
                    ? AgentCommandStrings.InitCommand_ProjectScope
                    : AgentCommandStrings.InitCommand_UserScope;
                if (sharedScopes.Contains(group.Key))
                {
                    scope = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.InitCommand_ScopeWithSharedAlternatives, scope);
                }
                var paths = group.Select(location => DisplayPath(location.Path, group.Key)).Distinct(AgentPath.Comparer);
                lines.Add("  " + string.Format(CultureInfo.CurrentCulture,
                    AgentCommandStrings.InitCommand_EnvironmentLocationDescription, scope, string.Join(", ", paths)).EscapeMarkup());
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return scanner.DisplayName.EscapeMarkup() + Environment.NewLine + "  " +
                string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.Configuration_ReadWriteFailed, ex.Message).EscapeMarkup();
        }

        string DisplayPath(string path, AgentConfigurationScope scope)
        {
            var root = scope is AgentConfigurationScope.Project ? request.WorkspaceRoot.FullName : ExecutionContext.HomeDirectory.FullName;
            var relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return path;
            }

            return scope is AgentConfigurationScope.Project ? relative : Path.Combine("~", relative);
        }
    }

    private void DisplayResults(AgentInitResult result)
    {
        foreach (var target in result.Targets)
        {
            var clients = string.Join(", ", target.Environments.Select(client => client.DisplayName));
            var scope = target.Scope is AgentConfigurationScope.Project ? AgentCommandStrings.InitCommand_ProjectScope : AgentCommandStrings.InitCommand_UserScope;
            var asset = target.Asset switch
            {
                AgentAssetKind.Mcp => AgentCommandStrings.InitCommand_McpAsset,
                AgentAssetKind.Playwright => AgentCommandStrings.InitCommand_PlaywrightAsset,
                AgentAssetKind.DotnetInspect => AgentCommandStrings.InitCommand_DotnetInspectAsset,
                AgentAssetKind.AspireSkills => AgentCommandStrings.InitCommand_AspireSkillsAsset,
                AgentAssetKind.TelemetryHooks => AgentCommandStrings.InitCommand_TelemetryHooksAsset,
                _ => throw new UnreachableException()
            };
            var format = target.Status switch
            {
                AgentConfigurationStatus.Configured when target.Asset is AgentAssetKind.AspireSkills => AgentCommandStrings.InitCommand_RegisteredTarget,
                AgentConfigurationStatus.Configured when target.Asset is AgentAssetKind.Playwright or AgentAssetKind.DotnetInspect => AgentCommandStrings.InitCommand_InstalledTarget,
                AgentConfigurationStatus.Configured => AgentCommandStrings.InitCommand_ConfiguredTarget,
                AgentConfigurationStatus.Unchanged => AgentCommandStrings.InitCommand_UnchangedTarget,
                AgentConfigurationStatus.Skipped => AgentCommandStrings.InitCommand_SkippedTarget,
                AgentConfigurationStatus.Blocked => AgentCommandStrings.InitCommand_BlockedTarget,
                AgentConfigurationStatus.Failed => AgentCommandStrings.InitCommand_FailedTarget,
                _ => throw new UnreachableException()
            };
            var message = string.Format(CultureInfo.CurrentCulture, format, asset, clients, scope, target.TargetPath);

            if (target.Status is AgentConfigurationStatus.Unchanged)
            {
                InteractionService.DisplaySubtleMessage(message);
            }
            else if (target.Status is AgentConfigurationStatus.Blocked or AgentConfigurationStatus.Failed &&
                target.Asset is not AgentAssetKind.TelemetryHooks)
            {
                InteractionService.DisplayError(message);
            }
            else
            {
                var emoji = target.Status is AgentConfigurationStatus.Configured ? KnownEmojis.CheckMarkButton : KnownEmojis.Warning;
                InteractionService.DisplayMessage(emoji, message);
            }

            if (target.Message is not null)
            {
                InteractionService.DisplaySubtleMessage(target.Message);
            }
        }

        if (result.Targets.Any(target =>
            target.Asset is AgentAssetKind.AspireSkills or AgentAssetKind.Mcp &&
            target.Status is AgentConfigurationStatus.Configured or AgentConfigurationStatus.Unchanged))
        {
            InteractionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_ClientAcquisitionNotice);
        }

        if (result.HasErrors || result.HasWarnings)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning,
                result.HasErrors ? AgentCommandStrings.ConfigurationCompletedWithErrors : AgentCommandStrings.ConfigurationCompletedWithWarnings);
        }
        else
        {
            InteractionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }
    }
}

internal sealed record AgentInitPromptBindings(
    PromptBinding<bool>? Mcp,
    PromptBinding<bool> Playwright,
    PromptBinding<bool> DotnetInspect,
    PromptBinding<bool> AspireSkills,
    PromptBinding<string?> Environments);

internal readonly record struct AgentInitExecutionResult(int ExitCode, IReadOnlyList<IAgentEnvironmentScanner> RegisteredEnvironments);

internal enum AgentConfirmation
{
    Yes,
    No
}
