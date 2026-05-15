// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Parent command for telemetry operations. Contains subcommands for viewing logs, spans, and traces.
/// </summary>
internal sealed class TelemetryCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    public TelemetryCommand(
        TelemetryLogsCommand logsCommand,
        TelemetrySpansCommand spansCommand,
        TelemetryTracesCommand tracesCommand,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry)
        : base("otel", TelemetryCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        ArgumentNullException.ThrowIfNull(logsCommand);
        ArgumentNullException.ThrowIfNull(spansCommand);
        ArgumentNullException.ThrowIfNull(tracesCommand);

        Subcommands.Add(logsCommand);
        Subcommands.Add(spansCommand);
        Subcommands.Add(tracesCommand);
    }
}
