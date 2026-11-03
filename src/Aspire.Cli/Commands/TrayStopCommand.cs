// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class TrayStopCommand(TrayLifecycleService tray, CommonCommandServices services)
    : BaseCommand("stop", TrayCommandStrings.StopDescription, services)
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var result = await tray.ExecuteAsync(start: false, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == CliExitCodes.Success)
        {
            InteractionService.DisplaySuccess(TrayCommandStrings.Stopped);
        }

        return result;
    }
}
