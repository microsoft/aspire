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
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to summarize trace latency, span counts, and errors.
/// </summary>
internal sealed class TelemetrySummaryCommand : BaseCommand
{
    private readonly TelemetryAnalysisDataSource _dataSource;
    private readonly ILogger<TelemetrySummaryCommand> _logger;

    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    private static readonly Option<FileInfo?> s_fileOption = TelemetryAnalysisCommandHelpers.CreateFileOption();

    public TelemetrySummaryCommand(
        TelemetryAnalysisDataSource dataSource,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TelemetrySummaryCommand> logger)
        : base("summary", TelemetryCommandStrings.SummaryDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _dataSource = dataSource;
        _logger = logger;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
        Options.Add(s_fileOption);
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

        if (TelemetryAnalysisCommandHelpers.ValidateInputOptions(file, appHost, dashboardUrl, apiKey) is { } validationResult)
        {
            return validationResult;
        }

        var dataResult = await _dataSource.GetTracesAsync(appHost, dashboardUrl, apiKey, file, resourceName, cancellationToken).ConfigureAwait(false);
        if (!dataResult.Success)
        {
            return CommandResult.Failure(dataResult.ExitCode, dataResult.ErrorMessage);
        }

        var data = dataResult.Data!;
        var traces = TelemetryAnalysisCommandHelpers.GetTraceInfos(data);
        var summary = TelemetryAnalysisCommandHelpers.CreateSummary(data, traces);

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(summary, TelemetryAnalysisJsonContext.RelaxedEscaping.TelemetrySummaryOutput);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplaySummary(summary);
        }

        _logger.LogDebug("Displayed telemetry summary for {TraceCount} traces", summary.TraceCount);
        return CommandResult.Success();
    }

    private void DisplaySummary(TelemetrySummaryOutput summary)
    {
        var table = new Table();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderMetric);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderValue);

        table.AddRow(TelemetryCommandStrings.MetricResources, summary.ResourceCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow(TelemetryCommandStrings.MetricTraces, summary.TraceCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow(TelemetryCommandStrings.MetricSpans, summary.SpanCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow(TelemetryCommandStrings.MetricErrorTraces, summary.ErrorTraceCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow(TelemetryCommandStrings.MetricErrorSpans, summary.ErrorSpanCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow(TelemetryCommandStrings.MetricAverageDuration, TelemetryAnalysisCommandHelpers.FormatDuration(summary.AverageDurationMs));
        table.AddRow(TelemetryCommandStrings.MetricP50Duration, TelemetryAnalysisCommandHelpers.FormatDuration(summary.P50DurationMs));
        table.AddRow(TelemetryCommandStrings.MetricP95Duration, TelemetryAnalysisCommandHelpers.FormatDuration(summary.P95DurationMs));
        table.AddRow(TelemetryCommandStrings.MetricP99Duration, TelemetryAnalysisCommandHelpers.FormatDuration(summary.P99DurationMs));
        table.AddRow(TelemetryCommandStrings.MetricMaxDuration, TelemetryAnalysisCommandHelpers.FormatDuration(summary.MaxDurationMs));

        InteractionService.DisplayRenderable(table);
    }
}
