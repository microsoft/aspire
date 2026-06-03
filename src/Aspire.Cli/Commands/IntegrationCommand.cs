// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class IntegrationCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    public IntegrationCommand(
        AddCommand addCommand,
        IntegrationListCommand listCommand,
        IntegrationSearchCommand searchCommand,
        CommonCommandServices services)
        : base("integration", AddCommandStrings.IntegrationCommandDescription, services)
    {
        Subcommands.Add(addCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
    }
}
