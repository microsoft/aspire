// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
/// MCP tool for listing structured logs.
/// Gets log data directly from the Dashboard telemetry API.
/// </summary>
internal sealed class ListStructuredLogsTool(IDashboardInfoProvider dashboardInfoProvider, IAuxiliaryBackchannelMonitor? auxiliaryBackchannelMonitor, IHttpClientFactory httpClientFactory, ILogger<ListStructuredLogsTool> logger) : CliMcpTool
{
    internal const int DefaultLimit = 50;

    // Minimum severities accepted by the Dashboard telemetry API, which parses them as Microsoft.Extensions.Logging.LogLevel.
    private static readonly string[] s_severities = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    private static readonly JsonElement s_inputSchema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "resourceName": {
              "type": "string",
              "description": "The resource name. This limits logs returned to the specified resource. If no resource name is specified then structured logs for all resources are returned."
            },
            "search": {
              "type": "string",
              "description": "Full-text search to filter logs. Searches across log text, attribute values, names, source, IDs, and other fields."
            },
            "severity": {
              "type": "string",
              "enum": ["Trace", "Debug", "Information", "Warning", "Error", "Critical"],
              "description": "Minimum severity of the logs to return. For example, 'Warning' returns Warning, Error and Critical logs."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{SharedAIHelpers.StructuredLogsLimit}},
              "default": {{DefaultLimit}},
              "description": "Maximum number of the most recent log entries to return. Responses are also capped by size."
            }
          }
        }
        """).RootElement;

    public override string Name => KnownMcpTools.ListStructuredLogs;

    public override string Description => "List structured logs for resources. Returns the most recent log entries; use severity, search, resourceName and limit to narrow the results.";

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
        var severity = McpToolHelpers.GetOptionalChoice(arguments, "severity", s_severities, defaultValue: null);
        var limit = McpToolHelpers.GetOptionalInt32(arguments, "limit", minimum: 1, maximum: SharedAIHelpers.StructuredLogsLimit, defaultValue: DefaultLimit);

        var (apiToken, apiBaseUrl, dashboardBaseUrl) = await dashboardInfoProvider.GetDashboardInfoAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, apiToken);

            // Resolve resource name to specific instances (handles replicas)
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, cancellationToken).ConfigureAwait(false);

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

            // Fetch all logs from the API. Limiting of returned telemetry to the MCP caller happens later.
            var url = DashboardUrls.TelemetryLogsApiUrl(apiBaseUrl, resolvedResources, severity: severity, limit: TelemetryCommandHelpers.MaxTelemetryLimit, search: search);

            logger.LogDebug(
                "Fetching structured logs from {Url}",
                McpToolHelpers.SanitizeDashboardRequestUrl(url));

            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);

            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            var resourceLogs = apiResponse?.Data?.ResourceLogs;

            // Filter out logs from resources that are excluded from MCP.
            if (resourceLogs is not null && string.IsNullOrEmpty(resourceName) && auxiliaryBackchannelMonitor is not null)
            {
                var excludedNames = await McpToolHelpers.GetExcludedResourceNamesAsync(auxiliaryBackchannelMonitor, cancellationToken).ConfigureAwait(false);
                if (excludedNames.Count > 0)
                {
                    resourceLogs = resourceLogs
                        .Where(rl => rl.Resource?.GetServiceName() is not { } name || !excludedNames.Contains(name))
                        .ToArray();
                }
            }

            var (logsData, limitMessage) = SharedAIHelpers.GetStructuredLogsJson(
                resourceLogs,
                limit,
                SharedAIHelpers.CompactListTokenLength,
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
            logger.LogError("Failed to fetch structured logs from Dashboard API");
            var errorMessage = dashboardInfoProvider.IsDirectConnection
                ? await TelemetryCommandHelpers.GetDashboardApiErrorMessageAsync(ex, apiBaseUrl, httpClientFactory, logger, cancellationToken)
                : "Failed to fetch structured logs from the Dashboard API.";
            throw new McpProtocolException(errorMessage, McpErrorCode.InternalError);
        }
    }
}
