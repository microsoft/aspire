// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Manages the experimental bundled macOS and Windows tray companion.
/// </summary>
internal sealed class TrayCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    public TrayCommand(TrayStartCommand startCommand, TrayStopCommand stopCommand, CommonCommandServices services)
        : base("tray", TrayCommandStrings.Description, services)
    {
        Subcommands.Add(startCommand);
        Subcommands.Add(stopCommand);
    }
}
