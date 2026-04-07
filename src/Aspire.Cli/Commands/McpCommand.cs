// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// MCP command for interacting with MCP tools exposed by running resources.
/// Also provides legacy 'start' and 'init' subcommands for backward compatibility.
/// </summary>
internal sealed class McpCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public McpCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        McpStartCommand startCommand,
        McpInitCommand initCommand,
        McpToolsCommand toolsCommand,
        McpCallCommand callCommand,
        AspireCliTelemetry telemetry)
        : base("mcp", McpCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(toolsCommand);
        Subcommands.Add(callCommand);

        // Legacy subcommands — hidden, use 'aspire agent' instead
        startCommand.Hidden = true;
        initCommand.Hidden = true;
        Subcommands.Add(startCommand);
        Subcommands.Add(initCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
