// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Aspire.Shared.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Dashboard.Mcp;

public static class McpExtensions
{
    public static IMcpServerBuilder AddAspireMcpTools(this IServiceCollection services, DashboardOptions dashboardOptions)
    {
        var builder = services.AddMcpServer(options =>
        {
            var icons = McpIconHelper.GetAspireIcons(typeof(McpExtensions).Assembly, "Aspire.Dashboard.Mcp.Resources");

            options.ServerInfo = new Implementation
            {
                Name = "Aspire MCP",
                Version = VersionHelpers.DashboardDisplayVersion ?? "1.0.0",
                Icons = icons
            };
            options.ServerInstructions =
                """
                ## Description
                This MCP Server provides various tools for managing Aspire resources, logs, traces and commands.

                ## Instructions
                - When a resource, structured log or trace is returned, include a link to the Aspire dashboard using dashboard_link
                - When a resource state (running, stopped, starting, ...) is returned, and add an emoji colored badge next to it (green, red, orange, etc).

                ## Tools

                """;
        }).WithHttpTransport();

        // Always register telemetry tools
        builder.WithTools<AspireTelemetryMcpTools>();

        // Only register resource tools if the resource service is configured
        if (dashboardOptions.ResourceServiceClient.GetUri() is not null)
        {
            builder.WithTools<AspireResourceMcpTools>();
        }

        builder
            .WithRequestFilters(filters => filters
                .AddListToolsFilter((next) => async (RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken) =>
                {
                    // Calls here are via the tools/list endpoint. See https://modelcontextprotocol.info/docs/concepts/tools/
                    // There is no tool name so we hardcode name to list_tools here so we can reuse the same event.
                    //
                    // We want to track when users list tools as it's an indicator of whether Aspire MCP is configured (client tools refresh tools via it).
                    // It's called even if no Aspire tools end up being used.
                    return await RecordCallToolNameAsync<ListToolsRequestParams, ListToolsResult>(next, request, "list_tools", cancellationToken).ConfigureAwait(false);
                })
                .AddCallToolFilter((next) => async (RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken) =>
                {
                    return await RecordCallToolNameAsync<CallToolRequestParams, CallToolResult>(next, request, request.Params?.Name, cancellationToken).ConfigureAwait(false);
                }));

        return builder;
    }

    private static async Task<TResult> RecordCallToolNameAsync<TParams, TResult>(McpRequestHandler<TParams, TResult> next, RequestContext<TParams> request, string? toolCallName, CancellationToken cancellationToken)
    {
        // Record the tool name to telemetry.
        OperationContextProperty? operationId = null;
        var telemetryService = request.Services?.GetService<DashboardTelemetryService>();
        if (telemetryService != null && toolCallName != null)
        {
            var startToolCall = telemetryService.StartOperation(TelemetryEventKeys.McpToolCall,
                new Dictionary<string, AspireTelemetryProperty>
                {
                    { TelemetryPropertyKeys.McpToolName, new AspireTelemetryProperty(toolCallName) },
                });

            operationId = startToolCall.Properties.FirstOrDefault();
        }

        try
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);

            if (telemetryService is not null && operationId is not null)
            {
                telemetryService.EndOperation(operationId, TelemetryResult.Success);
            }

            return result;
        }
        catch (Exception ex)
        {
            if (telemetryService is not null && operationId is not null)
            {
                telemetryService.EndOperation(operationId, TelemetryResult.Failure, ex.Message);
            }

            throw;
        }
    }
}
