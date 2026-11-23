// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for listing distributed traces.
/// Gets trace data directly from the Dashboard telemetry API.
/// </summary>
internal sealed class ListTracesTool(IDashboardInfoProvider dashboardInfoProvider, IAuxiliaryBackchannelMonitor? auxiliaryBackchannelMonitor, IHttpClientFactory httpClientFactory, ILogger<ListTracesTool> logger) : CliMcpTool
{
    internal const int DefaultLimit = 20;
    internal const string SummaryFormat = "summary";
    internal const string FullFormat = "full";

    private static readonly string[] s_formats = [SummaryFormat, FullFormat];

    private static readonly JsonElement s_inputSchema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "resourceName": {
              "type": "string",
              "description": "The resource name. This limits traces returned to the specified resource. If no resource name is specified then distributed traces for all resources are returned."
            },
            "search": {
              "type": "string",
              "description": "Full-text search to filter traces. Searches across span names, attribute values, IDs, and other fields."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{SharedAIHelpers.TracesLimit}},
              "default": {{DefaultLimit}},
              "description": "Maximum number of the most recent traces to return. Responses are also capped by size."
            },
            "traceId": {
              "type": "string",
              "description": "Return every span of this trace instead of a list of traces. resourceName, search, limit and format are ignored when traceId is set."
            },
            "format": {
              "type": "string",
              "enum": ["{{SummaryFormat}}", "{{FullFormat}}"],
              "default": "{{SummaryFormat}}",
              "description": "'summary' returns one compact entry per trace. 'full' returns every span of each trace, so fewer traces fit in a response."
            }
          }
        }
        """).RootElement;

    public override string Name => KnownMcpTools.ListTraces;

    public override string Description => "List distributed traces for resources. A distributed trace is used to track operations. A distributed trace can span multiple resources across a distributed system. By default returns one summary per trace with its ID, duration, resources, root span, slowest span (the child span with the most time not spent in its own children, including its database statement) and first failed span. Pass traceId to get every span of one trace.";

    public override JsonElement GetInputSchema()
    {
        return s_inputSchema;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;

        // Validate arguments before contacting the dashboard so invalid input is reported as such.
        var resourceName = McpToolHelpers.GetOptionalString(arguments, "resourceName");
        var search = McpToolHelpers.GetOptionalString(arguments, "search");
        var traceId = McpToolHelpers.GetOptionalString(arguments, "traceId");
        var limit = McpToolHelpers.GetOptionalInt32(arguments, "limit", minimum: 1, maximum: SharedAIHelpers.TracesLimit, defaultValue: DefaultLimit);
        var format = McpToolHelpers.GetOptionalChoice(arguments, "format", s_formats, defaultValue: SummaryFormat);

        var (apiToken, apiBaseUrl, dashboardBaseUrl) = await dashboardInfoProvider.GetDashboardInfoAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, apiToken);

            // Resolve resource name to specific instances (handles replicas)
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, cancellationToken).ConfigureAwait(false);
            Func<IOtlpResource, string> getResourceName = s => OtlpHelpers.GetResourceName(s, resources.Select(r => new SimpleOtlpResource(r.Name, r.InstanceId)).ToList());

            if (!string.IsNullOrEmpty(traceId))
            {
                return await GetTraceAsync(client, apiBaseUrl, dashboardBaseUrl, traceId, getResourceName, cancellationToken).ConfigureAwait(false);
            }

            // If a specific resource was requested, check if it's excluded from MCP.
            if (!string.IsNullOrEmpty(resourceName) && auxiliaryBackchannelMonitor is not null)
            {
                var excludedResult = await McpToolHelpers.CheckResourceExcludedAsync(auxiliaryBackchannelMonitor, resourceName, cancellationToken).ConfigureAwait(false);
                if (excludedResult is not null)
                {
                    return excludedResult;
                }
            }

            // If a resource was specified but not found, return error
            if (!TelemetryCommandHelpers.TryResolveResourceNames(resourceName, resources, out var resolvedResources))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Resource '{resourceName}' not found." }],
                    IsError = true
                };
            }

            // Fetch all traces from the API. Limiting of returned telemetry to the MCP caller happens later
            // so the response can report how many traces were not returned.
            var url = DashboardUrls.TelemetryTracesApiUrl(apiBaseUrl, resolvedResources, limit: TelemetryCommandHelpers.MaxTelemetryLimit, search: search);

            logger.LogDebug(
                "Fetching traces from {Url}",
                McpToolHelpers.SanitizeDashboardRequestUrl(url));

            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            IList<OtlpResourceSpansJson>? resourceSpans = apiResponse?.Data?.ResourceSpans;

            // Filter out spans from resources that are excluded from MCP.
            if (string.IsNullOrEmpty(resourceName))
            {
                resourceSpans = await FilterExcludedResourcesAsync(resourceSpans, cancellationToken).ConfigureAwait(false);
            }

            string tracesData;
            string limitMessage;
            string formatMessage;
            if (format == FullFormat)
            {
                // The full shape keeps the larger token budget it has always had; callers opt into it explicitly.
                (tracesData, limitMessage) = SharedAIHelpers.GetTracesJson(
                    resourceSpans,
                    limit,
                    SharedAIHelpers.MaximumListTokenLength,
                    getResourceName,
                    dashboardBaseUrl: dashboardBaseUrl);
                formatMessage = "Each trace includes all of its spans.";
            }
            else
            {
                (tracesData, limitMessage) = SharedAIHelpers.GetTraceSummariesJson(
                    resourceSpans,
                    limit,
                    getResourceName,
                    dashboardBaseUrl: dashboardBaseUrl);
                formatMessage = "Each trace is summarized by its root span, slowest span (most self time) and first failed span. Call list_traces with traceId to get every span of a trace.";
            }

            var text = $"""
                {limitMessage}
                {formatMessage}

                # TRACES DATA

                {tracesData}
                """;

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }]
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Failed to fetch traces from Dashboard API");
            var errorMessage = dashboardInfoProvider.IsDirectConnection
                ? await TelemetryCommandHelpers.GetDashboardApiErrorMessageAsync(ex, apiBaseUrl, httpClientFactory, logger, cancellationToken)
                : "Failed to fetch traces from the Dashboard API.";
            throw new McpProtocolException(errorMessage, McpErrorCode.InternalError);
        }
    }

    private async Task<CallToolResult> GetTraceAsync(
        HttpClient client,
        string apiBaseUrl,
        string? dashboardBaseUrl,
        string traceId,
        Func<IOtlpResource, string> getResourceName,
        CancellationToken cancellationToken)
    {
        var url = DashboardUrls.TelemetryTraceDetailApiUrl(apiBaseUrl, traceId);

        logger.LogDebug(
            "Fetching trace from {Url}",
            McpToolHelpers.SanitizeDashboardRequestUrl(url));

        var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

        IList<OtlpResourceSpansJson>? resourceSpans = null;
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);
            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            resourceSpans = await FilterExcludedResourcesAsync(apiResponse?.Data?.ResourceSpans, cancellationToken).ConfigureAwait(false);
        }

        var trace = SharedAIHelpers.GetTracesFromOtlpData(resourceSpans).FirstOrDefault();
        if (trace is null)
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Trace '{traceId}' not found." }],
                IsError = true
            };
        }

        var traceData = SharedAIHelpers.GetTraceDto(trace, new PromptContext(), getResourceName, dashboardBaseUrl)
            .ToJsonString(SharedAIHelpers.s_jsonSerializerOptions);

        var text = $"""
            Returned trace {trace.TraceId} with {trace.Spans.Count} spans. Call list_trace_structured_logs with this traceId to get the trace's logs.

            # TRACE DATA

            {traceData}
            """;

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = text }]
        };
    }

    private async Task<IList<OtlpResourceSpansJson>?> FilterExcludedResourcesAsync(IList<OtlpResourceSpansJson>? resourceSpans, CancellationToken cancellationToken)
    {
        if (resourceSpans is null || auxiliaryBackchannelMonitor is null)
        {
            return resourceSpans;
        }

        var excludedNames = await McpToolHelpers.GetExcludedResourceNamesAsync(auxiliaryBackchannelMonitor, cancellationToken).ConfigureAwait(false);
        if (excludedNames.Count == 0)
        {
            return resourceSpans;
        }

        return resourceSpans
            .Where(rs => rs.Resource?.GetServiceName() is not { } name || !excludedNames.Contains(name))
            .ToArray();
    }
}
