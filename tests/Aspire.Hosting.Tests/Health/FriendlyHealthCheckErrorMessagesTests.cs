// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Tests.Health;

[Trait("Partition", "6")]
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
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            cts.Token).DefaultTimeout();

        // Verify we got the unhealthy result for our health check
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        // The health check should be unhealthy
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);

        // The description should contain a friendly message about timeout or connection failure
        Assert.NotNull(entry.Description);
        Assert.True(
            entry.Description.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("was canceled", StringComparison.OrdinalIgnoreCase),
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
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            cts.Token).DefaultTimeout();

        // Verify we got the unhealthy result for our health check
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        // The health check should be unhealthy
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);

        // The description should contain a friendly message
        Assert.NotNull(entry.Description);
        Assert.True(
            entry.Description.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains("was canceled", StringComparison.OrdinalIgnoreCase),
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

    [Fact]
    public async Task StaticUriHealthCheck_ReturnsStatusCodeMessage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = WriteResponseAsync(listener, HttpStatusCode.NotFound, serverCts.Token);

        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var externalService = builder.AddExternalService("test", endpoint.ToString())
            .WithHttpHealthCheck(statusCode: 200);

        using var app = builder.Build();
        var healthCheckService = app.Services.GetRequiredService<HealthCheckService>();

        Assert.True(externalService.Resource.TryGetAnnotationsOfType<HealthCheckAnnotation>(out var healthCheckAnnotations));
        var healthCheckKey = healthCheckAnnotations.First(hc => hc.Key.StartsWith($"{externalService.Resource.Name}_external")).Key;

        var result = await healthCheckService.CheckHealthAsync(
            registration => registration.Name == healthCheckKey,
            CancellationToken.None).DefaultTimeout();
        Assert.Contains(healthCheckKey, result.Entries.Keys);
        var entry = result.Entries[healthCheckKey];

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.Equal($"Request to {endpoint} returned 404 NotFound", entry.Description);
        await serverTask.DefaultTimeout();
    }

    [Fact]
    public void GetFriendlyErrorMessage_StripsCredentialsFromUri()
    {
        var uri = new Uri("http://user:secret@example.com:8080/");
        var exception = new HttpRequestException();

        var message = HttpHealthCheckHelpers.GetFriendlyErrorMessage(uri, exception, CancellationToken.None);

        Assert.DoesNotContain("user", message);
        Assert.DoesNotContain("secret", message);
        Assert.Contains("example.com", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_PreservesUriWithoutCredentials()
    {
        var uri = new Uri("http://example.com:8080/");
        var exception = new HttpRequestException();

        var message = HttpHealthCheckHelpers.GetFriendlyErrorMessage(uri, exception, CancellationToken.None);

        Assert.Contains("http://example.com:8080/", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_ReturnsTimeoutForTaskCanceledException()
    {
        var uri = new Uri("http://example.com/");
        var exception = new TaskCanceledException();

        var message = HttpHealthCheckHelpers.GetFriendlyErrorMessage(uri, exception, CancellationToken.None);

        Assert.Contains("timed out", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_ReturnsCanceledWhenTokenIsCanceled()
    {
        var uri = new Uri("http://example.com/");
        var exception = new OperationCanceledException();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        var message = HttpHealthCheckHelpers.GetFriendlyErrorMessage(uri, exception, cts.Token);

        Assert.Contains("canceled", message);
    }

    [Fact]
    public void GetFriendlyErrorMessage_ReturnsStatusCodeForHttpRequestException()
    {
        var uri = new Uri("http://example.com/");
        var exception = new HttpRequestException(null, null, System.Net.HttpStatusCode.NotFound);

        var message = HttpHealthCheckHelpers.GetFriendlyErrorMessage(uri, exception, CancellationToken.None);

        Assert.Contains("404", message);
        Assert.Contains("NotFound", message);
    }

    private static async Task WriteResponseAsync(TcpListener listener, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }
}
