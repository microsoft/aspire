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
/// Command to analyze trace wall-clock time, span coverage, and gaps.
/// </summary>
internal sealed class TelemetryWallTimeCommand : BaseCommand
{
    private readonly TelemetryAnalysisDataSource _dataSource;
    private readonly ILogger<TelemetryWallTimeCommand> _logger;

    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    private static readonly Option<FileInfo?> s_fileOption = TelemetryAnalysisCommandHelpers.CreateFileOption();
    private static readonly Option<int> s_topOption = TelemetryAnalysisCommandHelpers.CreateTopOption();

    public TelemetryWallTimeCommand(
        TelemetryAnalysisDataSource dataSource,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TelemetryWallTimeCommand> logger)
        : base("wall-time", TelemetryCommandStrings.WallTimeDescription, features, updateNotifier, executionContext, interactionService, telemetry)
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
        var wallTime = TelemetryAnalysisCommandHelpers.CreateWallTimeOutputs(data)
            .Take(top)
            .ToArray();

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(wallTime, TelemetryAnalysisJsonContext.RelaxedEscaping.TelemetryWallTimeOutputArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplayWallTime(wallTime, data.DashboardUrl);
        }

        _logger.LogDebug("Displayed {WallTimeCount} wall-clock trace rows", wallTime.Length);
        return CommandResult.Success();
    }

    private void DisplayWallTime(TelemetryWallTimeOutput[] wallTime, string? dashboardUrl)
    {
        if (wallTime.Length == 0)
        {
            TelemetryCommandHelpers.DisplayNoData(InteractionService, "traces");
            return;
        }

        var table = new Table();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderWallClock);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderSpanSum);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderCovered);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderGap);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderOverlap);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderRatio);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderSpans);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderStatus);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderResource);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderName);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderTraceId);

        foreach (var trace in wallTime)
        {
            var status = trace.HasError ? "[red]ERR[/]" : "[green]OK[/]";
            var traceId = TelemetryCommandHelpers.FormatTraceLink(
                InteractionService,
                dashboardUrl,
                trace.TraceId,
                OtlpHelpers.ToShortenedId(trace.TraceId));

            table.AddRow(
                TelemetryAnalysisCommandHelpers.FormatDuration(trace.WallClockMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(trace.SpanSumMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(trace.CoveredMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(trace.GapMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(trace.OverlapMs),
                FormatRatio(trace.SpanSumToWallRatio),
                trace.SpanCount.ToString(CultureInfo.InvariantCulture),
                status,
                trace.Resource.EscapeMarkup(),
                trace.Name.EscapeMarkup(),
                traceId);
        }

        InteractionService.DisplayRenderable(table);
    }

    private static string FormatRatio(double ratio)
    {
        return ratio.ToString("0.###", CultureInfo.InvariantCulture) + "x";
    }
}
