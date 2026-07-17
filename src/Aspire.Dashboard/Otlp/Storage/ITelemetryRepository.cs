// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Google.Protobuf.Collections;
using Microsoft.FluentUI.AspNetCore.Components;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Provides storage and queries for dashboard telemetry.
/// </summary>
public interface ITelemetryRepository : IDisposable
{
    bool HasDisplayedMaxLogLimitMessage { get; set; }
    Message? MaxLogLimitMessage { get; set; }
    bool HasDisplayedMaxTraceLimitMessage { get; set; }
    Message? MaxTraceLimitMessage { get; set; }

    List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false);
    List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false);
    OtlpResource? GetResourceByCompositeName(string compositeName);
    OtlpResource? GetResource(ResourceKey key);
    List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false);
    Dictionary<ResourceKey, int> GetResourceUnviewedErrorLogsCount();
    void MarkViewedErrorLogs(ResourceKey? key);

    Subscription OnNewResources(Func<Task> callback);
    Subscription OnNewLogs(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback);
    Subscription OnNewMetrics(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback);
    Subscription OnNewTraces(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback);

    void AddLogs(AddContext context, RepeatedField<ResourceLogs> resourceLogs);
    void AddMetrics(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics);
    void AddTraces(AddContext context, RepeatedField<ResourceSpans> resourceSpans);

    PagedResult<OtlpLogEntry> GetLogs(GetLogsContext context);
    PagedResult<LogSummary> GetLogSummaries(GetLogsContext context);
    OtlpLogEntry? GetLog(long logId);
    List<OtlpLogEntry> GetLogsForSpan(string traceId, string spanId);
    List<OtlpLogEntry> GetLogsForTrace(string traceId);
    List<string> GetLogPropertyKeys(ResourceKey? resourceKey);
    List<string> GetTracePropertyKeys(ResourceKey? resourceKey);
    GetTracesResponse GetTraces(GetTracesRequest context);
    GetTraceSummariesResponse GetTraceSummaries(GetTracesRequest context);
    GetSpansResponse GetSpans(GetSpansRequest context);
    Dictionary<string, int> GetTraceFieldValues(string attributeName);
    Dictionary<string, int> GetLogsFieldValues(string attributeName);
    bool HasUpdatedTrace(OtlpTrace trace);
    OtlpTrace? GetTrace(string traceId);
    OtlpSpan? GetSpan(string traceId, string spanId);
    OtlpResource? GetPeerResource(OtlpSpan span);
    List<OtlpInstrumentSummary> GetInstrumentsSummaries(ResourceKey key);
    OtlpInstrumentData? GetInstrument(GetInstrumentRequest request);
    DateTime? GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName);

    IAsyncEnumerable<OtlpSpan> WatchSpansAsync(WatchSpansRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(WatchLogsRequest request, CancellationToken cancellationToken);

    void ClearSelectedSignals(Dictionary<string, HashSet<AspireDataType>> selectedResources);
    void ClearTraces(ResourceKey? resourceKey = null);
    void ClearStructuredLogs(ResourceKey? resourceKey = null);
    void ClearMetrics(ResourceKey? resourceKey = null);
}
