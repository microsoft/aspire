// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for terminal operations on resources registered with <c>WithTerminal()</c>.
/// Contains subcommands for attaching to interactive terminal sessions.
/// </summary>
internal sealed class TerminalCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    public TerminalCommand(
        TerminalAttachCommand attachCommand,
        TerminalPsCommand psCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("terminal", "Manage interactive terminal sessions for resources.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        ArgumentNullException.ThrowIfNull(attachCommand);
        ArgumentNullException.ThrowIfNull(psCommand);

        Subcommands.Add(attachCommand);
        Subcommands.Add(psCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
