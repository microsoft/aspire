// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Aspire.Dashboard.ServiceClient;

// Select the writable run at the batch boundary, serialized with run rollover.
internal sealed class CurrentTelemetryWriter(DashboardDataSourcePool pool) : ITelemetryRepositoryWriter
{
    public async Task AddLogsAsync(AddContext context, RepeatedField<ResourceLogs> resourceLogs)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).AddLogsAsync(context, resourceLogs).ConfigureAwait(false);
        }
    }

    public async Task AddMetricsAsync(AddContext context, RepeatedField<ResourceMetrics> resourceMetrics)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).AddMetricsAsync(context, resourceMetrics).ConfigureAwait(false);
        }
    }

    public async Task AddTracesAsync(AddContext context, RepeatedField<ResourceSpans> resourceSpans)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).AddTracesAsync(context, resourceSpans).ConfigureAwait(false);
        }
    }

    public async Task ClearSelectedSignalsAsync(Dictionary<string, HashSet<AspireDataType>> selectedResources)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).ClearSelectedSignalsAsync(selectedResources).ConfigureAwait(false);
        }
    }

    public async Task ClearTracesAsync(ResourceKey? resourceKey = null)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).ClearTracesAsync(resourceKey).ConfigureAwait(false);
        }
    }

    public async Task ClearStructuredLogsAsync(ResourceKey? resourceKey = null)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).ClearStructuredLogsAsync(resourceKey).ConfigureAwait(false);
        }
    }

    public async Task ClearMetricsAsync(ResourceKey? resourceKey = null)
    {
        using (await pool.RunWriteLock.LockAsync().ConfigureAwait(false))
        {
            await ((ITelemetryRepositoryWriter)pool.Current.TelemetryRepository).ClearMetricsAsync(resourceKey).ConfigureAwait(false);
        }
    }

}
