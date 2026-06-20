// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Dashboard;

[Trait("Partition", "3")]
public class DashboardOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OtlpGrpcEndpoint_IsNull_WhenMissingOrBlank(string? otlpEndpoint)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "ASPNETCORE_ENVIRONMENT", "Development" },
            { KnownConfigNames.AspNetCoreUrls, "https://localhost:8080" },
            { KnownConfigNames.DashboardOtlpGrpcEndpointUrl, otlpEndpoint },
            { KnownConfigNames.DashboardOtlpHttpEndpointUrl, null }
        });

        using var app = builder.Build();
        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;

        Assert.Null(dashboardOptions.OtlpGrpcEndpointUrl);
    }

    [Fact]
    public void OtlpGrpcEndpoint_DoesNotDefault_WhenOtlpHttpEndpointConfigured()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { KnownConfigNames.AspNetCoreUrls, "https://localhost:8080" },
            { KnownConfigNames.DashboardOtlpGrpcEndpointUrl, null },
            { KnownConfigNames.DashboardOtlpHttpEndpointUrl, "https://localhost:4318" }
        });

        using var app = builder.Build();
        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;

        Assert.Null(dashboardOptions.OtlpGrpcEndpointUrl);
        Assert.Equal("https://localhost:4318", dashboardOptions.OtlpHttpEndpointUrl);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("", null)]
    [InlineData("purplemonkeydishwasher", null)]
    [InlineData(null, null)]
    public void TelemetryOptOut_ConfiguredCorrectly(string? configurationValue, bool? expectedValue)
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            { "ASPIRE_DASHBOARD_TELEMETRY_OPTOUT", configurationValue },
            { "ASPNETCORE_ENVIRONMENT", "Development" },
            { KnownConfigNames.AspNetCoreUrls, "http://localhost:8080" },
            { KnownConfigNames.DashboardOtlpGrpcEndpointUrl, "http://localhost:4317" }
        });

        using var app = builder.Build();
        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.Equal(expectedValue, dashboardOptions.TelemetryOptOut);
    }
}
