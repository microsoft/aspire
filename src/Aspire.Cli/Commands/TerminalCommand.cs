// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Attaches to an interactive terminal session for a resource.
///
/// PHASE 6 PENDING: this command is a placeholder during the WithTerminal end-to-end
/// refactor. Once the per-replica HMP v1 wiring lands (Phases 4 + 5), this command
/// will: (1) call the auxiliary backchannel's <c>GetTerminalInfoAsync</c> to discover
/// per-replica consumer UDS paths, (2) prompt the user to pick a replica when there
/// is more than one, and (3) attach via Hex1b's HMP v1 client to bridge the local
/// terminal to the chosen replica.
/// </summary>
internal sealed class TerminalCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;

    private static readonly Argument<string> s_resourceArgument = new("resource")
    {
        Description = "The name of the resource to attach a terminal to."
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption =
        new("--apphost", "--project", Resources.SharedCommandStrings.AppHostOptionDescription);

    public TerminalCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TerminalCommand> logger)
        : base("terminal", "Attach to an interactive terminal session for a resource.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _ = backchannelMonitor;
        _ = logger;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        _interactionService.DisplayMessage(
            KnownEmojis.Information,
            "The 'aspire terminal' command is being rewritten as part of the WithTerminal feature for Aspire 13.4. Try again on a build that includes the Phase 6 wire-up.");
        return Task.FromResult(ExitCodeConstants.Success);
    }
}
