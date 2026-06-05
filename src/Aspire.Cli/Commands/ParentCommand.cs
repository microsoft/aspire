// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace Aspire.Cli.Commands;

/// <summary>
/// Base class for commands that only contain subcommands and display help when invoked directly.
/// </summary>
internal abstract class ParentCommand : BaseCommand
{
    protected ParentCommand(string name, string description, CommonCommandServices services)
        : base(name, description, services)
    {
    }

    protected sealed override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.DisplayHelp());
    }
}
