// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Otlp.Serialization;
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to view traces from the Dashboard telemetry API.
/// </summary>
internal sealed class TelemetryTracesCommand : BaseCommand
{
    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TelemetryTracesCommand> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResourceColorMap _resourceColorMap;
    private readonly TimeProvider _timeProvider;

    // Shared options from TelemetryCommandHelpers
    private static readonly Argument<string?> s_resourceArgument = TelemetryCommandHelpers.CreateResourceArgument();
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = TelemetryCommandHelpers.CreateAppHostOption();
    private static readonly Option<OutputFormat> s_formatOption = TelemetryCommandHelpers.CreateFormatOption();
    private static readonly Option<int?> s_limitOption = TelemetryCommandHelpers.CreateLimitOption();
    private static readonly Option<string?> s_traceIdOption = TelemetryCommandHelpers.CreateTraceIdOption("--trace-id", "-t");
    private static readonly Option<bool?> s_hasErrorOption = TelemetryCommandHelpers.CreateHasErrorOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();

    public TelemetryTracesCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        IHttpClientFactory httpClientFactory,
        ResourceColorMap resourceColorMap,
        TimeProvider timeProvider,
        ILogger<TelemetryTracesCommand> logger)
        : base("traces", TelemetryCommandStrings.TracesDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _httpClientFactory = httpClientFactory;
        _resourceColorMap = resourceColorMap;
        _timeProvider = timeProvider;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_formatOption);
        Options.Add(s_limitOption);
        Options.Add(s_traceIdOption);
        Options.Add(s_hasErrorOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var format = parseResult.GetValue(s_formatOption);
        var limit = parseResult.GetValue(s_limitOption);
        var traceId = parseResult.GetValue(s_traceIdOption);
        var hasError = parseResult.GetValue(s_hasErrorOption);
        var dashboardUrl = parseResult.GetValue(s_dashboardUrlOption);
        var apiKey = parseResult.GetValue(s_apiKeyOption);

        // Validate --limit value
        if (limit.HasValue && limit.Value < 1)
        {
            _interactionService.DisplayError(TelemetryCommandStrings.LimitMustBePositive);
            return ExitCodeConstants.InvalidCommand;
        }

        var dashboardApi = await TelemetryCommandHelpers.GetDashboardApiAsync(
            _connectionResolver, _interactionService, passedAppHostProjectFile, dashboardUrl, apiKey, requireDashboard: true, cancellationToken);

        if (!dashboardApi.Success)
        {
            return dashboardApi.ExitCode;
        }

        try
        {
            if (!string.IsNullOrEmpty(traceId))
            {
                return await FetchSingleTraceAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, traceId, format, dashboardApi.DashboardUrl!, cancellationToken);
            }
            else
            {
                return await FetchTracesAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, resourceName, hasError, limit, format, dashboardApi.DashboardUrl!, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch traces from Dashboard API");
            var errorMessage = await TelemetryCommandHelpers.FormatTelemetryErrorMessageAsync(ex, dashboardApi.BaseUrl!, dashboardUrl is not null, _httpClientFactory, _logger, cancellationToken);
            _interactionService.DisplayError(errorMessage);
            return ExitCodeConstants.DashboardFailure;
        }
    }

    private async Task<int> FetchSingleTraceAsync(
        string baseUrl,
        string apiToken,
        string traceId,
        OutputFormat format,
        string dashboardUrl,
        CancellationToken cancellationToken)
    {
        using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);

        // Fetch resources for name resolution
        var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, baseUrl, cancellationToken).ConfigureAwait(false);
        var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(resources);

        // Pre-resolve colors so assignment is deterministic regardless of data order
        TelemetryCommandHelpers.ResolveResourceColors(_resourceColorMap, allOtlpResources);

        var url = DashboardUrls.TelemetryTraceDetailApiUrl(baseUrl, traceId);

        _logger.LogDebug("Fetching trace {TraceId} from {Url}", traceId, url);

        var response = await client.GetAsync(url, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TelemetryCommandStrings.TraceNotFound, traceId));
            return ExitCodeConstants.InvalidCommand;
        }

        TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (format == OutputFormat.Json)
        {
            // Structured output always goes to stdout.
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            DisplayTraceDetails(json, traceId, allOtlpResources, dashboardUrl);
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> FetchTracesAsync(
        string baseUrl,
        string apiToken,
        string? resource,
        bool? hasError,
        int? limit,
        OutputFormat format,
        string dashboardUrl,
        CancellationToken cancellationToken)
    {
        using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);

        // Resolve resource name to specific instances (handles replicas)
        var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, baseUrl, cancellationToken).ConfigureAwait(false);

        // If a resource was specified but not found, return error
        if (!TelemetryCommandHelpers.TryResolveResourceNames(resource, resources, out var resolvedResources))
        {
            _interactionService.DisplayError($"Resource '{resource}' not found.");
            return ExitCodeConstants.InvalidCommand;
        }

        var allOtlpResources = TelemetryCommandHelpers.ToOtlpResources(resources);

        // Pre-resolve colors so assignment is deterministic regardless of data order
        TelemetryCommandHelpers.ResolveResourceColors(_resourceColorMap, allOtlpResources);

        var url = DashboardUrls.TelemetryTracesApiUrl(baseUrl, resolvedResources, hasError: hasError, limit: limit);

        _logger.LogDebug("Fetching traces from {Url}", url);

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
            DisplayTracesTable(json, allOtlpResources, dashboardUrl);
        }

        return ExitCodeConstants.Success;
    }

    private void DisplayTracesTable(string json, IReadOnlyList<IOtlpResource> allResources, string dashboardUrl)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceSpans = response?.Data?.ResourceSpans;

        if (resourceSpans is null or { Length: 0 })
        {
            TelemetryCommandHelpers.DisplayNoData(_interactionService, "traces");
            return;
        }

        var table = new Table();
        table.AddBoldColumn(TelemetryCommandStrings.HeaderTimestamp);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderName);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderSpans);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderDuration);
        table.AddBoldColumn(TelemetryCommandStrings.HeaderStatus);

        // Group by traceId to show trace summary
        var traceInfos = new Dictionary<string, (string Resource, string FirstSpanName, string TraceId, ulong? StartTimeNano, TimeSpan Duration, int SpanCount, bool HasError)>();

        foreach (var resourceSpan in resourceSpans)
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceSpan.Resource, allResources);

            foreach (var scopeSpan in resourceSpan.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpan.Spans ?? [])
                {
                    var traceIdValue = span.TraceId ?? "";

                    if (string.IsNullOrEmpty(traceIdValue))
                    {
                        continue;
                    }

                    var duration = OtlpHelpers.CalculateDuration(span.StartTimeUnixNano, span.EndTimeUnixNano);
                    var hasError = span.Status?.Code == 2; // ERROR status

                    if (traceInfos.TryGetValue(traceIdValue, out var info))
                    {
                        var maxDuration = info.Duration > duration ? info.Duration : duration;
                        // Track earliest start time across all spans in the trace
                        var earliestStart = info.StartTimeNano.HasValue && span.StartTimeUnixNano.HasValue
                            ? (info.StartTimeNano.Value < span.StartTimeUnixNano.Value ? info.StartTimeNano : span.StartTimeUnixNano)
                            : info.StartTimeNano ?? span.StartTimeUnixNano;
                        traceInfos[traceIdValue] = (info.Resource, info.FirstSpanName, info.TraceId, earliestStart, maxDuration, info.SpanCount + 1, info.HasError || hasError);
                    }
                    else
                    {
                        traceInfos[traceIdValue] = (resourceName, span.Name ?? "", traceIdValue, span.StartTimeUnixNano, duration, 1, hasError);
                    }
                }
            }
        }

        foreach (var (_, info) in traceInfos.OrderBy(x => x.Value.StartTimeNano ?? 0))
        {
            var statusText = info.HasError ? "[red]ERR[/]" : "[green]OK[/]";
            var durationStr = TelemetryCommandHelpers.FormatDuration(info.Duration);
            var resourceColor = _resourceColorMap.GetColor(info.Resource);
            var timestamp = info.StartTimeNano.HasValue
                ? FormatHelpers.FormatConsoleTime(_timeProvider, OtlpHelpers.UnixNanoSecondsToDateTime(info.StartTimeNano.Value))
                : "";
            var shortTraceId = OtlpHelpers.ToShortenedId(info.TraceId);
            var traceLink = TelemetryCommandHelpers.FormatTraceLink(dashboardUrl, info.TraceId, $"[grey]{shortTraceId}[/]");
            var nameColumn = $"[{resourceColor}]{info.Resource.EscapeMarkup()}[/]: {info.FirstSpanName.EscapeMarkup()} {traceLink}";
            table.AddRow(timestamp, nameColumn, info.SpanCount.ToString(CultureInfo.InvariantCulture), durationStr, statusText);
        }

        _interactionService.DisplayRenderable(table);
        _interactionService.DisplayMarkupLine($"[grey]Showing {traceInfos.Count} of {response?.TotalCount ?? traceInfos.Count} traces[/]");
    }

    private void DisplayTraceDetails(string json, string traceId, IReadOnlyList<IOtlpResource> allResources, string dashboardUrl)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceSpans = response?.Data?.ResourceSpans;

        // Collect all spans with their metadata
        var spans = new List<SpanInfo>();

        foreach (var resourceSpan in resourceSpans ?? [])
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceSpan.Resource, allResources);

            foreach (var scopeSpan in resourceSpan.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpan.Spans ?? [])
                {
                    var spanId = span.SpanId ?? "";
                    var parentSpanId = span.ParentSpanId;
                    var name = span.Name ?? "";
                    var duration = OtlpHelpers.CalculateDuration(span.StartTimeUnixNano, span.EndTimeUnixNano);
                    var startNano = (long)(span.StartTimeUnixNano ?? 0);
                    var hasError = span.Status?.Code == 2; // ERROR status

                    spans.Add(new SpanInfo(spanId, parentSpanId, resourceName, name, duration, startNano, hasError));
                }
            }
        }

        if (spans.Count == 0)
        {
            _interactionService.DisplayMarkupLine($"[bold]Trace: {traceId}[/]");
            _interactionService.DisplayMarkupLine("[dim]No spans found[/]");
            return;
        }

        // Calculate total duration from root spans
        var rootSpans = spans.Where(s => string.IsNullOrEmpty(s.ParentSpanId)).ToList();
        var totalDuration = rootSpans.Count > 0 ? rootSpans.Max(s => s.Duration) : spans.Max(s => s.Duration);

        // Header
        var traceLink = TelemetryCommandHelpers.FormatTraceLink(dashboardUrl, traceId, traceId);
        _interactionService.DisplayMarkupLine($"[bold]Trace:[/] {traceLink}");
        _interactionService.DisplayMarkupLine($"[bold]Duration:[/] {TelemetryCommandHelpers.FormatDuration(totalDuration)}  [bold]Spans:[/] {spans.Count}");
        _interactionService.DisplayEmptyLine();

        // Build tree and display
        DisplaySpanTree(spans, dashboardUrl, traceId);
    }

    private void DisplaySpanTree(List<SpanInfo> spans, string dashboardUrl, string traceId)
    {
        // Build a lookup of children by parent ID
        var childrenByParent = spans
            .Where(s => !string.IsNullOrEmpty(s.ParentSpanId))
            .GroupBy(s => s.ParentSpanId!)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartNano).ToList());

        // Find root spans (no parent or parent not in this trace)
        var spanIds = spans.Select(s => s.SpanId).ToHashSet();
        var rootSpans = spans
            .Where(s => string.IsNullOrEmpty(s.ParentSpanId) || !spanIds.Contains(s.ParentSpanId!))
            .OrderBy(s => s.StartNano)
            .ToList();

        // Track which resources we've seen to show resource transitions
        string? lastResource = null;

        foreach (var root in rootSpans)
        {
            DisplaySpanNode(root, childrenByParent, "", true, dashboardUrl, traceId, ref lastResource);
        }
    }

    private void DisplaySpanNode(
        SpanInfo span,
        Dictionary<string, List<SpanInfo>> childrenByParent,
        string indent,
        bool isLast,
        string dashboardUrl,
        string traceId,
        ref string? lastResource)
    {
        // Show resource name when it changes (indicates cross-service call)
        if (span.ResourceName != lastResource)
        {
            if (lastResource != null)
            {
                _interactionService.DisplayEmptyLine(); // Blank line between resources
            }
            var resourceColor = _resourceColorMap.GetColor(span.ResourceName);
            _interactionService.DisplayMarkupLine($"{indent}[bold {resourceColor}]{span.ResourceName.EscapeMarkup()}[/]");
            lastResource = span.ResourceName;
        }

        // Build the connector
        var connector = isLast ? "└─" : "├─";
        var childIndent = indent + (isLast ? "   " : "│  ");

        // Format span line with spanId
        var statusColor = span.HasError ? "red" : "green";
        var statusText = span.HasError ? "ERR" : "OK";
        var durationStr = TelemetryCommandHelpers.FormatDuration(span.Duration).PadLeft(8);
        var shortenedSpanId = OtlpHelpers.ToShortenedId(span.SpanId);
        var spanIdLink = TelemetryCommandHelpers.FormatTraceLink(dashboardUrl, traceId, $"[dim]{shortenedSpanId}[/]", spanId: span.SpanId);
        var escapedName = span.Name.EscapeMarkup();

        // Truncate long names
        var maxNameLength = 50;
        var displayName = escapedName.Length > maxNameLength
            ? escapedName[..(maxNameLength - 3)] + "..."
            : escapedName;

        _interactionService.DisplayMarkupLine($"{indent}{connector} {spanIdLink} {displayName} [{statusColor}]{statusText}[/] [dim]{durationStr}[/]");

        // Render children
        if (childrenByParent.TryGetValue(span.SpanId, out var children))
        {
            for (var i = 0; i < children.Count; i++)
            {
                DisplaySpanNode(children[i], childrenByParent, childIndent, i == children.Count - 1, dashboardUrl, traceId, ref lastResource);
            }
        }
    }

    private sealed record SpanInfo(
        string SpanId,
        string? ParentSpanId,
        string ResourceName,
        string Name,
        TimeSpan Duration,
        long StartNano,
        bool HasError);
}
