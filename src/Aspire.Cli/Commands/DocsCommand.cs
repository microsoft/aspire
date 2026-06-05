// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for documentation operations. Contains subcommands for listing, searching, and getting docs, including API reference content.
/// </summary>
internal sealed class DocsCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public DocsCommand(
        ApiCommand apiCommand,
        DocsListCommand listCommand,
        DocsSearchCommand searchCommand,
        DocsGetCommand getCommand,
        CommonCommandServices services)
        : base("docs", DocsCommandStrings.Description, services)
    {
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getCommand);
        Subcommands.Add(apiCommand);
    }
}
