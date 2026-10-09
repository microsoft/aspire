// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Telemetry;
using Aspire.Hosting;
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
            [DashboardTelemetryService.TelemetryOptOutConfigKey] = directOptOut,
            [DashboardConfigNames.Legacy.DebugSessionTelemetryOptOutName.ConfigKey] = forwardedOptOut?.ToString()
        }).Build();

        var settings = DashboardTelemetryConfiguration.Create(configuration);

        Assert.Equal(enabled, settings.ReportedTelemetryEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    public void Create_InvalidLegacyOptOut_Throws(string? directOptOut)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DashboardTelemetryService.TelemetryOptOutConfigKey] = directOptOut,
            [DashboardConfigNames.Legacy.DebugSessionTelemetryOptOutName.ConfigKey] = "invalid"
        }).Build();

        Assert.Throws<InvalidOperationException>(() => DashboardTelemetryConfiguration.Create(configuration));
    }
}
