// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

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
        CommonCommandServices services)
        : base("terminal", "Manage interactive terminal sessions for resources.", services)
    {
        ArgumentNullException.ThrowIfNull(attachCommand);
        ArgumentNullException.ThrowIfNull(psCommand);

        Subcommands.Add(attachCommand);
        Subcommands.Add(psCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.DisplayHelp());
    }
}
