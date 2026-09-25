// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// MCP command for interacting with MCP tools exposed by running resources.
/// Also provides legacy 'start' and 'init' subcommands for backward compatibility.
/// </summary>
internal sealed class McpCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public McpCommand(
        McpStartCommand startCommand,
        McpInitCommand initCommand,
        McpToolsCommand toolsCommand,
        McpCallCommand callCommand,
        CommonCommandServices services)
        : base("mcp", McpCommandStrings.Description, services)
    {
        Subcommands.Add(toolsCommand);
        Subcommands.Add(callCommand);

        // Legacy subcommands — hidden, use 'aspire agent' instead
        startCommand.Hidden = true;
        initCommand.Hidden = true;
        Subcommands.Add(startCommand);
        Subcommands.Add(initCommand);
    }
}
