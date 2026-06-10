// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Tests.Health;

[Trait("Partition", "3")]
public class HealthStatusTests
{
    private const string StartingState = "Starting";
    private const string RunningState = "Running";

    [Theory]
    [InlineData(StartingState, null, null)]
    [InlineData(StartingState, null, new string[]{})]
    [InlineData(StartingState, null, new string?[]{null})]
    // we don't have a Running + HealthReports null case because that's not a valid state - by this point, we will have received the list of HealthReports
    [InlineData(RunningState, HealthStatus.Healthy, new string[]{})]
    [InlineData(RunningState, HealthStatus.Healthy, new string?[] {"Healthy"})]
    [InlineData(RunningState, HealthStatus.Unhealthy, new string?[] {null})]
    [InlineData(RunningState, HealthStatus.Degraded, new string?[] {"Healthy", "Degraded"})]
    public void Resource_WithHealthReportAndState_ReturnsCorrectHealthStatus(string? state, HealthStatus? expectedStatus, string?[]? healthStatusStrings)
    {
        var reports = healthStatusStrings?.Select<string?, HealthReportSnapshot>((h, i) => new HealthReportSnapshot(i.ToString(), h is null ? null : Enum.Parse<HealthStatus>(h), null, null)).ToImmutableArray() ?? [];
        var actualStatus = CustomResourceSnapshot.ComputeHealthStatus(reports, state);
        Assert.Equal(expectedStatus, actualStatus);
    }
}
