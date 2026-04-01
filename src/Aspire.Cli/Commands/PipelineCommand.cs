// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Pipeline command group for managing CI/CD pipeline generation.
/// </summary>
internal sealed class PipelineCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Deployment;

    public PipelineCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        PipelineInitCommand initCommand,
        AspireCliTelemetry telemetry)
        : base("pipeline", PipelineCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(initCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }
}
