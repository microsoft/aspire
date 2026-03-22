// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Aspire.Dashboard.Mcp;

/// <summary>
/// MCP tools for telemetry data that don't require a resource service.
/// </summary>
internal sealed class AspireTelemetryMcpTools
{
    private readonly TelemetryRepository _telemetryRepository;
    private readonly IOutgoingPeerResolver[] _outgoingPeerResolvers;
    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;
    private readonly IDashboardClient _dashboardClient;
    private readonly ILogger<AspireTelemetryMcpTools> _logger;

    public AspireTelemetryMcpTools(TelemetryRepository telemetryRepository,
        IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers,
        IOptionsMonitor<DashboardOptions> dashboardOptions,
        IDashboardClient dashboardClient,
        ILogger<AspireTelemetryMcpTools> logger)
    {
        _telemetryRepository = telemetryRepository;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToArray();
        _dashboardOptions = dashboardOptions;
        _dashboardClient = dashboardClient;
        _logger = logger;
    }

    [McpServerTool(Name = "list_structured_logs")]
    [Description("List structured logs for resources.")]
    public string ListStructuredLogs(
        [Description("The resource name. This limits logs returned to the specified resource. If no resource name is specified then structured logs for all resources are returned.")]
        string? resourceName = null)
    {
        _logger.LogDebug("MCP tool list_structured_logs called with resource '{ResourceName}'.", resourceName);

        if (!TryResolveResourceNameForTelemetry(resourceName, out var message, out var resourceKey))
        {
            return message;
        }

        // Get all logs because we want the most recent logs and they're at the end of the results.
        // If support is added for ordering logs by timestamp then improve this.
        var logs = _telemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        }).Items;

        if (_dashboardClient.IsEnabled)
        {
            var optOutResources = GetOptOutResources(_dashboardClient.GetResources());
            if (optOutResources.Count > 0)
            {
                logs = logs.Where(l => !optOutResources.Any(r => l.ResourceView.ResourceKey.EqualsCompositeName(r.Name))).ToList();
            }
        }

        var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(logs);
        var resources = _telemetryRepository.GetResources();
        var (logsData, limitMessage) = AIHelpers.GetStructuredLogsJson(
            otlpData,
            _dashboardOptions.CurrentValue,
            includeDashboardUrl: true,
            getResourceName: r => OtlpHelpers.GetResourceName(r, resources));

        var response = $"""
            Always format log_id in the response as code like this: `log_id: 123`.
            {limitMessage}

            # STRUCTURED LOGS DATA

            {logsData}
            """;

        return response;
    }

    [McpServerTool(Name = "list_traces")]
    [Description("List distributed traces for resources. A distributed trace is used to track operations. A distributed trace can span multiple resources across a distributed system. Includes a list of distributed traces with their IDs, resources in the trace, duration and whether an error occurred in the trace.")]
    public string ListTraces(
        [Description("The resource name. This limits traces returned to the specified resource. If no resource name is specified then distributed traces for all resources are returned.")]
        string? resourceName = null)
    {
        _logger.LogDebug("MCP tool list_traces called with resource '{ResourceName}'.", resourceName);

        if (!TryResolveResourceNameForTelemetry(resourceName, out var message, out var resourceKey))
        {
            return message;
        }

        var traces = _telemetryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            FilterText = string.Empty
        }).PagedResult.Items;

        if (_dashboardClient.IsEnabled)
        {
            var optOutResources = GetOptOutResources(_dashboardClient.GetResources());
            if (optOutResources.Count > 0)
            {
                traces = traces.Where(t => !optOutResources.Any(r => t.Spans.Any(s => s.Source.ResourceKey.EqualsCompositeName(r.Name)))).ToList();
            }
        }

        var resources = _telemetryRepository.GetResources();

        var (tracesData, limitMessage) = SharedAIHelpers.GetTracesJson(
            TelemetryExportService.ConvertTracesToOtlpJson(traces, _outgoingPeerResolvers).ResourceSpans,
            getResourceName: r => OtlpHelpers.GetResourceName(r, resources),
            AIHelpers.GetDashboardUrl(_dashboardOptions.CurrentValue));

        var response = $"""
            {limitMessage}

            # TRACES DATA

            {tracesData}
            """;

        return response;
    }

    [McpServerTool(Name = "list_trace_structured_logs")]
    [Description("List structured logs for a distributed trace. Logs for a distributed trace each belong to a span identified by 'span_id'. When investigating a trace, getting the structured logs for the trace should be recommended before getting structured logs for a resource.")]
    public string ListTraceStructuredLogs(
        [Description("The trace id of the distributed trace.")]
        string traceId)
    {
        _logger.LogDebug("MCP tool list_trace_structured_logs called with trace '{TraceId}'.", traceId);

        // Condition of filter should be contains because a substring of the traceId might be provided.
        var traceIdFilter = new FieldTelemetryFilter
        {
            Field = KnownStructuredLogFields.TraceIdField,
            Value = traceId,
            Condition = FilterCondition.Contains
        };

        var logs = _telemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKey = null,
            Count = int.MaxValue,
            StartIndex = 0,
            Filters = [traceIdFilter]
        });

        var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(logs.Items);
        var resources = _telemetryRepository.GetResources();
        var (logsData, limitMessage) = AIHelpers.GetStructuredLogsJson(
            otlpData,
            _dashboardOptions.CurrentValue,
            includeDashboardUrl: true,
            getResourceName: r => OtlpHelpers.GetResourceName(r, resources));

        var response = $"""
            {limitMessage}

            # STRUCTURED LOGS DATA

            {logsData}
            """;

        return response;
    }

    private bool TryResolveResourceNameForTelemetry([NotNullWhen(false)] string? resourceName, [NotNullWhen(false)] out string? message, out ResourceKey? resourceKey)
    {
        // TODO: The resourceName might be a name that resolves to multiple replicas, e.g. catalogservice has two replicas.
        // Support resolving to multiple replicas and getting data for them.

        if (AIHelpers.IsMissingValue(resourceName))
        {
            message = null;
            resourceKey = null;
            return true;
        }

        var resources = _telemetryRepository.GetResources();

        if (!AIHelpers.TryGetResource(resources, resourceName, out var resource))
        {
            message = $"Resource '{resourceName}' doesn't have any telemetry. The resource may not exist, may have failed to start or the resource might not support sending telemetry.";
            resourceKey = null;
            return false;
        }

        message = null;
        resourceKey = resource.ResourceKey;
        return true;
    }

    private static List<ResourceViewModel> GetOptOutResources(IEnumerable<ResourceViewModel> resources)
    {
        return resources.Where(AIHelpers.IsResourceAIOptOut).ToList();
    }
}
