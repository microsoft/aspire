// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Agents;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.NuGet;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Legacy command 'aspire mcp init' that delegates to the new AgentInitCommand.
/// This is kept for backward compatibility but is hidden from help.
/// </summary>
internal sealed class McpInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly AgentInitCommand _agentInitCommand;

    /// <summary>
    /// McpInitCommand does not need template package metadata prefetching.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => false;

    /// <summary>
    /// McpInitCommand does not need CLI package metadata prefetching.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => false;

    public McpInitCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IGitRepository gitRepository,
        AspireCliTelemetry telemetry)
        : base("init", McpCommandStrings.InitCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        // Create the AgentInitCommand to delegate execution to
        _agentInitCommand = new AgentInitCommand(
            interactionService,
            features,
            updateNotifier,
            executionContext,
            agentEnvironmentDetector,
            gitRepository,
            telemetry);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Display deprecation warning
        InteractionService.DisplayMarkupLine($"[yellow]⚠ {McpCommandStrings.DeprecatedCommandWarning}[/]");
        InteractionService.DisplayEmptyLine();
        
        // Delegate to the new AgentInitCommand
        return _agentInitCommand.ExecuteCommandAsync(parseResult, cancellationToken);
    }
}
