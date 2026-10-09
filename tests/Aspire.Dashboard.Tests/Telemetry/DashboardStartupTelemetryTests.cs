// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DashboardConfigNames.DashboardLaunchContextName.ConfigKey] = "AppHost"
            })
            .Build();
        var startupTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, configuration, startupTimestamp);

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
    [InlineData("Container", KnownDashboardLaunchContexts.Container)]
    [InlineData("custom-context", KnownDashboardLaunchContexts.Unknown)]
    [InlineData("", KnownDashboardLaunchContexts.Unknown)]
    [InlineData(null, KnownDashboardLaunchContexts.Unknown)]
    public void Record_NormalizesLaunchContext(string? launchContext, string expected)
    {
        using var fixture = new DashboardTelemetryFixture();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DashboardConfigNames.DashboardLaunchContextName.ConfigKey] = launchContext
            })
            .Build();
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, configuration, Stopwatch.GetTimestamp());

        startupTelemetry.RecordSuccess();

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        var attributes = log.Attributes.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(expected, attributes[TelemetryPropertyKeys.StartupLaunchContext]);
    }
}
