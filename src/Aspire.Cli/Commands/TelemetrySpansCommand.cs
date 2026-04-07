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
using Aspire.Cli.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command to view spans from the Dashboard telemetry API.
/// </summary>
internal sealed class TelemetrySpansCommand : BaseCommand
{
    private readonly IInteractionService _interactionService;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<TelemetrySpansCommand> _logger;
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
    private static readonly Option<bool?> s_hasErrorOption = TelemetryCommandHelpers.CreateHasErrorOption();
    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();

    public TelemetrySpansCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        IHttpClientFactory httpClientFactory,
        ResourceColorMap resourceColorMap,
        TimeProvider timeProvider,
        ILogger<TelemetrySpansCommand> logger)
        : base("spans", TelemetryCommandStrings.SpansDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
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
        Options.Add(s_hasErrorOption);
        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
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

        return await FetchSpansAsync(dashboardApi.BaseUrl!, dashboardApi.ApiToken!, resourceName, traceId, hasError, limit, follow, format, dashboardOnly: dashboardUrl is not null, cancellationToken);
    }

    private async Task<int> FetchSpansAsync(
        string baseUrl,
        string apiToken,
        string? resource,
        string? traceId,
        bool? hasError,
        int? limit,
        bool follow,
        OutputFormat format,
        bool dashboardOnly,
        CancellationToken cancellationToken)
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

        // Build query string with multiple resource parameters
        var additionalParams = new List<(string key, string? value)>
        {
            ("traceId", traceId)
        };
        if (hasError.HasValue)
        {
            additionalParams.Add(("hasError", hasError.Value.ToString().ToLowerInvariant()));
        }
        if (limit.HasValue && !follow)
        {
            additionalParams.Add(("limit", limit.Value.ToString(CultureInfo.InvariantCulture)));
        }
        if (follow)
        {
            additionalParams.Add(("follow", "true"));
        }

        var url = DashboardUrls.TelemetrySpansApiUrl(baseUrl, resolvedResources, [.. additionalParams]);

        _logger.LogDebug("Fetching spans from {Url}", url);

        try
        {
            if (follow)
            {
                return await StreamSpansAsync(client, url, format, allOtlpResources, cancellationToken);
            }
            else
            {
                return await GetSpansSnapshotAsync(client, url, format, allOtlpResources, cancellationToken);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch spans from Dashboard API");
            var errorMessage = await TelemetryCommandHelpers.FormatTelemetryErrorMessageAsync(ex, baseUrl, dashboardOnly, _httpClientFactory, _logger, cancellationToken);
            _interactionService.DisplayError(errorMessage);
            return ExitCodeConstants.DashboardFailure;
        }
    }

    private async Task<int> GetSpansSnapshotAsync(HttpClient client, string url, OutputFormat format, IReadOnlyList<IOtlpResource> allResources, CancellationToken cancellationToken)
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
            DisplaySpansSnapshot(json, allResources);
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> StreamSpansAsync(HttpClient client, string url, OutputFormat format, IReadOnlyList<IOtlpResource> allResources, CancellationToken cancellationToken)
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
                DisplaySpansStreamLine(line, allResources);
            }
        }

        return ExitCodeConstants.Success;
    }

    private void DisplaySpansSnapshot(string json, IReadOnlyList<IOtlpResource> allResources)
    {
        var response = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourceSpans = response?.Data?.ResourceSpans;

        if (resourceSpans is null or { Length: 0 })
        {
            TelemetryCommandHelpers.DisplayNoData(_interactionService, "spans");
            return;
        }

        DisplayResourceSpans(resourceSpans, allResources);
    }

    private void DisplaySpansStreamLine(string json, IReadOnlyList<IOtlpResource> allResources)
    {
        var request = JsonSerializer.Deserialize(json, OtlpJsonSerializerContext.Default.OtlpExportTraceServiceRequestJson);
        DisplayResourceSpans(request?.ResourceSpans ?? [], allResources);
    }

    private void DisplayResourceSpans(IEnumerable<OtlpResourceSpansJson> resourceSpans, IReadOnlyList<IOtlpResource> allResources)
    {
        var allSpans = new List<(string ResourceName, OtlpSpanJson Span)>();

        foreach (var resourceSpan in resourceSpans)
        {
            var resourceName = TelemetryCommandHelpers.ResolveResourceName(resourceSpan.Resource, allResources);

            foreach (var scopeSpan in resourceSpan.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpan.Spans ?? [])
                {
                    allSpans.Add((resourceName, span));
                }
            }
        }

        foreach (var (resourceName, span) in allSpans.OrderBy(s => s.Span.StartTimeUnixNano ?? 0))
        {
            DisplaySpanEntry(resourceName, span);
        }
    }

    // Using simple text lines instead of Spectre.Console Table for streaming support.
    // Tables require knowing all data upfront, but streaming mode displays spans as they arrive.
    private void DisplaySpanEntry(string resourceName, OtlpSpanJson span)
    {
        var name = span.Name ?? "";
        var spanId = span.SpanId ?? "";
        var duration = OtlpHelpers.CalculateDuration(span.StartTimeUnixNano, span.EndTimeUnixNano);
        var hasError = span.Status?.Code == 2; // ERROR status

        var statusColor = hasError ? Color.Red : Color.Green;
        var statusText = hasError ? "ERR" : "OK ";

        var timestamp = span.StartTimeUnixNano.HasValue
            ? FormatHelpers.FormatConsoleTime(_timeProvider, OtlpHelpers.UnixNanoSecondsToDateTime(span.StartTimeUnixNano.Value))
            : "";
        var shortSpanId = OtlpHelpers.ToShortenedId(spanId);
        var durationStr = TelemetryCommandHelpers.FormatDuration(duration);
        var resourceColor = _resourceColorMap.GetColor(resourceName);

        var escapedName = name.EscapeMarkup();
        _interactionService.DisplayMarkupLine($"[grey]{timestamp}[/] [{statusColor}]{statusText}[/] [white]{durationStr,8}[/] [{resourceColor}]{resourceName.EscapeMarkup()}[/]: {escapedName} [grey]{shortSpanId}[/]");
    }
}
