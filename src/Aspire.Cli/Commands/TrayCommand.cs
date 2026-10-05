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

    public TrayCommand(TrayStartCommand startCommand, TrayStopCommand stopCommand, IEnvironment environment, CommonCommandServices services)
        : base("tray", TrayCommandStrings.Description, services)
    {
        // The tray companion only ships for macOS and Windows, so keep it out of help elsewhere.
        // TrayLifecycleService still rejects unsupported platforms because hidden commands remain invocable.
        Hidden = !environment.IsMacOS() && !environment.IsWindows();

        Subcommands.Add(startCommand);
        Subcommands.Add(stopCommand);
    }
}
