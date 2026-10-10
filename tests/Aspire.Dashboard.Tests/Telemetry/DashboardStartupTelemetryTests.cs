// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardStartupTelemetryTests
{
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, KnownDashboardStartupFailureReasons.AddressInUse)]
    public void Record_RecordsStartupResultOnce(bool success, string? errorType)
    {
        using var fixture = new DashboardTelemetryFixture();
        var options = new DashboardOptions { LaunchContext = "AppHost" };
        var startupTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, options, startupTimestamp);

        if (success)
        {
            startupTelemetry.RecordSuccess();
            startupTelemetry.RecordFailure("ignored");
        }
        else
        {
            startupTelemetry.RecordFailure(errorType!);
            startupTelemetry.RecordSuccess();
        }

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(TelemetryEventKeys.Startup, log.Message);
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(success, attributes[TelemetryPropertyKeys.StartupSuccess]);
        Assert.Equal(KnownDashboardLaunchContexts.AppHost, attributes[TelemetryPropertyKeys.StartupLaunchContext]);
        Assert.True(Assert.IsType<double>(attributes[TelemetryPropertyKeys.StartupDurationMilliseconds]) >= 900);
        if (errorType is null)
        {
            Assert.False(attributes.ContainsKey(TelemetryPropertyKeys.ErrorType));
        }
        else
        {
            Assert.Equal(errorType, attributes[TelemetryPropertyKeys.ErrorType]);
        }
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData("AppHost", KnownDashboardLaunchContexts.AppHost)]
    [InlineData(" CLI ", KnownDashboardLaunchContexts.Cli)]
    [InlineData("Container", KnownDashboardLaunchContexts.Container)]
    [InlineData("unknown", KnownDashboardLaunchContexts.Unknown)]
    [InlineData("custom-context", KnownDashboardLaunchContexts.Unknown)]
    [InlineData("", KnownDashboardLaunchContexts.Unknown)]
    [InlineData(" ", KnownDashboardLaunchContexts.Unknown)]
    [InlineData(null, KnownDashboardLaunchContexts.Unknown)]
    public void Record_NormalizesLaunchContext(string? launchContext, string expected)
    {
        using var fixture = new DashboardTelemetryFixture();
        var options = new DashboardOptions { LaunchContext = launchContext };
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, options, Stopwatch.GetTimestamp());

        startupTelemetry.RecordSuccess();

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(expected, attributes[TelemetryPropertyKeys.StartupLaunchContext]);
    }
}
