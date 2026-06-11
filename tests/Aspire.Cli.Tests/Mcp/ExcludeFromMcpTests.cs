// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ExcludeFromMcpTests
{
    private const string ApiServiceName = "api-service";
    private const string SecretServiceName = "secret-service";

    [Fact]
    public void IsExcludedFromMcp_ReturnsFalse_WhenPropertyNotSet()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "test",
            Properties = []
        };

        Assert.False(McpToolHelpers.IsExcludedFromMcp(snapshot));
    }

    [Fact]
    public void IsExcludedFromMcp_ReturnsTrue_WhenPropertyIsBoolTrue()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "test",
            Properties = new Dictionary<string, JsonNode?>
            {
                [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
            }
        };

        Assert.True(McpToolHelpers.IsExcludedFromMcp(snapshot));
    }

    [Fact]
    public void IsExcludedFromMcp_ReturnsFalse_WhenPropertyIsBoolFalse()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "test",
            Properties = new Dictionary<string, JsonNode?>
            {
                [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(false)
            }
        };

        Assert.False(McpToolHelpers.IsExcludedFromMcp(snapshot));
    }

    [Fact]
    public void IsExcludedFromMcp_ReturnsTrue_WhenPropertyIsStringTrue()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "test",
            Properties = new Dictionary<string, JsonNode?>
            {
                [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create("true")
            }
        };

        Assert.True(McpToolHelpers.IsExcludedFromMcp(snapshot));
    }

    [Fact]
    public void IsExcludedFromMcp_ReturnsFalse_WhenPropertyIsNull()
    {
        var snapshot = new ResourceSnapshot
        {
            Name = "test",
            Properties = new Dictionary<string, JsonNode?>
            {
                [KnownProperties.Resource.ExcludeFromMcp] = null
            }
        };

        Assert.False(McpToolHelpers.IsExcludedFromMcp(snapshot));
    }

    [Fact]
    public async Task ListResourcesTool_ExcludesResourceWithExcludeFromMcp()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = ApiServiceName,
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains(ApiServiceName, textContent.Text);
        Assert.DoesNotContain(SecretServiceName, textContent.Text);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsNoResourcesFound_WhenAllExcluded()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("No resources found", textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsError_WhenResourceIsExcluded()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ],
            LogLines = [new ResourceLogLine { Content = "secret log", IsError = false, ResourceName = SecretServiceName, LineNumber = 1 }]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse($"\"{SecretServiceName}\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Equal(McpToolHelpers.GetResourceNotAvailableMessage(SecretServiceName), textContent.Text);
    }

    [Fact]
    public async Task ListConsoleLogsTool_ReturnsLogs_WhenResourceIsNotExcluded()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = ApiServiceName,
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running"
                }
            ],
            LogLines = [new ResourceLogLine { Content = "Application started", IsError = false, ResourceName = ApiServiceName, LineNumber = 1 }]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListConsoleLogsTool(monitor, NullLogger<ListConsoleLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse($"\"{ApiServiceName}\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("Application started", textContent.Text);
    }

    [Fact]
    public async Task ExecuteResourceCommandTool_ReturnsError_WhenResourceIsExcluded()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ],
            ExecuteResourceCommandResult = new ExecuteResourceCommandResponse { Success = true }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ExecuteResourceCommandTool(monitor, NullLogger<ExecuteResourceCommandTool>.Instance);

        using var doc = JsonDocument.Parse($$"""{"resourceName": "{{SecretServiceName}}", "commandName": "restart"}""");
        var arguments = doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone());

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Equal(McpToolHelpers.GetResourceNotAvailableMessage(SecretServiceName), textContent.Text);
    }

    [Fact]
    public async Task ListStructuredLogsTool_ReturnsError_WhenSpecificResourceIsExcluded()
    {
        var monitor = CreateMonitorWithDashboardAndExcludedResource();
        var (mockHttpClientFactory, _) = CreateMockHttpWithLogs(SecretServiceName);

        var tool = CreateStructuredLogsTool(monitor, mockHttpClientFactory);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse($"\"{SecretServiceName}\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Equal(McpToolHelpers.GetResourceNotAvailableMessage(SecretServiceName), textContent.Text);
    }

    [Fact]
    public async Task ListStructuredLogsTool_FiltersExcludedResourceLogs_WhenNoResourceSpecified()
    {
        var monitor = CreateMonitorWithDashboardAndExcludedResource();
        var (mockHttpClientFactory, _) = CreateMockHttpWithLogs(ApiServiceName, SecretServiceName);

        var tool = CreateStructuredLogsTool(monitor, mockHttpClientFactory);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains(ApiServiceName, textContent.Text);
        Assert.DoesNotContain(SecretServiceName, textContent.Text);
    }

    [Fact]
    public async Task ListTracesTool_ReturnsError_WhenSpecificResourceIsExcluded()
    {
        var monitor = CreateMonitorWithDashboardAndExcludedResource();
        var (mockHttpClientFactory, _) = CreateMockHttpWithTraces(SecretServiceName);

        var tool = CreateTracesTool(monitor, mockHttpClientFactory);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["resourceName"] = JsonDocument.Parse($"\"{SecretServiceName}\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Equal(McpToolHelpers.GetResourceNotAvailableMessage(SecretServiceName), textContent.Text);
    }

    [Fact]
    public async Task ListTracesTool_FiltersExcludedResourceSpans_WhenNoResourceSpecified()
    {
        var monitor = CreateMonitorWithDashboardAndExcludedResource();
        var (mockHttpClientFactory, _) = CreateMockHttpWithTraces(ApiServiceName, SecretServiceName);

        var tool = CreateTracesTool(monitor, mockHttpClientFactory);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains(ApiServiceName, textContent.Text);
        Assert.DoesNotContain(SecretServiceName, textContent.Text);
    }

    [Fact]
    public async Task ListTraceStructuredLogsTool_FiltersExcludedResourceLogs()
    {
        var monitor = CreateMonitorWithDashboardAndExcludedResource();
        var (mockHttpClientFactory, _) = CreateMockHttpWithLogs(ApiServiceName, SecretServiceName);

        var tool = CreateTraceStructuredLogsTool(monitor, mockHttpClientFactory);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["traceId"] = JsonDocument.Parse("\"abc123def456\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains(ApiServiceName, textContent.Text);
        Assert.DoesNotContain(SecretServiceName, textContent.Text);
    }

    [Fact]
    public async Task ListTraceStructuredLogsTool_ReturnsAllLogs_WhenNoResourcesExcluded()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:5000",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888"]
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = ApiServiceName,
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running"
                }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var (mockHttpClientFactory, _) = CreateMockHttpWithLogs(ApiServiceName, SecretServiceName);

        var tool = CreateTraceStructuredLogsTool(monitor, mockHttpClientFactory);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["traceId"] = JsonDocument.Parse("\"abc123def456\"").RootElement
        };

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains(ApiServiceName, textContent.Text);
        Assert.Contains(SecretServiceName, textContent.Text);
    }

    private static TestAuxiliaryBackchannelMonitor CreateMonitorWithDashboardAndExcludedResource()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            DashboardInfoResponse = new GetDashboardInfoResponse
            {
                ApiBaseUrl = "http://localhost:5000",
                ApiToken = "test-token",
                DashboardUrls = ["http://localhost:18888"]
            },
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = ApiServiceName,
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = SecretServiceName,
                    DisplayName = "Secret Service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ]
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        return monitor;
    }

    private static (MockHttpClientFactory factory, MockHttpMessageHandler handler) CreateMockHttpWithLogs(params string[] serviceNames)
    {
        var resourceLogs = serviceNames.Select(name => new OtlpResourceLogsJson
        {
            Resource = new OtlpResourceJson
            {
                Attributes = [new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = name } }]
            },
            ScopeLogs =
            [
                new OtlpScopeLogsJson
                {
                    Scope = new OtlpInstrumentationScopeJson { Name = "Microsoft.Extensions.Logging" },
                    LogRecords =
                    [
                        new OtlpLogRecordJson
                        {
                            TimeUnixNano = 1706540400000000000,
                            SeverityNumber = 9,
                            SeverityText = "Information",
                            Body = new OtlpAnyValueJson { StringValue = $"Log from {name}" },
                            Attributes = [new OtlpKeyValueJson { Key = OtlpHelpers.AspireLogIdAttribute, Value = new OtlpAnyValueJson { IntValue = 1 } }]
                        }
                    ]
                }
            ]
        }).ToArray();

        var resources = serviceNames.Select(name => new ResourceInfoJson
        {
            Name = name,
            InstanceId = null,
            HasLogs = true,
            HasTraces = true,
            HasMetrics = true
        }).ToArray();

        return CreateMockHttp(resources, new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceLogs = resourceLogs },
            TotalCount = resourceLogs.Length,
            ReturnedCount = resourceLogs.Length
        });
    }

    private static (MockHttpClientFactory factory, MockHttpMessageHandler handler) CreateMockHttpWithTraces(params string[] serviceNames)
    {
        var resourceSpans = serviceNames.Select(name => new OtlpResourceSpansJson
        {
            Resource = new OtlpResourceJson
            {
                Attributes = [new OtlpKeyValueJson { Key = "service.name", Value = new OtlpAnyValueJson { StringValue = name } }]
            },
            ScopeSpans =
            [
                new OtlpScopeSpansJson
                {
                    Scope = new OtlpInstrumentationScopeJson { Name = "TestScope" },
                    Spans =
                    [
                        new OtlpSpanJson
                        {
                            Name = $"GET /{name}",
                            TraceId = "abc123def456",
                            SpanId = "span123",
                            StartTimeUnixNano = 1706540400000000000,
                            EndTimeUnixNano = 1706540400100000000,
                            Kind = 2 // Server
                        }
                    ]
                }
            ]
        }).ToArray();

        var resources = serviceNames.Select(name => new ResourceInfoJson
        {
            Name = name,
            InstanceId = null,
            HasLogs = true,
            HasTraces = true,
            HasMetrics = true
        }).ToArray();

        return CreateMockHttp(resources, new TelemetryApiResponse
        {
            Data = new OtlpTelemetryDataJson { ResourceSpans = resourceSpans },
            TotalCount = resourceSpans.Length,
            ReturnedCount = resourceSpans.Length
        });
    }

    private static (MockHttpClientFactory factory, MockHttpMessageHandler handler) CreateMockHttp(
        ResourceInfoJson[] resources,
        TelemetryApiResponse apiResponseObj)
    {
        var apiResponse = JsonSerializer.Serialize(apiResponseObj, OtlpJsonSerializerContext.Default.TelemetryApiResponse);
        var resourcesResponse = JsonSerializer.Serialize(resources, OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);

        var handler = new MockHttpMessageHandler(request =>
        {
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

        return (new MockHttpClientFactory(handler), handler);
    }

    private static ListStructuredLogsTool CreateStructuredLogsTool(
        TestAuxiliaryBackchannelMonitor monitor,
        IHttpClientFactory httpClientFactory)
    {
        IDashboardInfoProvider dashboardInfoProvider = new BackchannelDashboardInfoProvider(monitor, NullLogger<BackchannelDashboardInfoProvider>.Instance);
        return new ListStructuredLogsTool(
            dashboardInfoProvider,
            monitor,
            httpClientFactory,
            NullLogger<ListStructuredLogsTool>.Instance);
    }

    private static ListTracesTool CreateTracesTool(
        TestAuxiliaryBackchannelMonitor monitor,
        IHttpClientFactory httpClientFactory)
    {
        IDashboardInfoProvider dashboardInfoProvider = new BackchannelDashboardInfoProvider(monitor, NullLogger<BackchannelDashboardInfoProvider>.Instance);
        return new ListTracesTool(
            dashboardInfoProvider,
            monitor,
            httpClientFactory,
            NullLogger<ListTracesTool>.Instance);
    }

    private static ListTraceStructuredLogsTool CreateTraceStructuredLogsTool(
        TestAuxiliaryBackchannelMonitor monitor,
        IHttpClientFactory httpClientFactory)
    {
        IDashboardInfoProvider dashboardInfoProvider = new BackchannelDashboardInfoProvider(monitor, NullLogger<BackchannelDashboardInfoProvider>.Instance);
        return new ListTraceStructuredLogsTool(
            dashboardInfoProvider,
            monitor,
            httpClientFactory,
            NullLogger<ListTraceStructuredLogsTool>.Instance);
    }

}
