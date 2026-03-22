// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Mcp;
using Aspire.Dashboard.Telemetry;
using Aspire.Hosting;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class McpServiceTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public McpServiceTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    public async Task CallService_McpEndPoint_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var sessionId = await InitializeSessionAsync(httpClient);
        var request = CreateListToolsRequest(sessionId);

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        var responseData = await GetDataFromSseResponseAsync(responseMessage);

        // Assert
        var jsonResponse = JsonNode.Parse(responseData!)!;
        var tools = jsonResponse["result"]!["tools"]!.AsArray();

        Assert.NotEmpty(tools);
    }

    [Fact]
    public async Task CallService_McpEndPointDisabled_Failure()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardMcpDisableName.ConfigKey] = "true";
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var request = CreateListToolsRequest();

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, responseMessage.StatusCode);
    }

    [Fact]
    public async Task CallService_McpEndPoint_RequiredApiKeyWrong_Failure()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardMcpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardMcpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var requestMessage = CreateListToolsRequest();

        // Act
        var responseMessage = await httpClient.SendAsync(requestMessage).DefaultTimeout(TestConstants.LongTimeoutDuration);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, responseMessage.StatusCode);
    }

    [Fact]
    public async Task CallService_McpEndPoint_RequiredApiKeySent_Success()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardMcpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardMcpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        void AddApiKey(HttpRequestMessage r) => r.Headers.TryAddWithoutValidation(McpApiKeyAuthenticationHandler.McpApiKeyHeaderName, apiKey);

        var sessionId = await InitializeSessionAsync(httpClient, AddApiKey);

        var listRequest = CreateListToolsRequest(sessionId);
        AddApiKey(listRequest);

        var responseMessage = await httpClient.SendAsync(listRequest).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        var responseData = await GetDataFromSseResponseAsync(responseMessage);

        // Assert
        var jsonResponse = JsonNode.Parse(responseData!)!;
        var tools = jsonResponse["result"]!["tools"]!.AsArray();

        Assert.NotEmpty(tools);
    }

    [Fact]
    public async Task CallService_NoResourceService_ResourceToolsNotRegistered()
    {
        // Arrange - Create dashboard without configuring resource service URL
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var sessionId = await InitializeSessionAsync(httpClient);
        var request = CreateListToolsRequest(sessionId);

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        var responseData = await GetDataFromSseResponseAsync(responseMessage);

        // Assert
        var jsonResponse = JsonNode.Parse(responseData!)!;
        var tools = jsonResponse["result"]!["tools"]!.AsArray();

        // Verify that telemetry tools are available
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_structured_logs");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_traces");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_trace_structured_logs");

        // Verify that resource tools are NOT available
        Assert.DoesNotContain(tools, t => t!["name"]?.ToString() == "list_resources");
        Assert.DoesNotContain(tools, t => t!["name"]?.ToString() == "list_console_logs");
        Assert.DoesNotContain(tools, t => t!["name"]?.ToString() == "execute_resource_command");
    }

    [Fact]
    public async Task CallService_WithResourceService_ResourceToolsRegistered()
    {
        // Arrange - Create dashboard with resource service URL configured
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.ResourceServiceUrlName.ConfigKey] = "http://localhost:5000";
            config[DashboardConfigNames.ResourceServiceClientAuthModeName.ConfigKey] = nameof(ResourceClientAuthMode.Unsecured);
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var sessionId = await InitializeSessionAsync(httpClient);
        var request = CreateListToolsRequest(sessionId);

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        var responseData = await GetDataFromSseResponseAsync(responseMessage);

        // Assert
        var jsonResponse = JsonNode.Parse(responseData!)!;
        var tools = jsonResponse["result"]!["tools"]!.AsArray();

        // Verify that telemetry tools are available
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_structured_logs");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_traces");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_trace_structured_logs");

        // Verify that resource tools ARE available
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_resources");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "list_console_logs");
        Assert.Contains(tools, t => t!["name"]?.ToString() == "execute_resource_command");
    }

    [Fact]
    public async Task CallService_BrowserEndPoint_Failure()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.FrontendSingleEndPointAccessor().EndPoint}");

        var request = CreateListToolsRequest();

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, responseMessage.StatusCode);
    }

    [Fact]
    public async Task CallService_McpEndPointHttpOnly_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper,
            additionalConfiguration: data =>
            {
                data[DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "https://127.0.0.1:0";
                data[DashboardConfigNames.DashboardOtlpGrpcUrlName.ConfigKey] = "https://127.0.0.1:0";
                data[DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = "https://127.0.0.1:0";

                // Only HTTP endpoint
                data[DashboardConfigNames.DashboardMcpUrlName.ConfigKey] = "http://127.0.0.1:0";
            });

        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var sessionId = await InitializeSessionAsync(httpClient);
        var request = CreateListToolsRequest(sessionId);

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        var responseData = await GetDataFromSseResponseAsync(responseMessage);

        // Assert
        var jsonResponse = JsonNode.Parse(responseData!)!;
        var tools = jsonResponse["result"]!["tools"]!.AsArray();

        Assert.NotEmpty(tools);
    }

    [Fact]
    public async Task CallService_McpTool_TelemetryRecorded()
    {
        // Arrange
        var testTelemetrySender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };

        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            _testOutputHelper,
            preConfigureBuilder: builder =>
            {
                // Replace the telemetry sender with our test version
                builder.Services.AddSingleton<IDashboardTelemetrySender>(testTelemetrySender);
            });

        await app.StartAsync().DefaultTimeout();

        // Initialize telemetry service
        var telemetryService = app.Services.GetRequiredService<DashboardTelemetryService>();
        await telemetryService.InitializeAsync();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.McpEndPointAccessor().EndPoint}");

        var sessionId = await InitializeSessionAsync(httpClient);
        var request = CreateListToolsRequest(sessionId);

        // Act
        var responseMessage = await httpClient.SendAsync(request).DefaultTimeout(TestConstants.LongTimeoutDuration);
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        // Read telemetry items until we find the McpToolCall event
        bool foundMcpToolCall = false;
        while (await testTelemetrySender.ContextChannel.Reader.WaitToReadAsync().DefaultTimeout())
        {
            var context = await testTelemetrySender.ContextChannel.Reader.ReadAsync().DefaultTimeout();
            if (context.Name.Contains(TelemetryEventKeys.McpToolCall))
            {
                foundMcpToolCall = true;
                break;
            }
        }
        Assert.True(foundMcpToolCall, "Expected to find McpToolCall telemetry event");

        // Then read until we find the EndOperation event
        bool foundEndOperation = false;
        while (await testTelemetrySender.ContextChannel.Reader.WaitToReadAsync().DefaultTimeout())
        {
            var context = await testTelemetrySender.ContextChannel.Reader.ReadAsync().DefaultTimeout();
            if (context.Name.Contains(TelemetryEndpoints.TelemetryEndOperation))
            {
                foundEndOperation = true;
                break;
            }
        }
        Assert.True(foundEndOperation, "Expected to find EndOperation telemetry event");
    }

    internal static HttpRequestMessage CreateInitializeRequest(string? sessionId = null)
    {
        var json =
            """
            {
              "jsonrpc": "2.0",
              "id": "init",
              "method": "initialize",
              "params": {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {
                  "name": "test-client",
                  "version": "1.0.0"
                }
              }
            }
            """;
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.TryAddWithoutValidation("content-type", "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        return request;
    }

    internal static async Task<string> InitializeSessionAsync(HttpClient httpClient, Action<HttpRequestMessage>? configureRequest = null)
    {
        var initRequest = CreateInitializeRequest();
        configureRequest?.Invoke(initRequest);
        var initResponse = await httpClient.SendAsync(initRequest).DefaultTimeout(TestConstants.LongTimeoutDuration);
        initResponse.EnsureSuccessStatusCode();
        var sessionId = initResponse.Headers.GetValues("Mcp-Session-Id").First();

        // Consume the SSE response body to properly release the connection
        await initResponse.Content.ReadAsStringAsync().DefaultTimeout(TestConstants.LongTimeoutDuration);

        // Send initialized notification
        var notificationJson =
            """
            {
              "jsonrpc": "2.0",
              "method": "notifications/initialized"
            }
            """;
        var notificationContent = new ByteArrayContent(Encoding.UTF8.GetBytes(notificationJson));
        notificationContent.Headers.TryAddWithoutValidation("content-type", "application/json");
        var notificationRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = notificationContent
        };
        notificationRequest.Headers.TryAddWithoutValidation("accept", "application/json");
        notificationRequest.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        notificationRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        configureRequest?.Invoke(notificationRequest);
        var notificationResponse = await httpClient.SendAsync(notificationRequest).DefaultTimeout(TestConstants.LongTimeoutDuration);
        notificationResponse.EnsureSuccessStatusCode();

        return sessionId;
    }

    internal static HttpRequestMessage CreateListToolsRequest(string? sessionId = null)
    {
        var json =
            """
            {
              "jsonrpc": "2.0",
              "id": "1",
              "method": "tools/list",
              "params": {}
            }
            """;
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
        content.Headers.TryAddWithoutValidation("content-type", "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("accept", "application/json");
        request.Headers.TryAddWithoutValidation("accept", "text/event-stream");
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }
        return request;
    }

    internal static async Task<string?> GetDataFromSseResponseAsync(HttpResponseMessage response)
    {
        string responseText = await response.Content.ReadAsStringAsync();

        // Find the line that starts with "data:"
        var dataLine = Array.Find(responseText.Split('\n'), line => line.StartsWith("data:"));
        if (dataLine != null)
        {
            return dataLine.Substring("data:".Length).Trim();
        }

        return null;
    }
}
