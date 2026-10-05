// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;

namespace Aspire.Dashboard.Telemetry;

/// <summary>
/// Captures dashboard product telemetry enablement independently of provider lifetime.
/// </summary>
public sealed record DashboardTelemetryConfiguration
{
    /// <summary>
    /// Gets whether dashboard usage and error reporting is enabled.
    /// </summary>
    public bool ReportedTelemetryEnabled { get; init; }

    /// <summary>
    /// Resolves direct and AppHost-forwarded telemetry opt-out settings.
    /// </summary>
    /// <param name="configuration">The dashboard configuration.</param>
    /// <param name="options">The dashboard options containing the forwarded opt-out.</param>
    /// <returns>The resolved product telemetry settings.</returns>
    public static DashboardTelemetryConfiguration Create(IConfiguration configuration, DashboardOptions options)
    {
        // The AppHost still forwards consent under DebugSession, while standalone and
        // deployed dashboards can use the product environment variable directly.
        return new()
        {
            ReportedTelemetryEnabled = !configuration.GetBool(DashboardTelemetryService.TelemetryOptOutConfigKey, defaultValue: false) &&
                options.DebugSession.TelemetryOptOut is not true
        };
    }
}
