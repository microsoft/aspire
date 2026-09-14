// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for dashboard operations. Contains subcommands for running the dashboard.
/// </summary>
internal sealed class DashboardCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    public DashboardCommand(
        DashboardRunCommand runCommand,
        CommonCommandServices services)
        : base("dashboard", DashboardCommandStrings.Description, services)
    {
        ArgumentNullException.ThrowIfNull(runCommand);

        Subcommands.Add(runCommand);
    }
}
