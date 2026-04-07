// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands.Sdk;

/// <summary>
/// Parent command for SDK-related operations.
/// Usage: aspire sdk [subcommand]
/// </summary>
internal sealed class SdkCommand : BaseCommand
{
    public SdkCommand(
        SdkGenerateCommand generateCommand,
        SdkDumpCommand dumpCommand,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AspireCliTelemetry telemetry)
        : base("sdk", "Commands for generating SDKs for building Aspire integrations in other languages.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Hidden = true;
        Subcommands.Add(generateCommand);
        Subcommands.Add(dumpCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // When invoked without a subcommand, show help
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
