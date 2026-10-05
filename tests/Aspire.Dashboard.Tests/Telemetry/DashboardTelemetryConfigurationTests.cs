// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetryConfigurationTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData("false", null, true)]
    [InlineData("false", false, true)]
    [InlineData("false", true, false)]
    [InlineData("true", null, false)]
    [InlineData("true", false, false)]
    [InlineData("true", true, false)]
    [InlineData("1", false, false)]
    [InlineData("0", false, true)]
    [InlineData("0", true, false)]
    public void Create_ResolvesDirectAndForwardedOptOut(string? directOptOut, bool? forwardedOptOut, bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DashboardTelemetryService.TelemetryOptOutConfigKey] = directOptOut
        }).Build();
        var options = new DashboardOptions
        {
            DebugSession = new DebugSessionOptions { TelemetryOptOut = forwardedOptOut }
        };

        var settings = DashboardTelemetryConfiguration.Create(configuration, options);

        Assert.Equal(enabled, settings.ReportedTelemetryEnabled);
    }

    [Fact]
    public void Create_ResolvesSettingsOnce()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var options = new DashboardOptions();
        var settings = DashboardTelemetryConfiguration.Create(configuration, options);

        configuration[DashboardTelemetryService.TelemetryOptOutConfigKey] = "true";
        options.DebugSession.TelemetryOptOut = true;

        Assert.True(settings.ReportedTelemetryEnabled);
        Assert.False(DashboardTelemetryConfiguration.Create(configuration, options).ReportedTelemetryEnabled);
    }
}
