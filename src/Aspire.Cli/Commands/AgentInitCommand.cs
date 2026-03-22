// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Agents;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that initializes agent environment configuration for detected agents.
/// This is the new command under 'aspire agent init'.
/// </summary>
internal sealed class AgentInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly IInteractionService _interactionService;
    private readonly IAgentEnvironmentDetector _agentEnvironmentDetector;
    private readonly IGitRepository _gitRepository;

    /// <summary>
    /// AgentInitCommand does not need template package metadata prefetching.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => false;

    /// <summary>
    /// AgentInitCommand does not need CLI package metadata prefetching.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => false;

    public AgentInitCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IGitRepository gitRepository,
        AspireCliTelemetry telemetry)
        : base("init", AgentCommandStrings.InitCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _agentEnvironmentDetector = agentEnvironmentDetector;
        _gitRepository = gitRepository;
    }

    protected override bool UpdateNotificationsEnabled => false;

    /// <summary>
    /// Public entry point for executing the init command.
    /// This allows McpInitCommand to delegate to this implementation.
    /// </summary>
    internal Task<int> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    /// <summary>
    /// Prompts the user to run agent init after a successful command, then chains into agent init if accepted.
    /// Used by commands (e.g. <c>aspire init</c>, <c>aspire new</c>) to offer agent init as a follow-up step.
    /// </summary>
    internal async Task<int> PromptAndChainAsync(
        ICliHostEnvironment hostEnvironment,
        IInteractionService interactionService,
        int previousResultExitCode,
        DirectoryInfo workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (previousResultExitCode != ExitCodeConstants.Success)
        {
            return previousResultExitCode;
        }

        if (!hostEnvironment.SupportsInteractiveInput)
        {
            return ExitCodeConstants.Success;
        }

        var runAgentInit = await interactionService.ConfirmAsync(
            SharedCommandStrings.PromptRunAgentInit,
            defaultValue: true,
            cancellationToken: cancellationToken);

        if (runAgentInit)
        {
            return await ExecuteAgentInitAsync(workspaceRoot, cancellationToken);
        }

        return ExitCodeConstants.Success;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var workspaceRoot = await PromptForWorkspaceRootAsync(cancellationToken);
        return await ExecuteAgentInitAsync(workspaceRoot, cancellationToken);
    }

    private async Task<DirectoryInfo> PromptForWorkspaceRootAsync(CancellationToken cancellationToken)
    {
        // Try to discover the git repository root to use as the default workspace root
        var gitRoot = await _gitRepository.GetRootAsync(cancellationToken);
        var defaultWorkspaceRoot = gitRoot ?? ExecutionContext.WorkingDirectory;

        // Prompt the user for the workspace root
        var workspaceRootPath = await _interactionService.PromptForFilePathAsync(
            McpCommandStrings.InitCommand_WorkspaceRootPrompt,
            defaultValue: defaultWorkspaceRoot.FullName,
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

    private async Task<int> ExecuteAgentInitAsync(DirectoryInfo workspaceRoot, CancellationToken cancellationToken)
    {
        var context = new AgentEnvironmentScanContext
        {
            WorkingDirectory = ExecutionContext.WorkingDirectory,
            RepositoryRoot = workspaceRoot
        };

        var applicators = await _interactionService.ShowStatusAsync(
            McpCommandStrings.InitCommand_DetectingAgentEnvironments,
            async () => await _agentEnvironmentDetector.DetectAsync(context, cancellationToken));

        if (applicators.Length == 0)
        {
            _interactionService.DisplaySubtleMessage(McpCommandStrings.InitCommand_NoAgentEnvironmentsDetected);
            return ExitCodeConstants.Success;
        }

        // Apply deprecated config migrations silently (these are fixes, not choices)
        var configUpdates = applicators.Where(a => a.PromptGroup == McpInitPromptGroup.ConfigUpdates).ToList();
        var userChoices = applicators.Where(a => a.PromptGroup != McpInitPromptGroup.ConfigUpdates).ToList();

        foreach (var update in configUpdates)
        {
            try
            {
                await update.ApplyAsync(cancellationToken);
                _interactionService.DisplayMessage(KnownEmojis.Wrench, update.Description);
            }
            catch (InvalidOperationException ex)
            {
                _interactionService.DisplayError(ex.Message);
            }
        }

        if (userChoices.Count == 0)
        {
            _interactionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
            return ExitCodeConstants.Success;
        }

        // Categorize by prompt group (not string matching)
        var skillApplicators = userChoices
            .Where(a => a.PromptGroup == McpInitPromptGroup.SkillFiles)
            .ToList();
        var mcpApplicators = userChoices
            .Where(a => a.PromptGroup == McpInitPromptGroup.AgentEnvironments)
            .ToList();
        var toolApplicators = userChoices
            .Where(a => a.PromptGroup == McpInitPromptGroup.Tools)
            .ToList();
        var otherApplicators = userChoices
            .Except(skillApplicators)
            .Except(mcpApplicators)
            .Except(toolApplicators)
            .ToList();

        // Build a flat list: collapsed skill (pre-selected), then others (Playwright CLI, etc.)
        var promptChoices = new List<AgentEnvironmentApplicator>();

        // Collapse all skill applicators into a single line (pre-selected)
        AgentEnvironmentApplicator? combinedSkillApplicator = null;
        if (skillApplicators.Count > 0)
        {
            combinedSkillApplicator = new AgentEnvironmentApplicator(
                AgentCommandStrings.InitCommand_InstallSkillFile,
                async ct =>
                {
                    foreach (var skill in skillApplicators)
                    {
                        await skill.ApplyAsync(ct);
                        _interactionService.DisplayMessage(KnownEmojis.CheckMark, skill.Description);
                    }
                },
                promptGroup: McpInitPromptGroup.AdditionalOptions);
            promptChoices.Add(combinedSkillApplicator);
        }

        promptChoices.AddRange(toolApplicators);
        promptChoices.AddRange(otherApplicators);

        // Add collapsed MCP as last option for compatibility
        if (mcpApplicators.Count > 0)
        {
            var combinedMcpApplicator = new AgentEnvironmentApplicator(
                AgentCommandStrings.InitCommand_ConfigureMcpServer,
                async ct =>
                {
                    foreach (var mcp in mcpApplicators)
                    {
                        await mcp.ApplyAsync(ct);
                        _interactionService.DisplayMessage(KnownEmojis.CheckMark, mcp.Description);
                    }
                },
                promptGroup: McpInitPromptGroup.AdditionalOptions);
            promptChoices.Add(combinedMcpApplicator);
        }

        // Pre-select the skill applicator
        var preSelected = combinedSkillApplicator is not null ? [combinedSkillApplicator] : Array.Empty<AgentEnvironmentApplicator>();

        // Present a single flat prompt with skill pre-selected
        var selected = await _interactionService.PromptForSelectionsAsync(
            AgentCommandStrings.InitCommand_WhatToConfigure,
            promptChoices,
            applicator => applicator.Description,
            preSelected: preSelected,
            optional: true,
            cancellationToken);

        if (selected.Count == 0)
        {
            _interactionService.DisplaySubtleMessage(AgentCommandStrings.InitCommand_NothingSelected);
            return ExitCodeConstants.Success;
        }

        // Apply selected applicators
        var hasErrors = false;
        foreach (var applicator in selected)
        {
            try
            {
                await applicator.ApplyAsync(cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _interactionService.DisplayError(ex.Message);
                if (ex.InnerException is JsonException)
                {
                    _interactionService.DisplaySubtleMessage(
                        string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.SkippedMalformedConfigFile, applicator.Description));
                }
                hasErrors = true;
            }
        }

        if (hasErrors)
        {
            _interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.ConfigurationCompletedWithErrors);
        }
        else
        {
            _interactionService.DisplaySuccess(McpCommandStrings.InitCommand_ConfigurationComplete);
        }

        return hasErrors ? ExitCodeConstants.InvalidCommand : ExitCodeConstants.Success;
    }
}
