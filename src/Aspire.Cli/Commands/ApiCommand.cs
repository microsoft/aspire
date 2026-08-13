// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for API documentation operations under <c>docs</c>.
/// </summary>
internal sealed class ApiCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiCommand"/> class.
    /// </summary>
    /// <param name="listCommand">The scoped browse command.</param>
    /// <param name="searchCommand">The search command.</param>
    /// <param name="getCommand">The content retrieval command.</param>
    /// <param name="services">Common command services.</param>
    public ApiCommand(
        ApiListCommand listCommand,
        ApiSearchCommand searchCommand,
        ApiGetCommand getCommand,
        CommonCommandServices services)
        : base("api", ApiCommandStrings.Description, services)
    {
        Subcommands.Add(listCommand);
        Subcommands.Add(searchCommand);
        Subcommands.Add(getCommand);
    }
}
