// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Http.Json;
using Aspire.Dashboard.Api;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class TelemetryApiTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public TelemetryApiTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    #region Configuration Tests

    [Fact]
    public async Task Configuration_ApiAuthModeDefaults_WhenNotConfigured()
    {
        // Arrange & Act
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
            // Remove Api auth config to test defaults
            config.Remove(DashboardConfigNames.DashboardApiAuthModeName.ConfigKey);
            config.Remove(DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey);
        });
        await app.StartAsync().DefaultTimeout();

        // Assert - verify ApiOptions defaults to ApiKey with auto-generated key
        var options = app.Services.GetRequiredService<IOptionsMonitor<DashboardOptions>>().CurrentValue;
        Assert.NotNull(options.Api);
        Assert.Equal(ApiAuthMode.ApiKey, options.Api.AuthMode);
        Assert.False(string.IsNullOrEmpty(options.Api.PrimaryApiKey), "A primary API key should be auto-generated.");
    }

    #endregion

    [Fact]
    public async Task GetSpans_NoAuth_ReturnsUnauthorized()
    {
        // Arrange - with browser token auth mode and API key auth required
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        // Use a handler that doesn't follow redirects to capture the redirect response
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act - no auth (no API key header, no browser token)
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert - should redirect to login (since frontend auth is browser token)
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Found,
            $"Expected redirect or unauthorized, got {response.StatusCode}");
    }

    [Fact]
    public async Task GetSpans_UnsecuredMode_Returns200()
    {
        // Arrange - unsecured frontend mode
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
        Assert.NotNull(content.Data);
    }

    [Fact]
    public async Task GetSpans_WithApiKey_Returns200()
    {
        // Arrange - with Dashboard API key auth
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiAuthenticationHandler.ApiKeyHeaderName, apiKey);

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
    }

    [Fact]
    public async Task GetSpans_WithWrongApiKey_ReturnsUnauthorized()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        // Use a handler that doesn't follow redirects
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiAuthenticationHandler.ApiKeyHeaderName, "WrongKey!");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert - wrong API key should return 401 Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_UnsecuredMode_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/logs").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
        Assert.NotNull(content.Data);
    }

    [Fact]
    public async Task GetLogs_WithTraceIdFilter_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - use ?traceId query param instead of /spans/{traceId}/logs
        var response = await httpClient.GetAsync("/api/telemetry/logs?traceId=some-trace-id").DefaultTimeout();

        // Assert - returns 200 with empty data when no logs match the trace ID
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
        Assert.Equal(0, content.TotalCount);
    }

    [Fact]
    public async Task GetSpans_WithQueryParameters_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - test query parameters without resource filter (no resources exist in test)
        var response = await httpClient.GetAsync($"/api/telemetry/spans?hasError=true&limit={int.MaxValue}").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
    }

    [Fact]
    public async Task GetSpans_WithUnknownResource_Returns404()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - request with unknown resource filter
        var response = await httpClient.GetAsync("/api/telemetry/spans?resource=unknown-resource").DefaultTimeout();

        // Assert - should return 404 for unknown resource
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetLogs_WithQueryParameters_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - test query parameters without resource filter (no resources exist in test)
        var response = await httpClient.GetAsync($"/api/telemetry/logs?severity=Error&limit={int.MaxValue}").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
    }

    [Fact]
    public async Task GetLogs_WithUnknownResource_Returns404()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - request with unknown resource filter
        var response = await httpClient.GetAsync("/api/telemetry/logs?resource=unknown-resource").DefaultTimeout();

        // Assert - should return 404 for unknown resource
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetSpans_ApiAuthModeUnsecured_AllowsAccessWithoutAuth()
    {
        // Arrange - Api auth mode is unsecured (default)
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - no auth headers at all
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert - should succeed because Api auth is unsecured
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSpans_ApiAuthModeApiKey_RequiresApiKey()
    {
        // Arrange - Api auth mode requires API key
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        // Use a handler that doesn't follow redirects
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };

        // Act - no API key header
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert - should redirect to login or return unauthorized
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized or HttpStatusCode.Found,
            $"Expected redirect or unauthorized, got {response.StatusCode}");
    }

    [Fact]
    public async Task GetSpans_WithSecondaryApiKey_Returns200()
    {
        // Arrange - with secondary API key (for key rotation)
        var primaryKey = "PrimaryKey123!";
        var secondaryKey = "SecondaryKey456!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = primaryKey;
            config[DashboardConfigNames.DashboardApiSecondaryApiKeyName.ConfigKey] = secondaryKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiAuthenticationHandler.ApiKeyHeaderName, secondaryKey);

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/spans").DefaultTimeout();

        // Assert - secondary key should work
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetSpans_StreamingMode_ReturnsNdjsonContentType()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - request streaming mode
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var response = await httpClient.GetAsync("/api/telemetry/spans?follow=true", HttpCompletionOption.ResponseHeadersRead, cts.Token).DefaultTimeout();

            // Assert - should have NDJSON content type and streaming headers
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
            Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var bufferingValues));
            Assert.Equal("no", bufferingValues.Single());
        }
        catch (OperationCanceledException)
        {
            // Expected - streaming mode keeps connection open
        }
    }

    [Fact]
    public async Task GetLogs_StreamingMode_ReturnsNdjsonContentType()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act - request streaming mode
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var response = await httpClient.GetAsync("/api/telemetry/logs?follow=true", HttpCompletionOption.ResponseHeadersRead, cts.Token).DefaultTimeout();

            // Assert - should have NDJSON content type and streaming headers
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
            Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var bufferingValues));
            Assert.Equal("no", bufferingValues.Single());
        }
        catch (OperationCanceledException)
        {
            // Expected - streaming mode keeps connection open
        }
    }

    #region Trace Endpoint Tests

    [Fact]
    public async Task GetTraces_UnsecuredMode_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/traces").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
        Assert.NotNull(content.Data);
    }

    [Fact]
    public async Task GetTraces_WithHasErrorFilter_Returns200()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/traces?hasError=true&limit=10").DefaultTimeout();

        // Assert - returns 200 with empty data when no traces match
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync(OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        Assert.NotNull(content);
        Assert.Equal(0, content.TotalCount);
    }

    [Fact]
    public async Task GetTraces_WithUnknownResource_Returns404()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/traces?resource=unknownresource").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTraceById_NotFound_Returns404()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.Unsecured.ToString();
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/traces/nonexistent-trace-id").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetTraces_WithApiKey_Returns200()
    {
        // Arrange
        var apiKey = "TestApiKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = FrontendAuthMode.BrowserToken.ToString();
            config[DashboardConfigNames.DashboardApiAuthModeName.ConfigKey] = ApiAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardApiPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation(ApiAuthenticationHandler.ApiKeyHeaderName, apiKey);

        // Act
        var response = await httpClient.GetAsync("/api/telemetry/traces").DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    #endregion
}
