// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListTracesToolTests
{
    private static readonly TestHttpClientFactory s_httpClientFactory = new();

    [Fact]
    public async Task ListTracesTool_ReturnsFormattedTraces_WhenApiReturnsData()
    {
        // Local function to create OtlpResourceSpansJson with service name and instance ID
        static OtlpResourceSpansJson CreateResourceSpans(string serviceName, string? serviceInstanceId, params OtlpSpanJson[] spans)
        {
            var attributes = new List<OtlpKeyValueJson>
            {
                new() { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } }
            };
            if (serviceInstanceId is not null)
            {
                attributes.Add(new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = serviceInstanceId } });
            }

            return new OtlpResourceSpansJson
            {
                Resource = new OtlpResourceJson
                {
                    Attributes = [.. attributes]
                },
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Scope = new OtlpInstrumentationScopeJson { Name = "OpenTelemetry" },
                        Spans = spans
                    }
                ]
            };
        }

        // Arrange - Create mock HTTP handler with sample traces response
        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson
            {
                ResourceSpans =
                [
                    CreateResourceSpans("api-service", "instance-1",
                        new OtlpSpanJson
                        {
                            TraceId = "abc123def456789012345678901234567890",
                            SpanId = "span123456789012",
                            Name = "GET /api/products",
                            Kind = 2, // Server
                            StartTimeUnixNano = 1706540400000000000,
                            EndTimeUnixNano = 1706540400100000000,
                            Status = new OtlpSpanStatusJson { Code = 1 }, // Ok
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = "http.method", Value = new OtlpAnyValueJson { StringValue = "GET" } },
                                new OtlpKeyValueJson { Key = "http.url", Value = new OtlpAnyValueJson { StringValue = "/api/products" } }
                            ]
                        }),
                    CreateResourceSpans("api-service", "instance-2",
                        new OtlpSpanJson
                        {
                            TraceId = "abc123def456789012345678901234567890",
                            SpanId = "span234567890123",
                            ParentSpanId = "span123456789012",
                            Name = "GET /api/catalog",
                            Kind = 3, // Client
                            StartTimeUnixNano = 1706540400010000000,
                            EndTimeUnixNano = 1706540400090000000,
                            Status = new OtlpSpanStatusJson { Code = 1 },
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = "aspire.destination", Value = new OtlpAnyValueJson { StringValue = "catalog-service" } }
                            ]
                        }),
                    CreateResourceSpans("worker-service", "instance-1",
                        new OtlpSpanJson
                        {
                            TraceId = "xyz789abc123456789012345678901234567890",
                            SpanId = "span345678901234",
                            Name = "ProcessMessage",
                            Kind = 1, // Internal
                            StartTimeUnixNano = 1706540401000000000,
                            EndTimeUnixNano = 1706540401500000000,
                            Status = new OtlpSpanStatusJson { Code = 2 }, // Error
                            Attributes =
                            [
                                new OtlpKeyValueJson { Key = "error", Value = new OtlpAnyValueJson { StringValue = "Processing failed" } }
                            ]
                        })
                ]
            },
            TotalCount = 3,
            ReturnedCount = 3
        };

        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        // Create resources that match the OtlpResourceSpansJson entries
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

            // For traces endpoint, return the traces response
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

        // Parse the JSON array from the response
        var jsonStartIndex = textContent.Text.IndexOf('[');
        var jsonEndIndex = textContent.Text.LastIndexOf(']') + 1;
        var jsonText = textContent.Text[jsonStartIndex..jsonEndIndex];
        var tracesArray = JsonNode.Parse(jsonText)?.AsArray();

        Assert.NotNull(tracesArray);
        // Should have 2 traces (grouped by trace_id)
        Assert.Equal(2, tracesArray.Count);

        // Verify first trace (trace_id is shortened to 7 characters)
        var firstTrace = tracesArray[0]?.AsObject();
        Assert.NotNull(firstTrace);
        Assert.Equal("abc123d", firstTrace["trace_id"]?.GetValue<string>());

        // Verify spans in first trace have correct source and destination
        var spans = firstTrace["spans"]?.AsArray();
        Assert.NotNull(spans);
        Assert.Equal(2, spans.Count);

        // Verify dashboard_link is included for each trace with correct URLs (trace_id is shortened to 7 chars in the URL)
        var firstDashboardLink = firstTrace["dashboard_link"]?.AsObject();
        Assert.NotNull(firstDashboardLink);
        Assert.Equal("http://localhost:18888/traces/detail/abc123d", firstDashboardLink["url"]?.GetValue<string>());
        Assert.Equal("abc123d", firstDashboardLink["text"]?.GetValue<string>());

        // First span (server) should have source from resource name, no destination
        var serverSpan = spans.FirstOrDefault(s => s?["kind"]?.GetValue<string>() == "Server")?.AsObject();
        Assert.NotNull(serverSpan);
        Assert.Equal("api-service-instance-1", serverSpan["source"]?.GetValue<string>());
        Assert.Null(serverSpan["destination"]);

        // Second span (client) should have source from resource name and destination from aspire.destination
        var clientSpan = spans.FirstOrDefault(s => s?["kind"]?.GetValue<string>() == "Client")?.AsObject();
        Assert.NotNull(clientSpan);
        Assert.Equal("api-service-instance-2", clientSpan["source"]?.GetValue<string>());
        Assert.Equal("catalog-service", clientSpan["destination"]?.GetValue<string>());

        // Verify second trace
        var secondTrace = tracesArray[1]?.AsObject();
        Assert.NotNull(secondTrace);
        Assert.Equal("xyz789a", secondTrace["trace_id"]?.GetValue<string>());

        var secondDashboardLink = secondTrace["dashboard_link"]?.AsObject();
        Assert.NotNull(secondDashboardLink);
        Assert.Equal("http://localhost:18888/traces/detail/xyz789a", secondDashboardLink["url"]?.GetValue<string>());
        Assert.Equal("xyz789a", secondDashboardLink["text"]?.GetValue<string>());

        // Verify spans in second trace have correct source and destination
        var secondTraceSpans = secondTrace["spans"]?.AsArray();
        Assert.NotNull(secondTraceSpans);
        Assert.Single(secondTraceSpans);

        // Internal span should have source from resource name (worker-service has no instance ID), no destination
        var internalSpan = secondTraceSpans[0]?.AsObject();
        Assert.NotNull(internalSpan);
        Assert.Equal("Internal", internalSpan["kind"]?.GetValue<string>());
        Assert.Equal("worker-service", internalSpan["source"]?.GetValue<string>());
        Assert.Null(internalSpan["destination"]);
    }

    [Fact]
    public async Task ListTracesTool_ReturnsEmptyTraces_WhenApiReturnsNoData()
    {
        // Arrange - Create mock HTTP handler with empty traces response
        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = [] },
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

            // For traces endpoint, return empty traces response
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
        Assert.Contains("TRACES DATA", textContent.Text);
        // Empty array should be returned
        Assert.Contains("[]", textContent.Text);
    }

    [Fact]
    public async Task ListTracesTool_ReturnsResourceNotFound_WhenResourceDoesNotExist()
    {
        // Arrange - Create mock HTTP handler that returns resources that don't match the requested name
        var resources = new ResourceInfoJson[]
        {
            new() { Name = "other-resource", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var emptyTracesResponse = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = [] },
            TotalCount = 0,
            ReturnedCount = 0
        };
        var emptyTracesJson = JsonSerializer.Serialize(emptyTracesResponse, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

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

            // For any other request, return empty traces response
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(emptyTracesJson, System.Text.Encoding.UTF8, "application/json")
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
    public async Task ListTracesTool_FiltersTracesByResource_WhenResourceNameProvided()
    {
        // Arrange - Create mock HTTP handler with traces from multiple resources
        static OtlpResourceSpansJson CreateResourceSpans(string serviceName, string? serviceInstanceId, params OtlpSpanJson[] spans)
        {
            var attributes = new List<OtlpKeyValueJson>
            {
                new() { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = serviceName } }
            };
            if (serviceInstanceId is not null)
            {
                attributes.Add(new OtlpKeyValueJson { Key = "service.instance.id", Value = new OtlpAnyValueJson { StringValue = serviceInstanceId } });
            }

            return new OtlpResourceSpansJson
            {
                Resource = new OtlpResourceJson
                {
                    Attributes = [.. attributes]
                },
                ScopeSpans =
                [
                    new OtlpScopeSpansJson
                    {
                        Scope = new OtlpInstrumentationScopeJson { Name = "OpenTelemetry" },
                        Spans = spans
                    }
                ]
            };
        }

        var apiResponseObj = new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson
            {
                ResourceSpans =
                [
                    CreateResourceSpans("api-service", null,
                        new OtlpSpanJson
                        {
                            TraceId = "trace123",
                            SpanId = "span123",
                            Name = "GET /api/products",
                            Kind = 2,
                            StartTimeUnixNano = 1706540400000000000,
                            EndTimeUnixNano = 1706540400100000000,
                            Status = new OtlpSpanStatusJson { Code = 1 }
                        })
                ]
            },
            TotalCount = 1,
            ReturnedCount = 1
        };

        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);

        var resources = new ResourceInfoJson[]
        {
            new() { Name = "api-service", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true },
            new() { Name = "worker-service", InstanceId = null, HasLogs = true, HasTraces = true, HasMetrics = true }
        };
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        string? capturedUrl = null;
        using var mockHandler = new MockHttpMessageHandler(request =>
        {
            // Capture the URL for assertions
            capturedUrl = request.RequestUri?.ToString();

            if (request.RequestUri?.AbsolutePath.Contains("/resources") == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(resourcesResponse, System.Text.Encoding.UTF8, "application/json")
                };
            }

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
            ["resourceName"] = JsonDocument.Parse("\"api-service\"").RootElement
        };

        // Act
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        // Assert
        Assert.True(result.IsError is null or false);
        Assert.NotNull(capturedUrl);
        // Verify the URL contains the resource name filter
        Assert.Contains("api-service", capturedUrl);
    }

    /// <summary>
    /// Creates a ListTracesTool instance for testing with optional custom dependencies.
    /// </summary>
    private static ListTracesTool CreateTool(
        TestAuxiliaryBackchannelMonitor? monitor = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var actualMonitor = monitor ?? new TestAuxiliaryBackchannelMonitor();
        IDashboardInfoProvider dashboardInfoProvider = new BackchannelDashboardInfoProvider(actualMonitor, NullLogger<BackchannelDashboardInfoProvider>.Instance);
        return new ListTracesTool(
            dashboardInfoProvider,
            httpClientFactory ?? s_httpClientFactory,
            NullLogger<ListTracesTool>.Instance);
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
