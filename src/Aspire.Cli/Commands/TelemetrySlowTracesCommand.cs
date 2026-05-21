// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to show the slowest traces.
/// </summary>
internal sealed class TelemetrySlowTracesCommand : BaseCommand
{
    private readonly TelemetryAnalysisDataSource _dataSource;
    private readonly ILogger<TelemetrySlowTracesCommand> _logger;

    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    private static readonly Option<FileInfo?> s_fileOption = TelemetryAnalysisCommandHelpers.CreateFileOption();
    private static readonly Option<int> s_topOption = TelemetryAnalysisCommandHelpers.CreateTopOption();

    public TelemetrySlowTracesCommand(
        TelemetryAnalysisDataSource dataSource,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TelemetrySlowTracesCommand> logger)
        : base("top-traces", TelemetryCommandStrings.SlowTracesDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _dataSource = dataSource;
        _logger = logger;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
        Options.Add(s_fileOption);
        Options.Add(s_topOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var appHost = parseResult.GetValue(s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);
        var dashboardUrl = parseResult.GetValue(s_dashboardUrlOption);
        var apiKey = parseResult.GetValue(s_apiKeyOption);
        var file = parseResult.GetValue(s_fileOption);
        var top = parseResult.GetValue(s_topOption);

        if (TelemetryAnalysisCommandHelpers.ValidateInputOptions(file, appHost, dashboardUrl, apiKey) is { } validationResult)
        {
            return validationResult;
        }
        if (TelemetryAnalysisCommandHelpers.ValidateTop(top) is { } topValidationResult)
        {
            return topValidationResult;
        }

        var dataResult = await _dataSource.GetTracesAsync(appHost, dashboardUrl, apiKey, file, resourceName, cancellationToken).ConfigureAwait(false);
        if (!dataResult.Success)
        {
            return CommandResult.Failure(dataResult.ExitCode, dataResult.ErrorMessage);
        }

        var data = dataResult.Data!;
        var slowTraces = TelemetryAnalysisCommandHelpers.GetTraceInfos(data)
            .OrderByDescending(t => t.Duration)
            .Take(top)
            .ToArray();
        var output = TelemetryAnalysisCommandHelpers.CreateSlowTraceOutputs(data, slowTraces);

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(output, TelemetryAnalysisJsonContext.RelaxedEscaping.TelemetrySlowTraceOutputArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplaySlowTraces(slowTraces, data.DashboardUrl);
        }

        _logger.LogDebug("Displayed {TraceCount} slow traces", slowTraces.Length);
        return CommandResult.Success();
    }

    private void DisplaySlowTraces(TelemetryTraceInfo[] slowTraces, string? dashboardUrl)
    {
        if (slowTraces.Length == 0)
        {
            TelemetryCommandHelpers.DisplayNoData(InteractionService, "traces");
            return;
        }

        var table = new Table();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderDuration);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderSpans);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderStatus);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderResource);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderName);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderTraceId);

        foreach (var trace in slowTraces)
        {
            var status = trace.HasError ? "[red]ERR[/]" : "[green]OK[/]";
            var traceId = TelemetryCommandHelpers.FormatTraceLink(
                InteractionService,
                dashboardUrl,
                trace.TraceId,
                OtlpHelpers.ToShortenedId(trace.TraceId));

            table.AddRow(
                TelemetryCommandHelpers.FormatDuration(trace.Duration),
                trace.SpanCount.ToString(CultureInfo.InvariantCulture),
                status,
                trace.ResourceName.EscapeMarkup(),
                trace.Name.EscapeMarkup(),
                traceId);
        }

        InteractionService.DisplayRenderable(table);
    }
}
