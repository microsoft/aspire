// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListStructuredLogsToolTests
{
    private static readonly TestHttpClientFactory s_httpClientFactory = new();

    [Fact]
    public async Task ListStructuredLogsTool_ThrowsException_WhenNoAppHostRunning()
    {
        var tool = CreateTool();

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
    }

    [Fact]
    public async Task ListStructuredLogsTool_ThrowsException_WhenDashboardNotAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            DashboardInfoResponse = null
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains(McpErrorMessages.DashboardNotAvailable, exception.Message);
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsFormattedLogs_WhenApiReturnsData()
    {
        // Local function to create OtlpResourceLogsJson with service name and instance ID
        static OtlpResourceLogsJson CreateResourceLogs(string serviceName, string? serviceInstanceId, params OtlpLogRecordJson[] logRecords)
        {
            var attributes = new List<OtlpKeyValueJson>
            {
                new() { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } }
            };
            if (serviceInstanceId is not null)
            {
                attributes.Add(new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = serviceInstanceId } });
            }

            return new OtlpResourceLogsJson
            {
                Resource = new OtlpResourceJson
                {
                    Attributes = [.. attributes]
                },
                ScopeLogs =
                [
                    new OtlpScopeLogsJson
                    {
                        Scope = new OtlpInstrumentationScopeJson { Name = "Microsoft.Extensions.Logging" },
                        LogRecords = logRecords
                    }
                ]
            };
        }

        // Arrange - Create mock HTTP handler with sample structured logs response
        // Include aspire.log_id attribute to verify it's extracted to log_id field and filtered from attributes
        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson
            {
                ResourceLogs =
                [
                    CreateResourceLogs("api-service", "instance-1",
                        new OtlpLogRecordJson
                        {
                            TimeUnixNano = 1706540400000000000,
                            SeverityNumber = 9,
                            SeverityText = "Information",
                            Body = new OtlpAnyValueJson { StringValue = "Application started successfully" },
                            TraceId = "abc123",
                            SpanId = "def456",
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = OtlpHelpers.AspireLogIdAttribute, Value = new OtlpAnyValueJson { StringValue = "42" } },
                                new OtlpKeyValueJson { Key = "custom.attr", Value = new OtlpAnyValueJson { StringValue = "custom-value" } }
                            ]
                        }),
                    CreateResourceLogs("api-service", "instance-2",
                        new OtlpLogRecordJson
                        {
                            TimeUnixNano = 1706540401000000000,
                            SeverityNumber = 13,
                            SeverityText = "Warning",
                            Body = new OtlpAnyValueJson { StringValue = "Connection timeout warning" },
                            TraceId = "abc123",
                            SpanId = "ghi789",
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = OtlpHelpers.AspireLogIdAttribute, Value = new OtlpAnyValueJson { IntValue = 43 } }
                            ]
                        }),
                    CreateResourceLogs("worker-service", "instance-1",
                        new OtlpLogRecordJson
                        {
                            TimeUnixNano = 1706540402000000000,
                            SeverityNumber = 17,
                            SeverityText = "Error",
                            Body = new OtlpAnyValueJson { StringValue = "Worker failed to process message" },
                            TraceId = "xyz789",
                            SpanId = "uvw123",
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = OtlpHelpers.AspireLogIdAttribute, Value = new OtlpAnyValueJson { IntValue = 44 } }
                            ]
                        })
                ]
            },
            TotalCount = 3,
            ReturnedCount = 3
        };

        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        // Create resources that match the OtlpResourceLogsJson entries
        var resources = new ResourceInfoJson[]
        {
            new() { Name = "api-service", InstanceId = "instance-1", HasLogs = true, HasTraces = true, HasMetrics = true },
            new() { Name = "api-service", InstanceId = "instance-2", HasLogs = true, HasTraces = true, HasMetrics = true },
            new() { Name = "worker-service", InstanceId = "instance-1", HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        using var mockHandler = new MockHttpMessageHandler(request =>
        {
            // Handle the resources endpoint
            if (request.RequestUri?.AbsolutePath.Contains("/resources") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // For logs endpoint, return the structured logs response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(apiResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var mockHttpClientFactory = new MockHttpClientFactory(mockHandler);

        // Use a dashboard URL with path and query string to test that only the base URL is used
        var monitor = CreateMonitorWithDashboard(dashboardUrls: ["http://localhost:18888/login?t=authtoken123"]);
        var tool = CreateTool(monitor, mockHttpClientFactory);

        // Act
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        // Assert
        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);

        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);

        // Parse the JSON array from the response to verify log_id extraction and attribute filtering
        var jsonStartIndex = textContent.Text.IndexOf('[');
        var jsonEndIndex = textContent.Text.LastIndexOf(']') + 1;
        var jsonText = textContent.Text[jsonStartIndex..jsonEndIndex];
        var logsArray = JsonNode.Parse(jsonText)?.AsArray();

        Assert.NotNull(logsArray);
        Assert.Equal(3, logsArray.Count);

        // Verify first log entry has correct resource_name, log_id extracted, and aspire.log_id not in attributes
        var firstLog = logsArray[0]?.AsObject();
        Assert.NotNull(firstLog);
        Assert.Equal("api-service-instance-1", firstLog["resource_name"]?.GetValue<string>());
        Assert.Equal(42, firstLog["log_id"]?.GetValue<long>());
        var firstLogAttributes = firstLog["attributes"]?.AsObject();
        Assert.NotNull(firstLogAttributes);
        Assert.False(firstLogAttributes.ContainsKey(OtlpHelpers.AspireLogIdAttribute), "aspire.log_id should be filtered from attributes");
        Assert.True(firstLogAttributes.ContainsKey("custom.attr"), "custom.attr should be present in attributes");

        // Verify dashboard_link is included for each log entry with correct URLs
        var firstDashboardLink = firstLog["dashboard_link"]?.AsObject();
        Assert.NotNull(firstDashboardLink);
        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=42", firstDashboardLink["url"]?.GetValue<string>());
        Assert.Equal("log_id: 42", firstDashboardLink["text"]?.GetValue<string>());

        // Verify second log entry has correct resource_name (different instance), log_id extracted (from intValue)
        var secondLog = logsArray[1]?.AsObject();
        Assert.NotNull(secondLog);
        Assert.Equal("api-service-instance-2", secondLog["resource_name"]?.GetValue<string>());
        Assert.Equal(43, secondLog["log_id"]?.GetValue<long>());
        var secondLogAttributes = secondLog["attributes"]?.AsObject();
        Assert.NotNull(secondLogAttributes);
        Assert.False(secondLogAttributes.ContainsKey(OtlpHelpers.AspireLogIdAttribute), "aspire.log_id should be filtered from attributes");

        var secondDashboardLink = secondLog["dashboard_link"]?.AsObject();
        Assert.NotNull(secondDashboardLink);
        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=43", secondDashboardLink["url"]?.GetValue<string>());
        Assert.Equal("log_id: 43", secondDashboardLink["text"]?.GetValue<string>());

        // Verify third log entry has correct resource_name (no instance ID)
        var thirdLog = logsArray[2]?.AsObject();
        Assert.NotNull(thirdLog);
        Assert.Equal("worker-service", thirdLog["resource_name"]?.GetValue<string>());
        Assert.Equal(44, thirdLog["log_id"]?.GetValue<long>());

        var thirdDashboardLink = thirdLog["dashboard_link"]?.AsObject();
        Assert.NotNull(thirdDashboardLink);
        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=44", thirdDashboardLink["url"]?.GetValue<string>());
        Assert.Equal("log_id: 44", thirdDashboardLink["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsEmptyLogs_WhenApiReturnsNoData()
    {
        // Arrange - Create mock HTTP handler with empty logs response
        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = [] },
            TotalCount = 0,
            ReturnedCount = 0
        };
        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        var resources = new ResourceInfoJson[]
        {
            new() { Name = "api-service", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        using var mockHandler = new MockHttpMessageHandler(request =>
        {
            // Handle the resources endpoint
            if (request.RequestUri?.AbsolutePath.Contains("/resources") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // For logs endpoint, return empty logs response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(apiResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var mockHttpClientFactory = new MockHttpClientFactory(mockHandler);

        var monitor = CreateMonitorWithDashboard();
        var tool = CreateTool(monitor, mockHttpClientFactory);

        // Act
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        // Assert
        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);

        var textContent = result.Content[0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("STRUCTURED LOGS DATA", textContent.Text);
        // Empty array should be returned
        Assert.Contains("[]", textContent.Text);
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsResourceNotFound_WhenResourceDoesNotExist()
    {
        // Arrange - Create mock HTTP handler that returns resources that don't match the requested name
        var resources = new ResourceInfoJson[]
        {
            new() { Name = "other-resource", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var emptyLogsResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = [] },
            TotalCount = 0,
            ReturnedCount = 0
        };
        var emptyLogsJson = JsonSerializer.Serialize(emptyLogsResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        using var mockHandler = new MockHttpMessageHandler(request =>
        {
            // Check if this is the resources lookup request
            if (request.RequestUri?.AbsolutePath.Contains("/resources") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

            // For any other request, return empty logs response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emptyLogsJson, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var mockHttpClientFactory = new MockHttpClientFactory(mockHandler);

        var monitor = CreateMonitorWithDashboard();
        var tool = CreateTool(monitor, mockHttpClientFactory);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse("\"non-existent-resource\"").RootElement
        };

        // Act
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        // Assert
        Assert.True(result.IsError);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("Resource 'non-existent-resource' not found", textContent.Text);
    }

    [Fact]
    public void ListStructuredLogsTool_HasCorrectName()
    {
        var tool = CreateTool();

        Assert.Equal(KnownMcpTools.ListStructuredLogs, tool.Name);
    }

    [Fact]
    public void ListStructuredLogsTool_HasCorrectDescription()
    {
        var tool = CreateTool();

        Assert.Equal("List structured logs for resources.", tool.Description);
    }

    [Fact]
    public void ListStructuredLogsTool_InputSchema_HasResourceNameProperty()
    {
        var tool = CreateTool();

        var schema = tool.GetInputSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("resourceName", out var resourceName));
        Assert.True(resourceName.TryGetProperty("type", out var type));
        Assert.Equal("string", type.GetString());
    }

    [Fact]
    public void ListStructuredLogsTool_InputSchema_ResourceNameIsOptional()
    {
        var tool = CreateTool();

        var schema = tool.GetInputSchema();

        // Check that there's no "required" array or it doesn't include resourceName
        if (schema.TryGetProperty("required", out var required))
        {
            var requiredArray = required.EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.DoesNotContain("resourceName", requiredArray);
        }
        // If no required property, that's also fine - means nothing is required
    }

    /// <summary>
    /// Creates a ListStructuredLogsTool instance for testing with optional custom dependencies.
    /// </summary>
    private static ListStructuredLogsTool CreateTool(
        TestAuxiliaryBackchannelMonitor? monitor = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var actualMonitor = monitor ?? new TestAuxiliaryBackchannelMonitor();
        IDashboardInfoProvider dashboardInfoProvider = new BackchannelDashboardInfoProvider(actualMonitor, NullLogger<BackchannelDashboardInfoProvider>.Instance);
        return new ListStructuredLogsTool(
            dashboardInfoProvider,
            httpClientFactory ?? s_httpClientFactory,
            NullLogger<ListStructuredLogsTool>.Instance);
    }

    /// <summary>
    /// Creates a TestAuxiliaryBackchannelMonitor with a connection configured with dashboard info.
    /// </summary>
    private static TestAuxiliaryBackchannelMonitor CreateMonitorWithDashboard(
        string apiBaseUrl = "http://localhost:5000",
        string apiToken = "test-token",
        string[]? dashboardUrls = null)
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = apiBaseUrl,
                ApiToken = apiToken,
                DashboardUrls = dashboardUrls ?? ["http://localhost:18888"]
            }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        return monitor;
    }
}
