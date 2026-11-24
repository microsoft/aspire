// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Mcp;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Testing;
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
        monitor.AddConnection("socket.hash1", connection);

        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains(McpErrorMessages.DashboardNotAvailable, exception.Message);
    }

    [Fact]
    public async Task ListStructuredLogsTool_LogsSanitizedOutboundUrl()
    {
        var resourcesResponse = JsonSerializer.Serialize(
            Array.Empty<ResourceInfoJson>(),
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);
        var logsResponse = JsonSerializer.Serialize(
            new TelemetryApiResponse
            {
                Data = new OtlpTelemetryDataJson { ResourceLogs = [] },
                TotalCount = 0,
                ReturnedCount = 0
            },
            OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        string? outboundLogsUrl = null;

        using var handler = new MockHttpMessageHandler(request =>
        {
            var isResourcesRequest = request.RequestUri!.AbsolutePath.EndsWith(
                "/resources",
                StringComparison.Ordinal);
            if (!isResourcesRequest)
            {
                outboundLogsUrl = request.RequestUri.AbsoluteUri;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    isResourcesRequest ? resourcesResponse : logsResponse,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        });
        var sink = new TestSink();
        var logger = new TestLogger<ListStructuredLogsTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListStructuredLogsTool(
            new StaticDashboardInfoProvider(
                "https://user:password@example.com:5000/login?t=actual-login-token&view=resources",
                apiKey: "api-key"),
            auxiliaryBackchannelMonitor: null,
            new MockHttpClientFactory(handler),
            logger);

        await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        AssertDashboardRequestUrlEqual(
            $"https://user:password@example.com:5000/api/telemetry/logs?t=actual-login-token&view=resources&limit={TelemetryCommandHelpers.MaxTelemetryLimit}",
            outboundLogsUrl);
        var write = Assert.Single(
            sink.Writes,
            context => context.Message?.StartsWith(
                "Fetching structured logs from ",
                StringComparison.Ordinal) == true);
        Assert.Equal(
            $"Fetching structured logs from https://example.com:5000/api/telemetry/logs?view=resources&limit={TelemetryCommandHelpers.MaxTelemetryLimit}",
            write.Message);
    }

    [Fact]
    public async Task ListStructuredLogsTool_AuthenticatedRequestDoesNotLeakInLogsOrErrors()
    {
        var resourcesResponse = JsonSerializer.Serialize(
            Array.Empty<ResourceInfoJson>(),
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);
        Uri? logsRequestUri = null;
        using var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resources", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        resourcesResponse,
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            logsRequestUri = request.RequestUri;
            throw new HttpRequestException(
                "Request failed at https://" + "exception-user" + ":" + "exception-password" +
                "@exception.example?token=exception-secret#exception-fragment");
        });
        var sink = new TestSink();
        var logger = new TestLogger<ListStructuredLogsTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListStructuredLogsTool(
            new StaticDashboardInfoProvider(
                "https://" + "request-user" + ":" + "request-password" + "@example.com:5000/login" +
                "?t=actual-login-token&accessKey=request-secret&view=resources#request-fragment",
                apiKey: "api-key"),
            auxiliaryBackchannelMonitor: null,
            new MockHttpClientFactory(handler),
            logger);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.True(logsRequestUri is not null, exception.ToString());
        AssertAuthenticatedRequest(logsRequestUri);
        AssertDiagnosticsAreSanitized(exception, sink);
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

        // Parse the JSON array from the response to verify logId extraction and attribute filtering
        var jsonStartIndex = textContent.Text.IndexOf('[');
        var jsonEndIndex = textContent.Text.LastIndexOf(']') + 1;
        var jsonText = textContent.Text[jsonStartIndex..jsonEndIndex];
        var logsArray = JsonNode.Parse(jsonText)?.AsArray();

        Assert.NotNull(logsArray);
        Assert.Equal(3, logsArray.Count);

        // Verify first log entry has correct resourceName, logId extracted, and aspire.log_id not in attributes
        var firstLog = logsArray[0]?.AsObject();
        Assert.NotNull(firstLog);
        Assert.Equal("api-service-instance-1", firstLog["resourceName"]?.GetValue<string>());
        Assert.Equal(42, firstLog["logId"]?.GetValue<long>());
        var firstLogAttributes = firstLog["attributes"]?.AsObject();
        Assert.NotNull(firstLogAttributes);
        Assert.False(firstLogAttributes.ContainsKey(OtlpHelpers.AspireLogIdAttribute), "aspire.log_id should be filtered from attributes");
        Assert.True(firstLogAttributes.ContainsKey("custom.attr"), "custom.attr should be present in attributes");

        // Verify dashboardUrl is included for each log entry with correct URLs
        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=42", firstLog["dashboardUrl"]?.GetValue<string>());

        // Verify second log entry has correct resourceName (different instance), logId extracted (from intValue)
        var secondLog = logsArray[1]?.AsObject();
        Assert.NotNull(secondLog);
        Assert.Equal("api-service-instance-2", secondLog["resourceName"]?.GetValue<string>());
        Assert.Equal(43, secondLog["logId"]?.GetValue<long>());
        var secondLogAttributes = secondLog["attributes"]?.AsObject();
        Assert.NotNull(secondLogAttributes);
        Assert.False(secondLogAttributes.ContainsKey(OtlpHelpers.AspireLogIdAttribute), "aspire.log_id should be filtered from attributes");

        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=43", secondLog["dashboardUrl"]?.GetValue<string>());

        // Verify third log entry has correct resourceName (no instance ID)
        var thirdLog = logsArray[2]?.AsObject();
        Assert.NotNull(thirdLog);
        Assert.Equal("worker-service", thirdLog["resourceName"]?.GetValue<string>());
        Assert.Equal(44, thirdLog["logId"]?.GetValue<long>());

        Assert.Equal("http://localhost:18888/structuredlogs?logEntryId=44", thirdLog["dashboardUrl"]?.GetValue<string>());
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

        Assert.Equal("List structured logs for resources. Returns the most recent log entries; use severity, search, resourceName and limit to narrow the results.", tool.Description);
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
            actualMonitor,
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
        monitor.AddConnection("socket.hash1", connection);
        return monitor;
    }

    [Fact]
    public async Task ListStructuredLogsTool_WithSearch_PassesSearchToUrl()
    {
        var resources = new ResourceInfoJson[]
        {
            new() { Name = "api-service", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = [] },
            TotalCount = 0,
            ReturnedCount = 0
        };
        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        string? capturedUrl = null;
        using var mockHandler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (request.RequestUri?.AbsolutePath.Contains("/resources") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

            capturedUrl = url;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(apiResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var mockHttpClientFactory = new MockHttpClientFactory(mockHandler);

        var monitor = CreateMonitorWithDashboard();
        var tool = CreateTool(monitor, mockHttpClientFactory);

        var arguments = new Dictionary<string, JsonElement>
        {
            ["search"] = JsonDocument.Parse("\"timeout error\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(capturedUrl);
        Assert.Contains("search=timeout", capturedUrl);
    }

    [Fact]
    public void ListStructuredLogsTool_InputSchema_HasSearchProperty()
    {
        var tool = CreateTool();

        var schema = tool.GetInputSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("properties", out var properties));
        Assert.True(properties.TryGetProperty("search", out var search));
        Assert.True(search.TryGetProperty("type", out var type));
        Assert.Equal("string", type.GetString());
    }

    private static void AssertAuthenticatedRequest(Uri? requestUri)
    {
        Assert.NotNull(requestUri);
        Assert.Equal("request-user:request-password", requestUri.UserInfo);
        Assert.Equal(
            $"?accessKey=request-secret&view=resources&limit={TelemetryCommandHelpers.MaxTelemetryLimit}",
            requestUri.Query);
    }

    private static void AssertDiagnosticsAreSanitized(
        ModelContextProtocol.McpProtocolException exception,
        TestSink sink)
    {
        foreach (var diagnostic in new[] { exception.Message }.Concat(
            sink.Writes.Select(write => $"{write.Message} {write.Exception}")))
        {
            Assert.DoesNotContain("request-user", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-password", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("actual-login-token", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-secret", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-fragment", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-secret", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-fragment", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-user", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-password", diagnostic, StringComparison.Ordinal);
        }
    }

    private static void AssertDashboardRequestUrlEqual(string? expected, string? actual)
    {
        Assert.Equal(
            DashboardUrls.RemoveDashboardLoginToken(expected),
            DashboardUrls.RemoveDashboardLoginToken(actual));
    }

    [Fact]
    public async Task ListStructuredLogsTool_WithSeverity_PassesMinimumSeverityToUrl()
    {
        Uri? logsRequestUri = null;
        using var handler = CreateHandler(CreateLogsResponse(), request => logsRequestUri = request.RequestUri);
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["severity"] = JsonSerializer.SerializeToElement("warning")
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(logsRequestUri);
        Assert.Equal($"?severity=Warning&limit={TelemetryCommandHelpers.MaxTelemetryLimit}", logsRequestUri.Query);
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsLatestLogsUpToLimit()
    {
        using var handler = CreateHandler(CreateLogsResponse(Enumerable.Range(0, 5).Select(index => $"message {index}").ToArray()));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(2)
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.StartsWith("Returned latest 2 log entries. Earlier 3 log entries not returned because of size limits.", text, StringComparison.Ordinal);
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        Assert.Equal(["message 3", "message 4"], logs.Select(log => log!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsNewestLog_WhenResourcesAreInterleaved()
    {
        // The API groups records by resource, so the newest record is not the last one in the response.
        using var handler = CreateHandler(CreateResourceLogsResponse(
            CreateResourceLogs("api", (1, "api first"), (3, "api newest")),
            CreateResourceLogs("worker", (2, "worker middle"))));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["limit"] = JsonSerializer.SerializeToElement(1)
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        Assert.Equal(["api newest"], logs.Select(log => log!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListStructuredLogsTool_DefaultResponseStaysCompact_WhenManyLargeLogsExist()
    {
        using var handler = CreateHandler(CreateLogsResponse(Enumerable.Range(0, 800).Select(index => $"{index} {new string('x', 1500)}").ToArray()));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 20_000);
        Assert.StartsWith("Returned latest ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListStructuredLogsTool_BoundsAttributes_WhenNewestLogHasLargeAttributes()
    {
        // Nine distinct attribute values at the 2,048-character cap exceed the whole list budget, so without
        // bounding the newest log would not fit and the response would contain no logs at all.
        var resourceLogs = CreateResourceLogs("api", (0, "older small"), (1, "newest large"));
        var newestLog = resourceLogs.ScopeLogs![0].LogRecords![1];
        newestLog.Attributes =
        [
            .. newestLog.Attributes!,
            .. Enumerable.Range(0, 9).Select(index => new OtlpKeyValueJson
            {
                Key = $"large.{index}",
                Value = new OtlpAnyValueJson { StringValue = new string((char)('a' + index), 2048) }
            })
        ];
        using var handler = CreateHandler(CreateResourceLogsResponse(resourceLogs));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 20_000);
        Assert.StartsWith("Returned 2 log entries.", text, StringComparison.Ordinal);
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        Assert.Equal(["older small", "newest large"], logs.Select(log => log!["message"]!.GetValue<string>()));
        Assert.Null(logs[0]!["attributesTruncated"]);

        var newest = logs[1]!;
        Assert.Equal(1, newest["logId"]!.GetValue<long>());
        Assert.Equal("Information", newest["severity"]!.GetValue<string>());
        Assert.True(newest["attributesTruncated"]!.GetValue<bool>());
        var attributes = newest["attributes"]!.AsObject();
        Assert.Equal(["large.0", "large.1", "large.2", "large.3", "large.4", "large.5", "large.6"], attributes.Select(a => a.Key));
        Assert.Equal($"{new string('a', 512)}...[TRUNCATED]", attributes["large.0"]!.GetValue<string>());
    }

    [Fact]
    public async Task ListStructuredLogsTool_DuplicateReferencesPointToReturnedLogs_WhenOriginalIsOutsideBudget()
    {
        // The newest log repeats the oldest message, which falls outside the budget. Its message must be written in full
        // instead of referring to the dropped log. Log 9 repeats the message of log 7, and both are returned.
        var messages = Enumerable.Range(0, 9).Select(index => new string((char)('a' + index), 2048)).ToList();
        messages.Add(messages[7]);
        messages.Add(messages[0]);
        using var handler = CreateHandler(CreateLogsResponse(messages.ToArray()));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 20_000);
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        var logIds = logs.Select(log => log!["logId"]!.GetValue<long>()).ToList();
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10], logIds);
        Assert.Equal(messages[0], logs[^1]!["message"]!.GetValue<string>());
        Assert.Equal(messages[7], logs[^2]!["message"]!.GetValue<string>());

        var references = logs
            .Select(log => log!["message"]!.GetValue<string>())
            .Where(message => message.StartsWith("Duplicate of ", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(["""Duplicate of "message" for log entry 9"""], references);
        Assert.All(references, reference => Assert.Contains(long.Parse(reference[(reference.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture), logIds));
    }

    [Fact]
    public async Task ListStructuredLogsTool_DefaultResponseStaysCompact_WhenLogsHaveNonAsciiMessages()
    {
        // CJK characters are written as three UTF-8 bytes each, so these messages are three times larger than their
        // character count. Each exceeds the per-field budget and is shortened to fit it.
        var messages = Enumerable.Range(0, 9).Select(index => new string((char)(0x4E00 + index), 1500)).ToArray();
        using var handler = CreateHandler(CreateLogsResponse(messages));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 20_000);
        Assert.StartsWith("Returned latest 3 log entries. Earlier 6 log entries not returned because of size limits.", text, StringComparison.Ordinal);
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        Assert.Equal(
            messages[6..].Select(message => $"{message[..1365]}...[TRUNCATED]"),
            logs.Select(log => log!["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ListStructuredLogsTool_BoundsAttributes_WhenNewestLogHasEscapedAttributeValues()
    {
        // Control characters are written as six-byte escapes such as \u0001, so these values are small when counted in
        // characters but serialize beyond the whole list budget.
        var resourceLogs = CreateResourceLogs("api", (0, "older small"), (1, "newest escaped"));
        var newestLog = resourceLogs.ScopeLogs![0].LogRecords![1];
        newestLog.Attributes =
        [
            .. newestLog.Attributes!,
            .. Enumerable.Range(0, 7).Select(index => new OtlpKeyValueJson
            {
                Key = $"control.{index}",
                Value = new OtlpAnyValueJson { StringValue = new string((char)(1 + index), 512) }
            })
        ];
        using var handler = CreateHandler(CreateResourceLogsResponse(resourceLogs));
        var tool = CreateTool(CreateMonitorWithDashboard(), new MockHttpClientFactory(handler));

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!)).Text;
        Assert.InRange(Encoding.UTF8.GetByteCount(text), 1, 20_000);
        Assert.StartsWith("Returned 2 log entries.", text, StringComparison.Ordinal);
        var logs = JsonNode.Parse(text[text.IndexOf('[')..(text.LastIndexOf(']') + 1)])!.AsArray();
        Assert.Equal(["older small", "newest escaped"], logs.Select(log => log!["message"]!.GetValue<string>()));

        var newest = logs[1]!;
        Assert.True(newest["attributesTruncated"]!.GetValue<bool>());
        Assert.Equal(
            Enumerable.Range(0, 7).Select(index => ($"control.{index}", $"{new string((char)(1 + index), 85)}...[TRUNCATED]")),
            newest["attributes"]!.AsObject().Select(a => (a.Key, a.Value!.GetValue<string>())));
    }

    [Theory]
    [InlineData("""{"severity":"Fatal"}""", "Argument 'severity' must be one of: Trace, Debug, Information, Warning, Error, Critical.")]
    [InlineData("""{"limit":801}""", "Argument 'limit' must be an integer from 1 through 800.")]
    public async Task ListStructuredLogsTool_RejectsInvalidArguments(string json, string expectedMessage)
    {
        using var arguments = JsonDocument.Parse(json);
        var tool = CreateTool(CreateMonitorWithDashboard());
        var context = CallToolContextTestHelper.Create(
            arguments.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()));

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(context, CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(ModelContextProtocol.McpErrorCode.InvalidParams, exception.ErrorCode);
        Assert.Equal(expectedMessage, exception.Message);
    }

    private static string CreateLogsResponse(params string[] messages)
    {
        return CreateResourceLogsResponse(CreateResourceLogs("api", messages.Select((message, index) => ((ulong)index, message)).ToArray()));
    }

    private static string CreateResourceLogsResponse(params OtlpResourceLogsJson[] resourceLogs)
    {
        return JsonSerializer.Serialize(
            new TelemetryApiResponse { Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs } },
            OtlpJsonSerializerContext.Default.TelemetryApiResponse);
    }

    private static OtlpResourceLogsJson CreateResourceLogs(string serviceName, params (ulong TimeOffset, string Message)[] logs)
    {
        var logRecords = logs.Select((log, index) => new OtlpLogRecordJson
        {
            TimeUnixNano = 1706540400000000000 + log.TimeOffset,
            SeverityText = "Information",
            Body = new OtlpAnyValueJson { StringValue = log.Message },
            Attributes = [new OtlpKeyValueJson { Key = "aspire.log_id", Value = new OtlpAnyValueJson { IntValue = index } }]
        }).ToArray();

        return new OtlpResourceLogsJson
        {
            Resource = new OtlpResourceJson
            {
                Attributes = [new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } }]
            },
            ScopeLogs = [new OtlpScopeLogsJson { Scope = new OtlpInstrumentationScopeJson { Name = "Api" }, LogRecords = logRecords }]
        };
    }

    private static MockHttpMessageHandler CreateHandler(string logsResponse, Action<HttpRequestMessage>? onLogsRequest = null)
    {
        var resourcesResponse = JsonSerializer.Serialize(
            [new ResourceInfoJson { Name = "api", HasLogs = true, HasTraces = true, HasMetrics = true }],
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        return new MockHttpMessageHandler(request =>
        {
            var isResourcesRequest = request.RequestUri!.AbsolutePath.EndsWith("/resources", StringComparison.Ordinal);
            if (!isResourcesRequest)
            {
                onLogsRequest?.Invoke(request);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(isResourcesRequest ? resourcesResponse : logsResponse, System.Text.Encoding.UTF8, "application/json")
            };
        });
    }
}
