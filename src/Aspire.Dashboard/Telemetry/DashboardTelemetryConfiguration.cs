// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;

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
    /// <returns>The resolved product telemetry settings.</returns>
    public static DashboardTelemetryConfiguration Create(IConfiguration configuration)
    {
        // Retain the AppHost's legacy opt-out key without keeping the obsolete IDE transport options.
        var forwardedOptOut = configuration.GetValue<bool?>(DashboardConfigNames.Legacy.DebugSessionTelemetryOptOutName.ConfigKey);

        return new()
        {
            ReportedTelemetryEnabled = !configuration.GetBool(DashboardTelemetryService.TelemetryOptOutConfigKey, defaultValue: false) &&
                forwardedOptOut is not true
        };
    }
}
