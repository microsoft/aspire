// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class TerminalTapeCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    public TerminalTapeCommand(TerminalTapePlayCommand playCommand, CommonCommandServices services)
        : base("tape", TerminalCommandStrings.TapeDescription, services)
    {
        Subcommands.Add(playCommand);
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        => Task.FromResult(CommandResult.DisplayHelp());
}
