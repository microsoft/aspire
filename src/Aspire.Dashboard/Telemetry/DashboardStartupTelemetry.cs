// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Configuration;
using Aspire.Shared;

namespace Aspire.Dashboard.Telemetry;

internal sealed class DashboardStartupTelemetry
{
    private readonly DashboardTelemetryService _telemetry;
    private readonly string _launchContext;
    private readonly long _startTimestamp;
    private int _recorded;

    public DashboardStartupTelemetry(DashboardTelemetryService telemetry, DashboardOptions options, long startTimestamp)
    {
        _telemetry = telemetry;
        _startTimestamp = startTimestamp;
        _launchContext = options.LaunchContext?.Trim().ToLowerInvariant() switch
        {
            KnownDashboardLaunchContexts.AppHost => KnownDashboardLaunchContexts.AppHost,
            KnownDashboardLaunchContexts.Cli => KnownDashboardLaunchContexts.Cli,
            KnownDashboardLaunchContexts.Container => KnownDashboardLaunchContexts.Container,
            _ => KnownDashboardLaunchContexts.Unknown
        };
    }

    public void RecordSuccess() => Record(success: true, errorType: null);

    public void RecordFailure(string errorType) => Record(success: false, errorType);

    private void Record(bool success, string? errorType)
    {
        if (Interlocked.Exchange(ref _recorded, 1) != 0)
        {
            return;
        }

        Dictionary<string, AspireTelemetryProperty> properties = new()
        {
            [TelemetryPropertyKeys.StartupSuccess] = new(success),
            [TelemetryPropertyKeys.StartupLaunchContext] = new(_launchContext),
            [TelemetryPropertyKeys.StartupDurationMilliseconds] = new(
                Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds,
                AspireTelemetryPropertyType.Metric)
        };
        if (errorType is not null)
        {
            properties[TelemetryPropertyKeys.ErrorType] = new(errorType);
        }

        _telemetry.RecordEvent(TelemetryEventKeys.Startup, properties);
    }
}
