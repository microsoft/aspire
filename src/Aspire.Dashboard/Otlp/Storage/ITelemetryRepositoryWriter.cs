// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.Otlp.Storage;

/// <summary>
/// Adds and removes telemetry in the writable dashboard telemetry store.
/// </summary>
public interface ITelemetryRepositoryWriter
{
    /// <summary>
    /// Asynchronously adds log records to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceLogs">The resource log records to add.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AddLogsAsync(AddContext context, RepeatedField<ResourceLogs> resourceLogs);

    /// <summary>
    /// Asynchronously adds metric data to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceMetrics">The resource metric data to add.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AddMetricsAsync(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics);

    /// <summary>
    /// Asynchronously adds spans to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceSpans">The resource spans to add.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AddTracesAsync(AddContext context, RepeatedField<ResourceSpans> resourceSpans);

    /// <summary>
    /// Asynchronously removes the selected telemetry signals for each resource.
    /// </summary>
    /// <param name="selectedResources">The telemetry signal types selected for removal, keyed by resource name.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearSelectedSignalsAsync(Dictionary<string, HashSet<AspireDataType>> selectedResources);

    /// <summary>
    /// Asynchronously removes traces, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearTracesAsync(ResourceKey? resourceKey = null);

    /// <summary>
    /// Asynchronously removes structured logs, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearStructuredLogsAsync(ResourceKey? resourceKey = null);

    /// <summary>
    /// Asynchronously removes metrics, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearMetricsAsync(ResourceKey? resourceKey = null);
}