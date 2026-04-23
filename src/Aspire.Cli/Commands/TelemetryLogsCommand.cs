// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Shared.ConsoleLogs;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to view structured logs from the Dashboard telemetry API.
/// </summary>
internal sealed class TelemetryLogsCommand : BaseCommand
{
    private readonly IInteractionService _interactionService;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TelemetryLogsCommand> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResourceColorMap _resourceColorMap;
    private readonly TimeProvider _timeProvider;

    // Shared options from TelemetryCommandHelpers
    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<bool> s_followOption = TelemetryCommandHelpers.CreateFollowOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<int?> s_limitOption = TelemetryCommandHelpers.CreateLimitOption();
    private static readonly Option<string?> s_traceIdOption = TelemetryCommandHelpers.CreateTraceIdOption("--trace-id");
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();
    // Logs-specific option
    private static readonly Option<string?> s_severityOption = new("--severity")
    {
        Description = TelemetryCommandStrings.SeverityOptionDescription
    };

    public TelemetryLogsCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ICliHostEnvironment hostEnvironment,
        IHttpClientFactory httpClientFactory,
        ResourceColorMap resourceColorMap,
        TimeProvider timeProvider,
        ILogger<TelemetryLogsCommand> logger)
        : base("logs", TelemetryCommandStrings.LogsDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _hostEnvironment = hostEnvironment;
        _httpClientFactory = httpClientFactory;
        _resourceColorMap = resourceColorMap;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_followOption);
        Options.Add(s_formatOption);
        Options.Add(s_limitOption);
        Options.Add(s_traceIdOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
        Options.Add(s_severityOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var follow = parseResult.GetValue(s_followOption);
        var format = parseResult.GetValue(s_formatOption);
        var limit = parseResult.GetValue(s_limitOption);
        var traceId = parseResult.GetValue(s_traceIdOption);
        var severity = parseResult.GetValue(s_severityOption);
        var dashboardUrl = parseResult.GetValue(s_dashboardUrlOption);
        var apiKey = parseResult.GetValue(s_apiKeyOption);

        // Validate --limit value
        if (limit.HasValue && limit.Value < 1)
        {
            _interactionService.DisplayError(TelemetryCommandStrings.LimitMustBePositive);
            return ExitCodeConstants.InvalidCommand;
        }

        var dashboardApi = await TelemetryCommandHelpers.GetDashboardApiAsync(
            _connectionResolver, _interactionService, _httpClientFactory, _logger, passedAppHostProjectFile, dashboardUrl, apiKey, requireDashboard: true, ExecutionContext.LogFilePath, cancellationToken);

        if (!dashboardApi.Success)
        {
            return dashboardApi.ExitCode;
        }

        return await FetchLogsAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, resourceName, traceId, severity, limit, follow, format, dashboardOnly: dashboardUrl is not null, cancellationToken);
    }

    private async Task<int> FetchLogsAsync(
        string baseUrl,
        string apiToken,
        string? resource,
        string? traceId,
        string? severity,
        int? limit,
        bool follow,
        OutputFormat format,
        bool dashboardOnly,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);

            // Resolve resource name to specific instances (handles replicas)
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, baseUrl, cancellationToken).ConfigureAwait(false);
            var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(resources);

            // Pre-resolve colors so assignment is deterministic regardless of data order
            TelemetryCommandHelpers.ResolveResourceColors(_resourceColorMap, allOtlpResources);

            // If a resource was specified but not found, return error
            if (!TelemetryCommandHelpers.TryResolveResourceNames(resource, resources, out var resolvedResources))
            {
                _interactionService.DisplayError($"Resource '{resource}' not found.");
                return ExitCodeConstants.InvalidCommand;
            }

            // Build URL with query parameters
            int? effectiveLimit = (limit.HasValue && !follow) ? limit.Value : null;

            var url = DashboardUrls.TelemetryLogsApiUrl(baseUrl, resolvedResources, traceId: traceId, severity: severity, limit: effectiveLimit, follow: follow ? true : null);

            if (follow)
            {
                return await StreamLogsAsync(client, url, format, allOtlpResources, cancellationToken);
            }
            else
            {
                return await GetLogsSnapshotAsync(client, url, format, allOtlpResources, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch logs from Dashboard API");
            var errorInfo = await TelemetryCommandHelpers.FormatTelemetryErrorAsync(ex, baseUrl, dashboardOnly, _httpClientFactory, _logger, cancellationToken);
            TelemetryCommandHelpers.DisplayTelemetryError(_interactionService, errorInfo, ExecutionContext.LogFilePath);
            return ExitCodeConstants.DashboardFailure;
        }
    }

    private async Task<int> GetLogsSnapshotAsync(HttpClient client, string url, OutputFormat format, IReadOnlyList<IOtlpResource> allResources, CancellationToken cancellationToken)
    {
        var response = await client.GetAsync(url, cancellationToken);
        TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (format == OutputFormat.Json)
        {
            // Structured output always goes to stdout.
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplayLogsSnapshot(json, allResources);
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> StreamLogsAsync(HttpClient client, string url, OutputFormat format, IReadOnlyList<IOtlpResource> allResources, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        await foreach (var line in reader.ReadLinesAsync(cancellationToken))
        {
            if (format == OutputFormat.Json)
            {
                // Structured output always goes to stdout.
                _interactionService.DisplayRawText(line, ConsoleOutput.Standard);
            }
            else
            {
                DisplayLogsStreamLine(line, allResources);
            }
        }

        return ExitCodeConstants.Success;
    }

    private void DisplayLogsSnapshot(string json, IReadOnlyList<IOtlpResource> allResources)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceLogs = response?.Data?.ResourceLogs;

        if (resourceLogs is null or { Length: 0 })
        {
            TelemetryCommandHelpers.DisplayNoData(_interactionService, "logs");
            return;
        }

        DisplayResourceLogs(resourceLogs, allResources);
    }

    private void DisplayLogsStreamLine(string json, IReadOnlyList<IOtlpResource> allResources)
    {
        var request = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpExportLogsServiceRequestJson);
        DisplayResourceLogs(request?.ResourceLogs ?? [], allResources);
    }

    private void DisplayResourceLogs(IEnumerable<OtlpResourceLogsJson> resourceLogs, IReadOnlyList<IOtlpResource> allResources)
    {
        foreach (var resourceLog in resourceLogs)
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceLog.Resource, allResources);

            foreach (var scopeLog in resourceLog.ScopeLogs ?? [])
            {
                foreach (var log in scopeLog.LogRecords ?? [])
                {
                    DisplayLogEntry(resourceName, log);
                }
            }
        }
    }

    // Using simple text lines instead of Spectre.Console Table for streaming support.
    // Tables require knowing all data upfront, but streaming mode displays logs as they arrive.
    private void DisplayLogEntry(string resourceName, OtlpLogRecordJson log)
    {
        var timestamp = log.TimeUnixNano.HasValue
            ? FormatHelpers.FormatConsoleTime(_timeProvider, OtlpHelpers.UnixNanoSecondsToDateTime(log.TimeUnixNano.Value))
            : "";
        var severity = TelemetryCommandHelpers.GetSeverityText(log.SeverityNumber);
        var body = log.Body?.StringValue ?? "";
        var displayBody = _hostEnvironment.SupportsAnsi ? body : AnsiParser.StripControlSequences(body);

        // Use severity number for color mapping (more reliable than text)
        var severityColor = TelemetryCommandHelpers.GetSeverityColor(log.SeverityNumber);
        var resourceColor = _resourceColorMap.GetColor(resourceName);

        var escapedBody = displayBody.EscapeMarkup();
        _interactionService.DisplayMarkupLine($"[grey]{timestamp}[/] [{severityColor}]{severity,-4}[/] [{resourceColor}]{resourceName.EscapeMarkup()}[/] {escapedBody}");
    }
}
