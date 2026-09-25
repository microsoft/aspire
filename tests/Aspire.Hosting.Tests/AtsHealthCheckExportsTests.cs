// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Ats;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using AtsHealthCheckResult = Aspire.Hosting.Ats.HealthCheckResult;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AtsHealthCheckExportsTests
{
    [Fact]
    public async Task AddHealthCheck_RegistersCallback()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        builder.AddHealthCheck("custom_check", () => Task.FromResult(new AtsHealthCheckResult
        {
            Status = HealthStatus.Degraded,
            Description = "custom description",
            Data = new Dictionary<string, string>
            {
                ["custom key"] = "custom value"
            }
        }));

        using var serviceProvider = builder.Services.BuildServiceProvider();
        var healthCheckService = serviceProvider.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync(registration => registration.Name == "custom_check");
        var entry = Assert.Single(report.Entries);

        Assert.Equal("custom_check", entry.Key);
        Assert.Equal(HealthStatus.Degraded, entry.Value.Status);
        Assert.Equal("custom description", entry.Value.Description);
        Assert.Equal("custom value", entry.Value.Data["custom key"]);
    }
}
