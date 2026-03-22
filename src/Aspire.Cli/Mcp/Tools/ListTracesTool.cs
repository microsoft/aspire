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
/// MCP tool for listing distributed traces.
/// Gets trace data directly from the Dashboard telemetry API.
/// </summary>
internal sealed class ListTracesTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, IHttpClientFactory httpClientFactory, ILogger<ListTracesTool> logger) : CliMcpTool
{
    public override string Name => KnownMcpTools.ListTraces;

    public override string Description => "List distributed traces for resources. A distributed trace is used to track operations. A distributed trace can span multiple resources across a distributed system. Includes a list of distributed traces with their IDs, resources in the trace, duration and whether an error occurred in the trace.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "resourceName": {
                  "type": "string",
                  "description": "The resource name. This limits traces returned to the specified resource. If no resource name is specified then distributed traces for all resources are returned."
                }
              }
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;
        var (apiToken, apiBaseUrl, dashboardBaseUrl) = await McpToolHelpers.GetDashboardInfoAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);

        // Extract resourceName from arguments
        string? resourceName = null;
        if (arguments?.TryGetValue("resourceName", out var resourceNameElement) == true &&
            resourceNameElement.ValueKind == JsonValueKind.String)
        {
            resourceName = resourceNameElement.GetString();
        }

        try
        {
            using var client = TelemetryCommandHelpers.CreateApiClient(httpClientFactory, apiToken);

            // Resolve resource name to specific instances (handles replicas)
            var resources = await TelemetryCommandHelpers.GetAllResourcesAsync(client, apiBaseUrl, cancellationToken).ConfigureAwait(false);

            // If a resource was specified but not found, return error
            if (!TelemetryCommandHelpers.TryResolveResourceNames(resourceName, resources, out var resolvedResources))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Resource '{resourceName}' not found." }],
                    IsError = true
                };
            }

            var url = DashboardUrls.TelemetryTracesApiUrl(apiBaseUrl, resolvedResources);

            logger.LogDebug("Fetching traces from {Url}", url);

            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var apiResponse = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse, cancellationToken).ConfigureAwait(false);
            var resourceSpans = apiResponse?.Data?.ResourceSpans;

            var (tracesData, limitMessage) = SharedAIHelpers.GetTracesJson(
                resourceSpans,
                getResourceName: s => OtlpHelpers.GetResourceName(s, resources.Select(r => new SimpleOtlpResource(r.Name, r.InstanceId)).ToList()),
                dashboardBaseUrl: dashboardBaseUrl);

            var text = $"""
                {limitMessage}

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
            logger.LogError(ex, "Failed to fetch traces from Dashboard API");
            throw new McpProtocolException($"Failed to fetch traces: {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
