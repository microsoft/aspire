// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Legacy command 'aspire mcp init' that delegates to the new AgentInitCommand.
/// This is kept for backward compatibility but is hidden from help.
/// </summary>
internal sealed class McpInitCommand : BaseCommand
{
    private readonly AgentInitCommand _agentInitCommand;

    public McpInitCommand(
        AgentInitCommand agentInitCommand,
        CommonCommandServices services)
        : base("init", McpCommandStrings.InitCommand_Description, services)
    {
        _agentInitCommand = agentInitCommand;
        AgentInitCommand.AddOptions(this, includeMcp: true, includeWorkspaceRoot: true);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Display deprecation warning
        InteractionService.DisplayMarkupLine($"[yellow]⚠ {McpCommandStrings.DeprecatedCommandWarning}[/]");
        InteractionService.DisplayEmptyLine();

        // Delegate to the new AgentInitCommand
        return await _agentInitCommand.ExecuteCommandAsync(parseResult, cancellationToken);
    }
}
