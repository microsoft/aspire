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
    /// Adds log records to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceLogs">The resource log records to add.</param>
    void AddLogs(AddContext context, RepeatedField<ResourceLogs> resourceLogs);

    /// <summary>
    /// Adds metric data to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceMetrics">The resource metric data to add.</param>
    void AddMetrics(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics);

    /// <summary>
    /// Adds spans to the telemetry store.
    /// </summary>
    /// <param name="context">The context that records the result of adding telemetry.</param>
    /// <param name="resourceSpans">The resource spans to add.</param>
    void AddTraces(AddContext context, RepeatedField<ResourceSpans> resourceSpans);

    /// <summary>
    /// Removes the selected telemetry signals for each resource.
    /// </summary>
    /// <param name="selectedResources">The telemetry signal types selected for removal, keyed by resource name.</param>
    void ClearSelectedSignals(Dictionary<string, HashSet<AspireDataType>> selectedResources);

    /// <summary>
    /// Removes traces, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    void ClearTraces(ResourceKey? resourceKey = null);

    /// <summary>
    /// Removes structured logs, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    void ClearStructuredLogs(ResourceKey? resourceKey = null);

    /// <summary>
    /// Removes metrics, optionally limited to a resource.
    /// </summary>
    /// <param name="resourceKey">The resource to clear, or <see langword="null"/> to clear all resources.</param>
    void ClearMetrics(ResourceKey? resourceKey = null);
}