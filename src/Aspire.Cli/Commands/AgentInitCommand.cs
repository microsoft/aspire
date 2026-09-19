// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Completions;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Selects independent assets and clients before applying agent configuration.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand
{
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly AgentClientCatalog _clientCatalog;
    private readonly IAgentInitService _agentInitService;
    private readonly IGitRepository _gitRepository;

    private static readonly Option<string?> s_workspaceRootOption = new("--workspace-root")
    {
        Description = AgentCommandStrings.InitCommand_WorkspaceRootOptionDescription
    };

    internal static readonly Option<AgentConfirmation?> s_mcpOption = CreateAssetOption("--mcp", AgentCommandStrings.InitCommand_McpOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_playwrightOption = CreateAssetOption("--playwright", AgentInitStrings.PlaywrightOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_dotnetInspectOption = CreateAssetOption("--dotnet-inspect", AgentInitStrings.DotnetInspectOptionDescription);
    internal static readonly Option<AgentConfirmation?> s_aspireSkillsOption = CreateAssetOption("--aspire-skills", AgentInitStrings.AspireSkillsOptionDescription);

    internal static readonly Option<string?> s_clientsOption = CreateClientsOption();

    public AgentInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        AgentClientCatalog clientCatalog,
        IAgentInitService agentInitService,
        IGitRepository gitRepository,
        CommonCommandServices services)
        : base("init", AgentCommandStrings.InitCommand_Description, services)
    {
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _clientCatalog = clientCatalog;
        _agentInitService = agentInitService;
        _gitRepository = gitRepository;

        AddOptions(this, includeMcp: true, includeWorkspaceRoot: true);
    }

    internal static void AddOptions(Command command, bool includeMcp, bool includeWorkspaceRoot)
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
        command.Options.Add(s_clientsOption);
    }

    internal static AgentInitPromptBindings CreateBindings(ParseResult parseResult, bool includeMcp) => new(
        includeMcp ? CreateAssetBinding(parseResult, s_mcpOption, defaultValue: false) : null,
        CreateAssetBinding(parseResult, s_playwrightOption, defaultValue: false),
        CreateAssetBinding(parseResult, s_dotnetInspectOption, defaultValue: false),
        CreateAssetBinding(parseResult, s_aspireSkillsOption, defaultValue: true),
        PromptBinding.Create(parseResult, s_clientsOption));

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

                result.AddError(string.Format(CultureInfo.CurrentCulture, AgentInitStrings.InvalidAssetValue, name, value));
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

    private static Option<string?> CreateClientsOption()
    {
        var clientIds = new AgentClientCatalog().Clients.Select(static client => client.Id).ToArray();
        var supportedClients = string.Join(",", clientIds);

        return new Option<string?>("--clients")
        {
            Description = string.Format(CultureInfo.InvariantCulture, AgentInitStrings.ClientsOptionDescription,
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
                    result.AddError(string.Format(CultureInfo.CurrentCulture, AgentInitStrings.InvalidClients,
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
            AgentInitStrings.ConfigurePlaywrightPrompt,
            binding: bindings.Playwright,
            cancellationToken: cancellationToken);
        var dotnetInspect = await InteractionService.PromptConfirmAsync(
            AgentInitStrings.ConfigureDotnetInspectPrompt,
            binding: bindings.DotnetInspect,
            cancellationToken: cancellationToken);
        var aspireSkills = await InteractionService.PromptConfirmAsync(
            AgentInitStrings.ConfigureAspireSkillsPrompt,
            binding: bindings.AspireSkills,
            cancellationToken: cancellationToken);
        var assets = new AgentAssetSelection(mcp, playwright, dotnetInspect, aspireSkills);

        if (!assets.HasAssets)
        {
            InteractionService.DisplaySubtleMessage(AgentInitStrings.NoAssetsSelected);
            return new(CliExitCodes.Success, []);
        }

        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };
        var detections = await InteractionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            () => _agentEnvironmentDetector.DetectAsync(context, cancellationToken),
            emoji: KnownEmojis.Robot);

        var detectedClients = detections.Select(static detection => detection.Client).ToHashSet();
        var defaults = _clientCatalog.Clients.Where(client => detectedClients.Contains(client.Kind)).ToArray();
        // No default is intentionally different from "none": unattended setup must ask
        // for --clients when detection cannot supply a choice.
        var clientsBinding = defaults.Length == 0
            ? bindings.Clients
            : bindings.Clients.WithDefault(string.Join(",", defaults.Select(static client => client.Id)));
        var clients = await InteractionService.PromptForSelectionsAsync(
            AgentInitStrings.SelectClients,
            _clientCatalog.Clients,
            static client => client.DisplayName,
            preSelected: defaults,
            optional: true,
            binding: clientsBinding,
            cancellationToken: cancellationToken);

        if (clients.Count == 0)
        {
            InteractionService.DisplaySubtleMessage(AgentInitStrings.NoClientsSelected);
            return new(CliExitCodes.Success, []);
        }

        using var activity = Telemetry.StartReportedActivity("AgentInit.Configure");
        activity?.SetTag("aspire.agent.clients", string.Join(",", clients.Select(static client => client.Id)));
        activity?.SetTag("aspire.agent.mcp", mcp);
        activity?.SetTag("aspire.agent.playwright", playwright);
        activity?.SetTag("aspire.agent.dotnet_inspect", dotnetInspect);
        activity?.SetTag("aspire.agent.aspire_skills", aspireSkills);

        var result = await _agentInitService.ConfigureAsync(
            new AgentInitRequest(workspaceRoot, assets, clients.Select(static client => client.Kind).Distinct().ToArray(), detections),
            cancellationToken);

        DisplayResults(result);
        activity?.SetTag("aspire.agent.configuration_failed", result.HasErrors);
        activity?.SetTag("aspire.agent.configuration_warnings", result.HasWarnings);
        if (result.HasErrors)
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        return new(result.HasErrors ? CliExitCodes.InvalidCommand : CliExitCodes.Success, result.RegisteredClients);
    }

    private void DisplayResults(AgentInitResult result)
    {
        foreach (var target in result.Targets)
        {
            var clients = string.Join(", ", target.Clients.Select(client => _clientCatalog.Get(client).DisplayName));
            var scope = target.Scope is AgentConfigurationScope.Project ? AgentInitStrings.ProjectScope : AgentInitStrings.UserScope;
            var asset = target.Asset switch
            {
                AgentAssetKind.Mcp => AgentInitStrings.McpAsset,
                AgentAssetKind.Playwright => AgentInitStrings.PlaywrightAsset,
                AgentAssetKind.DotnetInspect => AgentInitStrings.DotnetInspectAsset,
                AgentAssetKind.AspireSkills => AgentInitStrings.AspireSkillsAsset,
                AgentAssetKind.TelemetryHooks => AgentInitStrings.TelemetryHooksAsset,
                _ => throw new UnreachableException()
            };
            var format = target.Status switch
            {
                AgentConfigurationStatus.Configured when target.Asset is AgentAssetKind.AspireSkills => AgentInitStrings.RegisteredTarget,
                AgentConfigurationStatus.Configured when target.Asset is AgentAssetKind.Playwright or AgentAssetKind.DotnetInspect => AgentInitStrings.InstalledTarget,
                AgentConfigurationStatus.Configured => AgentInitStrings.ConfiguredTarget,
                AgentConfigurationStatus.Unchanged => AgentInitStrings.UnchangedTarget,
                AgentConfigurationStatus.Skipped => AgentInitStrings.SkippedTarget,
                AgentConfigurationStatus.Blocked => AgentInitStrings.BlockedTarget,
                AgentConfigurationStatus.Failed => AgentInitStrings.FailedTarget,
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

        if (result.RegisteredClients.Count > 0)
        {
            InteractionService.DisplaySubtleMessage(AgentInitStrings.ClientAcquisitionNotice);
        }

        if (result.HasErrors || result.HasWarnings)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning,
                result.HasErrors ? AgentCommandStrings.ConfigurationCompletedWithErrors : AgentInitStrings.ConfigurationCompletedWithWarnings);
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
    PromptBinding<string?> Clients);

internal readonly record struct AgentInitExecutionResult(int ExitCode, IReadOnlyList<AgentClientKind> RegisteredClients);

internal enum AgentConfirmation
{
    Yes,
    No
}
