// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Manages AppHost user secrets (set, get, list, delete).
/// </summary>
internal sealed class SecretCommand : ParentCommand
{
    internal static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public SecretCommand(
        SecretSetCommand setCommand,
        SecretGetCommand getCommand,
        SecretListCommand listCommand,
        SecretPathCommand pathCommand,
        SecretDeleteCommand deleteCommand,
        CommonCommandServices services)
        : base("secret", SecretCommandStrings.Description, services)
    {
        Subcommands.Add(getCommand);
        Subcommands.Add(setCommand);
        Subcommands.Add(listCommand);
        Subcommands.Add(pathCommand);
        Subcommands.Add(deleteCommand);
    }
}
