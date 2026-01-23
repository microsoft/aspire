// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Tests.Health;

public class FriendlyHealthCheckErrorMessagesTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task StaticUriHealthCheck_ReturnsTimeoutMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        // Add an external service with a health check to a URL that will timeout
        // Using a non-routable IP (TEST-NET-1) to simulate timeout
        var externalService = builder.AddExternalService("test", "http://192.0.2.1/")
            .WithHttpHealthCheck();

        using var app = builder.Build();

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();

        // Get the health check key
        Assert.True(externalService.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var healthCheckAnnotations));
        var healthCheckKey = healthCheckAnnotations.First(hc => hc.Key.StartsWith($"{externalService.Resource.Name}_external")).Key;

        // Run the health check with a short timeout
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            cts.Token);

        // Verify we got the unhealthy result for our health check
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        // The health check should be unhealthy
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);

        // The description should contain a friendly message about timeout or connection failure
        Assert.NotNull(entry.Description);
        Assert.True(
            entry.Description.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase),
            $"Expected friendly error message, but got: {entry.Description}");
    }

    [Fact]
    public async Task ParameterUriHealthCheck_ReturnsTimeoutMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["Parameters:test-url"] = "http://192.0.2.1/";

        var urlParam = builder.AddParameter("test-url");
        var externalService = builder.AddExternalService("test", urlParam)
            .WithHttpHealthCheck();

        using var app = builder.Build();

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();

        // Get the health check key
        Assert.True(externalService.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var healthCheckAnnotations));
        var healthCheckKey = healthCheckAnnotations.First(hc => hc.Key.StartsWith($"{externalService.Resource.Name}_external")).Key;

        // Run the health check with a short timeout
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            cts.Token);

        // Verify we got the unhealthy result for our health check
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        // The health check should be unhealthy
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);

        // The description should contain a friendly message
        Assert.NotNull(entry.Description);
        Assert.True(
            entry.Description.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase),
            $"Expected friendly error message, but got: {entry.Description}");
    }

    [Fact]
    public async Task ParameterUriHealthCheck_InvalidUrlReturnsMessage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration["Parameters:test-url"] = "invalid-url";

        var urlParam = builder.AddParameter("test-url");
        var externalService = builder.AddExternalService("test", urlParam)
            .WithHttpHealthCheck();

        using var app = builder.Build();

        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();

        // Get the health check key
        Assert.True(externalService.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var healthCheckAnnotations));
        var healthCheckKey = healthCheckAnnotations.First(hc => hc.Key.StartsWith($"{externalService.Resource.Name}_external")).Key;

        // Run the health check
        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            CancellationToken.None).DefaultTimeout();

        // Verify we got the unhealthy result for our health check
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        // The health check should be unhealthy
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);

        // The description should contain a friendly message about invalid URL
        Assert.NotNull(entry.Description);
        Assert.Contains("invalid", entry.Description, StringComparison.OrdinalIgnoreCase);
    }
}
