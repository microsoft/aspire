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
/// Command to aggregate span duration statistics.
/// </summary>
internal sealed class TelemetrySpanStatsCommand : BaseCommand
{
    private readonly TelemetryAnalysisDataSource _dataSource;
    private readonly ILogger<TelemetrySpanStatsCommand> _logger;

    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    private static readonly Option<FileInfo?> s_fileOption = TelemetryAnalysisCommandHelpers.CreateFileOption();
    private static readonly Option<int> s_topOption = TelemetryAnalysisCommandHelpers.CreateTopOption();
    private static readonly Option<SpanStatsGroupBy> s_groupByOption = new("--group-by")
    {
        Description = TelemetryCommandStrings.GroupByOptionDescription,
        DefaultValueFactory = _ => SpanStatsGroupBy.Name
    };

    public TelemetrySpanStatsCommand(
        TelemetryAnalysisDataSource dataSource,
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ILogger<TelemetrySpanStatsCommand> logger)
        : base("span-stats", TelemetryCommandStrings.SpanStatsDescription, features, updateNotifier, executionContext, interactionService, telemetry)
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
        Options.Add(s_groupByOption);
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
        var groupBy = parseResult.GetValue(s_groupByOption);

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

        var spanStats = TelemetryAnalysisCommandHelpers.CreateSpanStats(dataResult.Data!, groupBy)
            .Take(top)
            .ToArray();

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(spanStats, TelemetryAnalysisJsonContext.RelaxedEscaping.TelemetrySpanStatsOutputArray);
            InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplaySpanStats(spanStats);
        }

        _logger.LogDebug("Displayed {SpanStatsCount} span statistic rows", spanStats.Length);
        return CommandResult.Success();
    }

    private void DisplaySpanStats(TelemetrySpanStatsOutput[] spanStats)
    {
        if (spanStats.Length == 0)
        {
            TelemetryCommandHelpers.DisplayNoData(InteractionService, "spans");
            return;
        }

        var table = new Table();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderGroup);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderCount);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderErrors);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderAverage);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderP95);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderMax);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderTotal);

        foreach (var item in spanStats)
        {
            table.AddRow(
                item.Group.EscapeMarkup(),
                item.Count.ToString(CultureInfo.InvariantCulture),
                item.ErrorCount.ToString(CultureInfo.InvariantCulture),
                TelemetryAnalysisCommandHelpers.FormatDuration(item.AverageDurationMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(item.P95DurationMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(item.MaxDurationMs),
                TelemetryAnalysisCommandHelpers.FormatDuration(item.TotalDurationMs));
        }

        InteractionService.DisplayRenderable(table);
    }
}
