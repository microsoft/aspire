// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Provides storage and queries for dashboard telemetry.
/// </summary>
public interface ITelemetryRepository : IDisposable
{
    /// <summary>
    /// Gets a value indicating whether the repository is read-only.
    /// </summary>
    bool IsReadOnly { get; }

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

    Task<PagedResult<OtlpLogEntry>> GetLogsAsync(GetLogsContext context, CancellationToken cancellationToken);
    Task<PagedResult<LogSummary>> GetLogSummariesAsync(GetLogsContext context, CancellationToken cancellationToken);
    OtlpLogEntry? GetLog(long logId);
    Task<List<OtlpLogEntry>> GetLogsForSpanAsync(string traceId, string spanId, CancellationToken cancellationToken);
    Task<List<OtlpLogEntry>> GetLogsForTraceAsync(string traceId, CancellationToken cancellationToken);
    Task<List<string>> GetLogPropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken);
    Task<List<string>> GetTracePropertyKeysAsync(ResourceKey? resourceKey, CancellationToken cancellationToken);
    Task<GetTracesResponse> GetTracesAsync(GetTracesRequest context, CancellationToken cancellationToken);
    Task<GetTraceSummariesResponse> GetTraceSummariesAsync(GetTracesRequest context, CancellationToken cancellationToken);
    Task<GetSpansResponse> GetSpansAsync(GetSpansRequest context, CancellationToken cancellationToken);
    Task<Dictionary<string, int>> GetTraceFieldValuesAsync(string attributeName, CancellationToken cancellationToken);
    Task<Dictionary<string, int>> GetLogsFieldValuesAsync(string attributeName, CancellationToken cancellationToken);
    bool HasUpdatedTrace(OtlpTrace trace);
    OtlpTrace? GetTrace(string traceId);
    OtlpSpan? GetSpan(string traceId, string spanId);
    OtlpResource? GetPeerResource(OtlpSpan span);
    List<OtlpInstrumentSummary> GetInstrumentSummaries(ResourceKey key);

    /// <summary>
    /// Gets the summary for an instrument emitted by a resource.
    /// </summary>
    /// <param name="resourceKey">The resource that emitted the instrument.</param>
    /// <param name="meterName">The name of the meter that contains the instrument.</param>
    /// <param name="instrumentName">The name of the instrument.</param>
    /// <returns>The instrument summary, or <see langword="null"/> when the instrument is not found.</returns>
    OtlpInstrumentSummary? GetInstrumentSummary(ResourceKey resourceKey, string meterName, string instrumentName);

    Task<OtlpInstrumentData?> GetInstrumentAsync(GetInstrumentRequest request, CancellationToken cancellationToken);
    DateTime? GetInstrumentLatestEndTime(ResourceKey resourceKey, string meterName, string instrumentName);

    IAsyncEnumerable<OtlpSpan> WatchSpansAsync(WatchSpansRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<OtlpLogEntry> WatchLogsAsync(WatchLogsRequest request, CancellationToken cancellationToken);

}
