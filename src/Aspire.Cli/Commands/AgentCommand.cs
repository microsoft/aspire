// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for AI agent integrations. Contains subcommands for MCP server and initialization.
/// </summary>
internal sealed class AgentCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public AgentCommand(
        AgentMcpCommand mcpCommand,
        AgentInitCommand initCommand,
        CommonCommandServices services)
        : base("agent", AgentCommandStrings.Description, services)
    {
        Subcommands.Add(mcpCommand);
        Subcommands.Add(initCommand);
    }
}
