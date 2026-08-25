// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Tests.Shared;

internal sealed class TestTelemetryRepository(ITelemetryRepository inner) : ITelemetryRepository
{
    public Func<GetLogsContext, CancellationToken, Task<PagedResult<LogSummary>>>? GetLogSummariesAsyncHandler { get; init; }

    public bool IsReadOnly => inner.IsReadOnly;
    public bool HasDisplayedMaxLogLimitMessage { get => inner.HasDisplayedMaxLogLimitMessage; set => inner.HasDisplayedMaxLogLimitMessage = value; }
    public Message? MaxLogLimitMessage { get => inner.MaxLogLimitMessage; set => inner.MaxLogLimitMessage = value; }
    public bool HasDisplayedMaxTraceLimitMessage { get => inner.HasDisplayedMaxTraceLimitMessage; set => inner.HasDisplayedMaxTraceLimitMessage = value; }
    public Message? MaxTraceLimitMessage { get => inner.MaxTraceLimitMessage; set => inner.MaxTraceLimitMessage = value; }

    public List<OtlpResource> GetResources(bool includeUninstrumentedPeers = false) => inner.GetResources(includeUninstrumentedPeers);
    public List<OtlpResource> GetResourcesByName(string name, bool includeUninstrumentedPeers = false) => inner.GetResourcesByName(name, includeUninstrumentedPeers);
    public OtlpResource? GetResourceByCompositeName(string compositeName) => inner.GetResourceByCompositeName(compositeName);
    public OtlpResource? GetResource(ResourceKey key) => inner.GetResource(key);
    public List<OtlpResource> GetResources(ResourceKey key, bool includeUninstrumentedPeers = false) => inner.GetResources(key, includeUninstrumentedPeers);
    public Dictionary<ResourceKey, int> GetResourceUnviewedErrorLogsCount() => inner.GetResourceUnviewedErrorLogsCount();
    public void MarkViewedErrorLogs(ResourceKey? key) => inner.MarkViewedErrorLogs(key);

    public Subscription OnNewResources(Func<Task> callback) => inner.OnNewResources(callback);
    public Subscription OnNewLogs(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => inner.OnNewLogs(resourceKey, subscriptionType, callback);
    public Subscription OnNewMetrics(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => inner.OnNewMetrics(resourceKey, subscriptionType, callback);
    public Subscription OnNewTraces(ResourceKey? resourceKey, SubscriptionType subscriptionType, Func<Task> callback) => inner.OnNewTraces(resourceKey, subscriptionType, callback);

    public Task<PagedResult<OtlpLogEntry>> GetLogsAsync(GetLogsContext context, CancellationToken cancellationToken) => inner.GetLogsAsync(context, cancellationToken);
    public Task<PagedResult<LogSummary>> GetLogSummariesAsync(GetLogsContext context, CancellationToken cancellationToken) =>
        GetLogSummariesAsyncHandler?.Invoke(context, cancellationToken) ?? inner.GetLogSummariesAsync(context, cancellationToken);
    public OtlpLogEntry? GetLog(long logId) => inner.GetLog(logId);
    public Task<List<OtlpLogEntry>> GetLogsForSpanAsync(string traceId, string spanId, CancellationToken cancellationToken) => inner.GetLogsForSpanAsync(traceId, spanId, cancellationToken);
    public Task<List<OtlpLogEntry>> GetLogsForTraceAsync(string traceId, CancellationToken cancellationToken) => inner.GetLogsForTraceAsync(traceId, cancellationToken);
    public Task<List<string>> GetLogPropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken) => inner.GetLogPropertyKeysAsync(resourceKey, cancellationToken);
    public Task<List<string>> GetTracePropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken) => inner.GetTracePropertyKeysAsync(resourceKey, cancellationToken);
    public Task<GetTracesResponse> GetTracesAsync(GetTracesRequest context, CancellationToken cancellationToken) => inner.GetTracesAsync(context, cancellationToken);
    public Task<GetTraceSummariesResponse> GetTraceSummariesAsync(GetTracesRequest context, CancellationToken cancellationToken) => inner.GetTraceSummariesAsync(context, cancellationToken);
    public Task<GetSpansResponse> GetSpansAsync(GetSpansRequest context, CancellationToken cancellationToken) => inner.GetSpansAsync(context, cancellationToken);
    public Task<Dictionary<string, int>> GetTraceFieldValuesAsync(string attributeName, CancellationToken cancellationToken) => inner.GetTraceFieldValuesAsync(attributeName, cancellationToken);
    public Task<Dictionary<string, int>> GetLogsFieldValuesAsync(string attributeName, CancellationToken cancellationToken) => inner.GetLogsFieldValuesAsync(attributeName, cancellationToken);
    public bool HasUpdatedTrace(OtlpTrace trace) => inner.HasUpdatedTrace(trace);
    public OtlpTrace? GetTrace(string traceId) => inner.GetTrace(traceId);
    public OtlpSpan? GetSpan(string traceId, string spanId) => inner.GetSpan(traceId, spanId);
    public OtlpResource? GetPeerResource(OtlpSpan span) => inner.GetPeerResource(span);
    public List<OtlpInstrumentSummary> GetInstrumentSummaries(ResourceKey key) => inner.GetInstrumentSummaries(key);
    public OtlpInstrumentSummary? GetInstrumentSummary(ResourceKey resourceKey, string meterName, string instrumentName) => inner.GetInstrumentSummary(resourceKey, meterName, instrumentName);
    public Task<OtlpInstrumentData?> GetInstrumentAsync(GetInstrumentRequest request, CancellationToken cancellationToken) => inner.GetInstrumentAsync(request, cancellationToken);
    public DateTime? GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName) => inner.GetInstrumentLatestEndTime(resourceKey, meterName, instrumentName);

    public IAsyncEnumerable<OtlpSpan> WatchSpansAsync(WatchSpansRequest request, CancellationToken cancellationToken) => inner.WatchSpansAsync(request, cancellationToken);
    public IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(WatchLogsRequest request, CancellationToken cancellationToken) => inner.WatchLogsAsync(request, cancellationToken);

    public void Dispose()
    {
    }
}
