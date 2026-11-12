// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

internal sealed class TrayStartCommand(TrayLifecycleService tray, CommonCommandServices services)
    : BaseCommand("start", TrayCommandStrings.StartDescription, services)
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    // The first termination signal must leave enough time for the helper to finish the
    // GUI's lease handoff instead of letting BaseCommand abandon the in-flight handler.
    protected override TimeSpan GracefulShutdownBudget => TrayLifecycleService.HelperTimeout;

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var result = await tray.ExecuteAsync(start: true, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode == CliExitCodes.Success)
        {
            InteractionService.DisplaySuccess(TrayCommandStrings.Started);
        }

        return result;
    }
}
