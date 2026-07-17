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
/// Forwards telemetry operations to the repository for the selected dashboard run.
/// </summary>
internal sealed class SelectedTelemetryRepository(DashboardDataSource dataSource) : ITelemetryRepository
{
    private ITelemetryRepository Repository => dataSource.TelemetryRepository;

    public bool HasDisplayedMaxLogLimitMessage
    {
        get => Repository.HasDisplayedMaxLogLimitMessage;
        set => Repository.HasDisplayedMaxLogLimitMessage = value;
    }

    public Message? MaxLogLimitMessage
    {
        get => Repository.MaxLogLimitMessage;
        set => Repository.MaxLogLimitMessage = value;
    }

    public bool HasDisplayedMaxTraceLimitMessage
    {
        get => Repository.HasDisplayedMaxTraceLimitMessage;
        set => Repository.HasDisplayedMaxTraceLimitMessage = value;
    }

    public Message? MaxTraceLimitMessage
    {
        get => Repository.MaxTraceLimitMessage;
        set => Repository.MaxTraceLimitMessage = value;
    }

    public List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false) => Repository.GetResources(includeUninstrumentedPeers);
    public List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false) => Repository.GetResourcesByName(name, includeUninstrumentedPeers);
    public OtlpResource? GetResourceByCompositeName(string compositeName) => Repository.GetResourceByCompositeName(compositeName);
    public OtlpResource? GetResource(ResourceKey key) => Repository.GetResource(key);
    public List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false) => Repository.GetResources(key, includeUninstrumentedPeers);
    public Dictionary<ResourceKey, int> GetResourceUnviewedErrorLogsCount() => Repository.GetResourceUnviewedErrorLogsCount();
    public void MarkViewedErrorLogs(ResourceKey? key) => Repository.MarkViewedErrorLogs(key);
    public Subscription OnNewResources(Func<Task> callback) => Repository.OnNewResources(callback);
    public Subscription OnNewLogs(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => Repository.OnNewLogs(resourceKey, subscriptionType, callback);
    public Subscription OnNewMetrics(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => Repository.OnNewMetrics(resourceKey, subscriptionType, callback);
    public Subscription OnNewTraces(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => Repository.OnNewTraces(resourceKey, subscriptionType, callback);
    public void AddLogs(AddContext context, RepeatedField<ResourceLogs> resourceLogs) => Repository.AddLogs(context, resourceLogs);
    public void AddMetrics(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics) => Repository.AddMetrics(context, resourceMetrics);
    public void AddTraces(AddContext context, RepeatedField<ResourceSpans> resourceSpans) => Repository.AddTraces(context, resourceSpans);
    public PagedResult<OtlpLogEntry> GetLogs(GetLogsContext context) => Repository.GetLogs(context);
    public PagedResult<LogSummary> GetLogSummaries(GetLogsContext context) => Repository.GetLogSummaries(context);
    public OtlpLogEntry? GetLog(long logId) => Repository.GetLog(logId);
    public List<OtlpLogEntry> GetLogsForSpan(string traceId, string spanId) => Repository.GetLogsForSpan(traceId, spanId);
    public List<OtlpLogEntry> GetLogsForTrace(string traceId) => Repository.GetLogsForTrace(traceId);
    public List<string> GetLogPropertyKeys(ResourceKey? resourceKey) => Repository.GetLogPropertyKeys(resourceKey);
    public List<string> GetTracePropertyKeys(ResourceKey? resourceKey) => Repository.GetTracePropertyKeys(resourceKey);
    public GetTracesResponse GetTraces(GetTracesRequest context) => Repository.GetTraces(context);
    public GetTraceSummariesResponse GetTraceSummaries(GetTracesRequest context) => Repository.GetTraceSummaries(context);
    public GetSpansResponse GetSpans(GetSpansRequest context) => Repository.GetSpans(context);
    public Dictionary<string, int> GetTraceFieldValues(string attributeName) => Repository.GetTraceFieldValues(attributeName);
    public Dictionary<string, int> GetLogsFieldValues(string attributeName) => Repository.GetLogsFieldValues(attributeName);
    public bool HasUpdatedTrace(OtlpTrace trace) => Repository.HasUpdatedTrace(trace);
    public OtlpTrace? GetTrace(string traceId) => Repository.GetTrace(traceId);
    public OtlpSpan? GetSpan(string traceId, string spanId) => Repository.GetSpan(traceId, spanId);
    public OtlpResource? GetPeerResource(OtlpSpan span) => Repository.GetPeerResource(span);
    public List<OtlpInstrumentSummary> GetInstrumentsSummaries(ResourceKey key) => Repository.GetInstrumentsSummaries(key);
    public OtlpInstrumentData? GetInstrument(GetInstrumentRequest request) => Repository.GetInstrument(request);
    public DateTime? GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName) => Repository.GetInstrumentLatestEndTime(resourceKey, meterName, instrumentName);
    public IAsyncEnumerable<OtlpSpan> WatchSpansAsync(WatchSpansRequest request, CancellationToken cancellationToken) => Repository.WatchSpansAsync(request, cancellationToken);
    public IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(WatchLogsRequest request, CancellationToken cancellationToken) => Repository.WatchLogsAsync(request, cancellationToken);
    public void ClearSelectedSignals(Dictionary<string, HashSet<AspireDataType>> selectedResources) => Repository.ClearSelectedSignals(selectedResources);
    public void ClearTraces(ResourceKey? resourceKey = null) => Repository.ClearTraces(resourceKey);
    public void ClearStructuredLogs(ResourceKey? resourceKey = null) => Repository.ClearStructuredLogs(resourceKey);
    public void ClearMetrics(ResourceKey? resourceKey = null) => Repository.ClearMetrics(resourceKey);

    public void Dispose()
    {
        // DashboardDataSource owns the selected repository and disposes historical instances when selection changes.
    }
}