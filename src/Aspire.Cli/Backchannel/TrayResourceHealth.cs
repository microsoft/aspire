// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Aggregates visible resource state and health using the dashboard's lifetime conventions.
/// </summary>
internal static class TrayResourceHealth
{
    public static string? Aggregate(IEnumerable<ResourceSnapshot> resources)
    {
        string? aggregate = null;
        foreach (var resource in resources)
        {
            if (ResourceSnapshotMapper.IsHiddenResource(resource))
            {
                continue;
            }

            var health = GetHealth(resource);
            if (health == "unhealthy")
            {
                return health;
            }
            if (health == "warning" || aggregate is null)
            {
                aggregate = health;
            }
        }
        return aggregate;
    }

    private static string? GetHealth(ResourceSnapshot resource)
    {
        var completed = IsState(resource, KnownResourceStates.Finished) || IsState(resource, KnownResourceStates.Exited);
        var pendingCheck = resource.HealthReports.Any(report => report.Status is null);
        if (IsState(resource, KnownResourceStates.FailedToStart)
            || IsState(resource, KnownResourceStates.RuntimeUnhealthy)
            || (completed && resource.ExitCode is not null and not 0)
            || string.Equals(resource.StateStyle, "error", StringComparisons.ResourceState)
            || resource.HealthReports.Any(report => IsHealth(report.Status, "Unhealthy"))
            || (IsHealth(resource.HealthStatus, "Unhealthy") && !pendingCheck))
        {
            return "unhealthy";
        }

        // The AppHost reports aggregate Unhealthy until the first registered check returns.
        // For example: healthStatus: "Unhealthy", healthReports: [{ name: "ready", status: null }].
        // An actual failing report still takes precedence over another check that is pending.
        if (pendingCheck
            || string.Equals(resource.StateStyle, "warning", StringComparisons.ResourceState)
            || IsHealth(resource.HealthStatus, "Degraded")
            || resource.HealthReports.Any(report => IsHealth(report.Status, "Degraded")))
        {
            return "warning";
        }

        // The dashboard treats successful jobs and resources without a lifetime (such as
        // resolved parameters) as neutral, not services waiting to become Running.
        if (completed || ((string.IsNullOrEmpty(resource.State) || IsState(resource, KnownResourceStates.Active))
            && resource.HealthStatus is null && resource.HealthReports.Length == 0))
        {
            return null;
        }

        if (!IsState(resource, KnownResourceStates.Running))
        {
            return "warning";
        }

        // As in CustomResourceSnapshot.ComputeHealthStatus, Running with no registered
        // checks is healthy. Do not extend that assumption to pending or unknown reports.
        return (resource.HealthStatus is null || IsHealth(resource.HealthStatus, "Healthy"))
            && resource.HealthReports.All(report => IsHealth(report.Status, "Healthy"))
            ? "healthy"
            : "warning";
    }

    private static bool IsState(ResourceSnapshot resource, string state)
        => string.Equals(resource.State, state, StringComparisons.ResourceState);

    private static bool IsHealth(string? status, string expected)
        => string.Equals(status, expected, StringComparison.OrdinalIgnoreCase);
}
