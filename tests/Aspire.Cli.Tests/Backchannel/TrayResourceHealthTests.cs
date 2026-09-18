// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Tests.Backchannel;

public class TrayResourceHealthTests
{
    [Theory]
    [InlineData("Running", "Healthy", null, null, "healthy")]
    [InlineData("Running", null, null, null, "healthy")]
    [InlineData("Running", "Degraded", null, null, "warning")]
    [InlineData("Running", "Unhealthy", null, null, "unhealthy")]
    [InlineData("Starting", null, null, null, "warning")]
    [InlineData("Building", null, null, null, "warning")]
    [InlineData("Waiting", null, null, null, "warning")]
    [InlineData("Stopping", null, null, null, "warning")]
    [InlineData("NotStarted", null, null, null, "warning")]
    [InlineData("ValueMissing", null, null, null, "warning")]
    [InlineData("Unknown", null, null, null, "warning")]
    [InlineData("FailedToStart", null, null, null, "unhealthy")]
    [InlineData("RuntimeUnhealthy", null, null, null, "unhealthy")]
    [InlineData("Finished", null, null, 0, null)]
    [InlineData("Exited", null, null, 0, null)]
    [InlineData("Finished", null, null, 1, "unhealthy")]
    [InlineData("Exited", null, null, 1, "unhealthy")]
    [InlineData("CustomState", null, "warning", null, "warning")]
    [InlineData("CustomState", null, "error", null, "unhealthy")]
    [InlineData("Active", null, null, null, null)]
    [InlineData(null, null, null, null, null)]
    public void ResourceStateAndHealthDetermineAggregate(string? state, string? health, string? style, int? exitCode, string? expected)
    {
        var resource = new ResourceSnapshot
        {
            Name = "resource",
            State = state,
            StateStyle = style,
            HealthStatus = health,
            ExitCode = exitCode
        };

        Assert.Equal(expected, TrayResourceHealth.Aggregate([resource]));
    }

    [Theory]
    [InlineData(null, "warning")]
    [InlineData("Healthy", "warning")]
    [InlineData("Degraded", "warning")]
    [InlineData("Unhealthy", "unhealthy")]
    public void PendingHealthCheckIsWaitingButDoesNotHideAFailedCheck(string? otherStatus, string expected)
    {
        var resource = new ResourceSnapshot
        {
            Name = "api",
            State = "Running",
            HealthStatus = "Unhealthy",
            HealthReports =
            [
                new() { Name = "pending", Status = null },
                new() { Name = "other", Status = otherStatus }
            ]
        };

        Assert.Equal(expected, TrayResourceHealth.Aggregate([resource]));
    }

    [Fact]
    public void HiddenAndNonApplicableResourcesDoNotMaskVisibleHealth()
    {
        ResourceSnapshot[] resources =
        [
            new() { Name = "hidden-state", State = "Hidden", HealthStatus = "Unhealthy" },
            new() { Name = "hidden-flag", State = "FailedToStart", IsHidden = true },
            new() { Name = "parameter", ResourceType = "Parameter", State = "Active" },
            new() { Name = "connection", ResourceType = "ConnectionString" },
            new() { Name = "setup", State = "Finished", ExitCode = 0 }
        ];
        Assert.Null(TrayResourceHealth.Aggregate([]));
        Assert.Null(TrayResourceHealth.Aggregate(resources));
        Assert.Equal("healthy", TrayResourceHealth.Aggregate(
            [.. resources, new() { Name = "api", State = "Running", HealthStatus = "Healthy" }]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MostSevereVisibleResourceWinsRegardlessOfOrdering(bool reverse)
    {
        ResourceSnapshot[] resources =
        [
            new() { Name = "api", State = "Running", HealthStatus = "Healthy" },
            new() { Name = "worker", State = "Waiting" },
            new() { Name = "database", State = "Running", HealthStatus = "Unhealthy" }
        ];

        Assert.Equal("warning", TrayResourceHealth.Aggregate(resources.Take(2)));
        Assert.Equal("unhealthy", TrayResourceHealth.Aggregate(reverse ? resources.Reverse() : resources));
    }
}
