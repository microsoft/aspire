// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        var startupTelemetry = new DashboardStartupTelemetry(fixture.Telemetry, configuration);

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
        Assert.True(Assert.IsType<double>(attributes[TelemetryPropertyKeys.StartupDurationMilliseconds]) >= 0);
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
}
