// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;

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
        CommonCommandServices services)
        : base("otel", TelemetryCommandStrings.Description, services)
    {
        ArgumentNullException.ThrowIfNull(logsCommand);
        ArgumentNullException.ThrowIfNull(spansCommand);
        ArgumentNullException.ThrowIfNull(tracesCommand);

        Subcommands.Add(logsCommand);
        Subcommands.Add(spansCommand);
        Subcommands.Add(tracesCommand);
    }
}
