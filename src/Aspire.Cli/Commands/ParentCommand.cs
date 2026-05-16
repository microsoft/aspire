// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Base class for commands that only contain subcommands and display help when invoked directly.
/// </summary>
internal abstract class ParentCommand : BaseCommand
{
    protected ParentCommand(string name, string description, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, IInteractionService interactionService, AspireCliTelemetry telemetry)
        : base(name, description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected sealed override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.DisplayHelp());
    }
}
