// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Replaces the default help action for the root command with grouped help output.
/// Falls back to default help for subcommands.
/// </summary>
internal sealed class GroupedHelpAction(Command rootCommand, IAnsiConsole ansiConsole) : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        // Only use grouped help for the root command; subcommands get default help.
        if (parseResult.CommandResult.Command == rootCommand)
        {
            var writer = ansiConsole.Profile.Out.Writer;
            var consoleWidth = ansiConsole.Profile.Width;
            GroupedHelpWriter.WriteHelp(rootCommand, writer, consoleWidth);
            return 0;
        }

        return new HelpAction().Invoke(parseResult);
    }
}
