// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Legacy command 'aspire mcp start' that delegates to the new AgentMcpCommand.
/// This is kept for backward compatibility but is hidden from help.
/// </summary>
internal sealed class McpStartCommand : BaseCommand
{
    private readonly AgentMcpCommand _agentMcpCommand;

    public McpStartCommand(
        AgentMcpCommand agentMcpCommand,
        CommonCommandServices services)
        : base("start", McpCommandStrings.StartCommand_Description, services)
    {
        // Use the injected AgentMcpCommand to delegate execution to
        _agentMcpCommand = agentMcpCommand;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Display deprecation warning to stderr (all MCP logging goes to stderr)
        InteractionService.DisplayMarkupLine($"[yellow]⚠ {McpCommandStrings.DeprecatedCommandWarning}[/]");

        // Delegate to the new AgentMcpCommand
        return await _agentMcpCommand.ExecuteCommandAsync(parseResult, cancellationToken);
    }
}
