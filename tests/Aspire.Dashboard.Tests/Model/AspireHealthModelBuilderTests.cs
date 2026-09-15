// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.HealthModel;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class AspireHealthModelBuilderTests
{
    [Theory]
    [InlineData(KnownResourceState.Running, HealthState.Healthy)]
    [InlineData(KnownResourceState.Finished, HealthState.Healthy)]
    [InlineData(KnownResourceState.FailedToStart, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.Exited, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.RuntimeUnhealthy, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.ValueMissing, HealthState.Unhealthy)]
    [InlineData(KnownResourceState.Starting, HealthState.Unknown)]
    [InlineData(KnownResourceState.Waiting, HealthState.Unknown)]
    [InlineData(KnownResourceState.Stopping, HealthState.Unknown)]
    public void MapResourceState_MapsLifecycleStates(KnownResourceState state, HealthState expected)
    {
        Assert.Equal(expected, AspireHealthModelBuilder.MapResourceState(state));
    }

    [Fact]
    public void MapResourceState_NullState_IsUnknown()
    {
        Assert.Equal(HealthState.Unknown, AspireHealthModelBuilder.MapResourceState(null));
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, HealthState.Healthy)]
    [InlineData(HealthStatus.Degraded, HealthState.Degraded)]
    [InlineData(HealthStatus.Unhealthy, HealthState.Unhealthy)]
    public void MapHealthStatus_MapsHealthCheckResults(HealthStatus status, HealthState expected)
    {
        Assert.Equal(expected, AspireHealthModelBuilder.MapHealthStatus(status));
    }
}
