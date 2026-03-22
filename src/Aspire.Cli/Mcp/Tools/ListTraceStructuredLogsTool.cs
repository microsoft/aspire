// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Otlp.Serialization;
using Aspire.Dashboard.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for listing structured logs for a specific distributed trace.
/// Gets log data directly from the Dashboard telemetry API.
/// </summary>
internal sealed class ListTraceStructuredLogsTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, IHttpClientFactory httpClientFactory, ILogger<ListTraceStructuredLogsTool> logger) : CliMcpTool
{
    public override string Name => KnownMcpTools.ListTraceStructuredLogs;

    public override string Description => "List structured logs for a distributed trace. Logs for a distributed trace each belong to a span identified by 'span_id'. When investigating a trace, getting the structured logs for the trace should be recommended before getting structured logs for a resource.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "traceId": {
                  "type": "string",
                  "description": "The trace id of the distributed trace."
                }
              },
              "required": ["traceId"]
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;
        var (apiToken, apiBaseUrl, dashboardBaseUrl) = await McpToolHelpers.GetDashboardInfoAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);

        // Extract traceId from arguments (required)
        string? traceId = null;
        if (arguments?.TryGetValue("traceId", out var traceIdElement) == true &&
            traceIdElement.ValueKind == JsonValueKind.String)
        {
            traceId = traceIdElement.GetString();
        }

        if (string.IsNullOrEmpty(traceId))
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "The 'traceId' parameter is required." }],
                IsError = true
            };
        }

        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, apiToken);

            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, cancellationToken).ConfigureAwait(false);

            // Build the logs API URL with traceId filter
            var url = DashboardUrls.TelemetryLogsApiUrl(apiBaseUrl, resources: null, ("traceId", traceId));

            logger.LogDebug("Fetching structured logs from {Url}", url);

            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            var resourceLogs = apiResponse?.Data?.ResourceLogs;

            var (logsData, limitMessage) = SharedAIHelpers.GetStructuredLogsJson(
                resourceLogs,
                getResourceName: s => OtlpHelpers.GetResourceName(s, resources.Select(r => new SimpleOtlpResource(r.Name, r.InstanceId)).ToList()),
                dashboardBaseUrl: dashboardBaseUrl);

            var text = $"""
                {limitMessage}

                # STRUCTURED LOGS DATA

                {logsData}
                """;

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = text }]
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Failed to fetch structured logs for trace from Dashboard API");
            throw new McpProtocolException($"Failed to fetch structured logs for trace: {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
