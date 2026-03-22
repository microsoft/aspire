// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Legacy command 'aspire mcp start' that delegates to the new AgentMcpCommand.
/// This is kept for backward compatibility but is hidden from help.
/// </summary>
internal sealed class McpStartCommand : BaseCommand
{
    private readonly AgentMcpCommand _agentMcpCommand;

    public McpStartCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AgentMcpCommand agentMcpCommand,
        AspireCliTelemetry telemetry)
        : base("start", McpCommandStrings.StartCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        // Use the injected AgentMcpCommand to delegate execution to
        _agentMcpCommand = agentMcpCommand;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Display deprecation warning to stderr (all MCP logging goes to stderr)
        InteractionService.DisplayMarkupLine($"[yellow]⚠ {McpCommandStrings.DeprecatedCommandWarning}[/]");

        // Delegate to the new AgentMcpCommand
        return _agentMcpCommand.ExecuteCommandAsync(parseResult, cancellationToken);
    }
}
