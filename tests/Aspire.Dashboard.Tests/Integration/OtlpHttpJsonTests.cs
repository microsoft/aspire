// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text;
using Aspire.Dashboard.Authentication.OtlpApiKey;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Otlp.Http;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Hosting;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class OtlpHttpJsonTests
{
    /// <summary>
    /// Example trace JSON from https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/examples/trace.json
    /// </summary>
    private const string ExampleTraceJson = """
        {
          "resourceSpans": [
            {
              "resource": {
                "attributes": [
                  {
                    "key": "service.name",
                    "value": {
                      "stringValue": "my.service"
                    }
                  }
                ]
              },
              "scopeSpans": [
                {
                  "scope": {
                    "name": "my.library",
                    "version": "1.0.0",
                    "attributes": [
                      {
                        "key": "my.scope.attribute",
                        "value": {
                          "stringValue": "some scope attribute"
                        }
                      }
                    ]
                  },
                  "spans": [
                    {
                      "traceId": "5B8EFFF798038103D269B633813FC60C",
                      "spanId": "EEE19B7EC3C1B174",
                      "parentSpanId": "EEE19B7EC3C1B173",
                      "name": "I'm a server span",
                      "startTimeUnixNano": "1544712660000000000",
                      "endTimeUnixNano": "1544712661000000000",
                      "kind": 2,
                      "attributes": [
                        {
                          "key": "my.span.attr",
                          "value": {
                            "stringValue": "some value"
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Example logs JSON from https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/examples/logs.json
    /// </summary>
    private const string ExampleLogsJson = """
        {
          "resourceLogs": [
            {
              "resource": {
                "attributes": [
                  {
                    "key": "service.name",
                    "value": {
                      "stringValue": "my.service"
                    }
                  }
                ]
              },
              "scopeLogs": [
                {
                  "scope": {
                    "name": "my.library",
                    "version": "1.0.0",
                    "attributes": [
                      {
                        "key": "my.scope.attribute",
                        "value": {
                          "stringValue": "some scope attribute"
                        }
                      }
                    ]
                  },
                  "logRecords": [
                    {
                      "timeUnixNano": "1544712660300000000",
                      "observedTimeUnixNano": "1544712660300000000",
                      "severityNumber": 10,
                      "severityText": "Information",
                      "traceId": "5B8EFFF798038103D269B633813FC60C",
                      "spanId": "EEE19B7EC3C1B174",
                      "body": {
                        "stringValue": "Example log record"
                      },
                      "attributes": [
                        {
                          "key": "string.attribute",
                          "value": {
                            "stringValue": "some string"
                          }
                        },
                        {
                          "key": "boolean.attribute",
                          "value": {
                            "boolValue": true
                          }
                        },
                        {
                          "key": "int.attribute",
                          "value": {
                            "intValue": "10"
                          }
                        },
                        {
                          "key": "double.attribute",
                          "value": {
                            "doubleValue": 637.704
                          }
                        },
                        {
                          "key": "array.attribute",
                          "value": {
                            "arrayValue": {
                              "values": [
                                {
                                  "stringValue": "many"
                                },
                                {
                                  "stringValue": "values"
                                }
                              ]
                            }
                          }
                        },
                        {
                          "key": "map.attribute",
                          "value": {
                            "kvlistValue": {
                              "values": [
                                {
                                  "key": "some.map.key",
                                  "value": {
                                    "stringValue": "some value"
                                  }
                                }
                              ]
                            }
                          }
                        }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Example events JSON from https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/examples/events.json
    /// Events are log records with an eventName field.
    /// </summary>
    private const string ExampleEventsJson = """
        {
          "resourceLogs": [
            {
              "resource": {
                "attributes": [
                  {
                    "key": "service.name",
                    "value": {
                      "stringValue": "my.service"
                    }
                  }
                ]
              },
              "scopeLogs": [
                {
                  "scope": {
                    "name": "my.library",
                    "version": "1.0.0",
                    "attributes": [
                      {
                        "key": "my.scope.attribute",
                        "value": {
                          "stringValue": "some scope attribute"
                        }
                      }
                    ]
                  },
                  "logRecords": [
                    {
                      "eventName": "browser.page_view",
                      "timeUnixNano": "1544712660300000000",
                      "observedTimeUnixNano": "1544712660300000000",
                      "severityNumber": 9,
                      "severityText": "test severity text",
                      "attributes": [
                        {
                          "key": "event.attribute",
                          "value": {
                            "stringValue": "some event attribute"
                          }
                        }
                      ],
                      "body": {
                        "kvlistValue": {
                          "values": [
                            {
                              "key": "type",
                              "value": {
                                "intValue": "0"
                              }
                            },
                            {
                              "key": "url",
                              "value": {
                                "stringValue": "https://www.example.com/page"
                              }
                            },
                            {
                              "key": "referrer",
                              "value": {
                                "stringValue": "https://www.google.com"
                              }
                            },
                            {
                              "key": "title",
                              "value": {
                                "stringValue": "Example Page Title"
                              }
                            }
                          ]
                        }
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Metrics JSON with histogram bucketCounts as numbers instead of strings.
    /// Some OTLP exporters send small uint64 values as JSON numbers rather than strings,
    /// even though the protojson spec requires string encoding for uint64 fields.
    /// </summary>
    private const string MetricsJsonWithNumericBucketCounts = """
        {
          "resourceMetrics": [
            {
              "resource": {
                "attributes": [
                  {
                    "key": "service.name",
                    "value": {
                      "stringValue": "copilot-chat"
                    }
                  }
                ]
              },
              "scopeMetrics": [
                {
                  "scope": {
                    "name": "copilot-chat",
                    "version": "0.45.2026042102"
                  },
                  "metrics": [
                    {
                      "name": "copilot_chat.tool.call.duration",
                      "description": "",
                      "unit": "",
                      "histogram": {
                        "aggregationTemporality": 2,
                        "dataPoints": [
                          {
                            "attributes": [
                              {
                                "key": "gen_ai.tool.name",
                                "value": {
                                  "stringValue": "manage_todo_list"
                                }
                              }
                            ],
                            "bucketCounts": [
                              0,
                              2,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0,
                              0
                            ],
                            "explicitBounds": [
                              0,
                              5,
                              10,
                              25,
                              50,
                              75,
                              100,
                              250,
                              500,
                              750,
                              1000,
                              2500,
                              5000,
                              7500,
                              10000
                            ],
                            "count": 2,
                            "sum": 5,
                            "min": 2,
                            "max": 3,
                            "startTimeUnixNano": "1776863423819000000",
                            "timeUnixNano": "1776864142921000000"
                          }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Example metrics JSON from https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/examples/metrics.json
    /// </summary>
    private const string ExampleMetricsJson = """
        {
          "resourceMetrics": [
            {
              "resource": {
                "attributes": [
                  {
                    "key": "service.name",
                    "value": {
                      "stringValue": "my.service"
                    }
                  }
                ]
              },
              "scopeMetrics": [
                {
                  "scope": {
                    "name": "my.library",
                    "version": "1.0.0",
                    "attributes": [
                      {
                        "key": "my.scope.attribute",
                        "value": {
                          "stringValue": "some scope attribute"
                        }
                      }
                    ]
                  },
                  "metrics": [
                    {
                      "name": "my.counter",
                      "unit": "1",
                      "description": "I am a Counter",
                      "sum": {
                        "aggregationTemporality": 1,
                        "isMonotonic": true,
                        "dataPoints": [
                          {
                            "asDouble": 5,
                            "startTimeUnixNano": "1544712660300000000",
                            "timeUnixNano": "1544712660300000000",
                            "attributes": [
                              {
                                "key": "my.counter.attr",
                                "value": {
                                  "stringValue": "some value"
                                }
                              }
                            ]
                          }
                        ]
                      }
                    },
                    {
                      "name": "my.gauge",
                      "unit": "1",
                      "description": "I am a Gauge",
                      "gauge": {
                        "dataPoints": [
                          {
                            "asDouble": 10,
                            "timeUnixNano": "1544712660300000000",
                            "attributes": [
                              {
                                "key": "my.gauge.attr",
                                "value": {
                                  "stringValue": "some value"
                                }
                              }
                            ]
                          }
                        ]
                      }
                    },
                    {
                      "name": "my.histogram",
                      "unit": "1",
                      "description": "I am a Histogram",
                      "histogram": {
                        "aggregationTemporality": 1,
                        "dataPoints": [
                          {
                            "startTimeUnixNano": "1544712660300000000",
                            "timeUnixNano": "1544712660300000000",
                            "count": "2",
                            "sum": 2,
                            "bucketCounts": ["1", "1"],
                            "explicitBounds": [1],
                            "min": 0,
                            "max": 2,
                            "attributes": [
                              {
                                "key": "my.histogram.attr",
                                "value": {
                                  "stringValue": "some value"
                                }
                              }
                            ]
                          }
                        ]
                      }
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private readonly ITestOutputHelper _testOutputHelper;

    public OtlpHttpJsonTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    private static OtlpResource AssertMyServiceResource(TelemetryRepository telemetryRepository)
    {
        var resources = telemetryRepository.GetResourcesByName("my.service");
        var resource = Assert.Single(resources);
        Assert.Equal("my.service", resource.ResourceName);
        Assert.False(resource.UninstrumentedPeer);

        return resource;
    }

    [Fact]
    public async Task CallService_Traces_JsonContentType_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleTraceJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/traces", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);

        var responseBody = await responseMessage.Content.ReadAsStringAsync().DefaultTimeout();
        Assert.NotNull(responseBody);

        // Verify the response is valid JSON and can be deserialized
        var response = System.Text.Json.JsonSerializer.Deserialize(responseBody, OtlpJsonSerializerContext.Default.OtlpExportTraceServiceResponseJson);
        Assert.NotNull(response);

        // Verify data was stored in the repository
        var telemetryRepository = app.Services.GetRequiredService<TelemetryRepository>();
        var resource = AssertMyServiceResource(telemetryRepository);

        var traces = telemetryRepository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            FilterText = string.Empty,
            Filters = []
        });
        Assert.NotEmpty(traces.PagedResult.Items);

        var trace = traces.PagedResult.Items.First();
        Assert.Equal("5b8efff798038103d269b633813fc60c", trace.TraceId);
        Assert.Single(trace.Spans);

        var span = trace.Spans.First();
        Assert.Equal("I'm a server span", span.Name);
        Assert.Equal("eee19b7ec3c1b174", span.SpanId);
        Assert.Equal("eee19b7ec3c1b173", span.ParentSpanId);
        Assert.Equal(Aspire.Dashboard.Otlp.Model.OtlpSpanKind.Server, span.Kind);
        Assert.Equal(new DateTime(2018, 12, 13, 14, 51, 0, DateTimeKind.Utc), span.StartTime);
        Assert.Equal(new DateTime(2018, 12, 13, 14, 51, 1, DateTimeKind.Utc), span.EndTime);
        Assert.Equal(TimeSpan.FromSeconds(1), span.Duration);
        Assert.Collection(span.Attributes,
            a =>
            {
                Assert.Equal("my.span.attr", a.Key);
                Assert.Equal("some value", a.Value);
            });
        Assert.Equal("my.library", span.Scope.Name);
        Assert.Equal("1.0.0", span.Scope.Version);
    }

    [Fact]
    public async Task CallService_Logs_JsonContentType_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleLogsJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/logs", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);

        var responseBody = await responseMessage.Content.ReadAsStringAsync().DefaultTimeout();
        Assert.NotNull(responseBody);

        // Verify the response is valid JSON and can be deserialized
        var response = System.Text.Json.JsonSerializer.Deserialize(responseBody, OtlpJsonSerializerContext.Default.OtlpExportLogsServiceResponseJson);
        Assert.NotNull(response);

        // Verify data was stored in the repository
        var telemetryRepository = app.Services.GetRequiredService<TelemetryRepository>();
        var resource = AssertMyServiceResource(telemetryRepository);

        var logs = telemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.NotEmpty(logs.Items);

        var log = logs.Items.First();
        Assert.Equal("Example log record", log.Message);
        Assert.Equal(LogLevel.Information, log.Severity);
        Assert.Equal(new DateTime(2018, 12, 13, 14, 51, 0, 300, DateTimeKind.Utc), log.TimeStamp);
        Assert.Equal("5b8efff798038103d269b633813fc60c", log.TraceId);
        Assert.Equal("eee19b7ec3c1b174", log.SpanId);
        Assert.Equal("my.library", log.Scope.Name);
        Assert.Equal("1.0.0", log.Scope.Version);
        Assert.Collection(log.Attributes,
            a =>
            {
                Assert.Equal("string.attribute", a.Key);
                Assert.Equal("some string", a.Value);
            },
            a =>
            {
                Assert.Equal("boolean.attribute", a.Key);
                Assert.Equal("true", a.Value);
            },
            a =>
            {
                Assert.Equal("int.attribute", a.Key);
                Assert.Equal("10", a.Value);
            },
            a =>
            {
                Assert.Equal("double.attribute", a.Key);
                Assert.Equal("637.704", a.Value);
            },
            a =>
            {
                Assert.Equal("array.attribute", a.Key);
            },
            a =>
            {
                Assert.Equal("map.attribute", a.Key);
            });
    }

    [Fact]
    public async Task CallService_Events_JsonContentType_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleEventsJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/logs", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);

        var responseBody = await responseMessage.Content.ReadAsStringAsync().DefaultTimeout();
        Assert.NotNull(responseBody);

        // Verify the response is valid JSON and can be deserialized
        var response = System.Text.Json.JsonSerializer.Deserialize(responseBody, OtlpJsonSerializerContext.Default.OtlpExportLogsServiceResponseJson);
        Assert.NotNull(response);

        // Verify data was stored in the repository (events are stored as logs)
        var telemetryRepository = app.Services.GetRequiredService<TelemetryRepository>();
        var resource = AssertMyServiceResource(telemetryRepository);

        var logs = telemetryRepository.GetLogs(new GetLogsContext
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.NotEmpty(logs.Items);

        var log = logs.Items.First();
        Assert.Equal(LogLevel.Information, log.Severity);
        Assert.Equal(new DateTime(2018, 12, 13, 14, 51, 0, 300, DateTimeKind.Utc), log.TimeStamp);
        Assert.Equal("my.library", log.Scope.Name);
        Assert.Collection(log.Attributes,
            a =>
            {
                Assert.Equal("event.attribute", a.Key);
                Assert.Equal("some event attribute", a.Value);
            });
    }

    [Fact]
    public async Task CallService_Metrics_JsonContentType_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleMetricsJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/metrics", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);

        var responseBody = await responseMessage.Content.ReadAsStringAsync().DefaultTimeout();
        Assert.NotNull(responseBody);

        // Verify the response is valid JSON and can be deserialized
        var response = System.Text.Json.JsonSerializer.Deserialize(responseBody, OtlpJsonSerializerContext.Default.OtlpExportMetricsServiceResponseJson);
        Assert.NotNull(response);

        // Verify data was stored in the repository
        var telemetryRepository = app.Services.GetRequiredService<TelemetryRepository>();
        var resource = AssertMyServiceResource(telemetryRepository);

        var instruments = resource.GetInstrumentsSummary();
        Assert.Collection(instruments,
            counter =>
            {
                Assert.Equal("my.counter", counter.Name);
                Assert.Equal("I am a Counter", counter.Description);
                Assert.Equal("1", counter.Unit);
                Assert.Equal("my.library", counter.Parent.Name);
            },
            gauge =>
            {
                Assert.Equal("my.gauge", gauge.Name);
                Assert.Equal("I am a Gauge", gauge.Description);
                Assert.Equal("1", gauge.Unit);
            },
            histogram =>
            {
                Assert.Equal("my.histogram", histogram.Name);
                Assert.Equal("I am a Histogram", histogram.Description);
                Assert.Equal("1", histogram.Unit);
            });
    }

    [Fact]
    public async Task CallService_Metrics_NumericBucketCounts_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(MetricsJsonWithNumericBucketCounts, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/metrics", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);

        var responseBody = await responseMessage.Content.ReadAsStringAsync().DefaultTimeout();
        Assert.NotNull(responseBody);

        var response = System.Text.Json.JsonSerializer.Deserialize(responseBody, OtlpJsonSerializerContext.Default.OtlpExportMetricsServiceResponseJson);
        Assert.NotNull(response);

        var telemetryRepository = app.Services.GetRequiredService<TelemetryRepository>();
        var resources = telemetryRepository.GetResourcesByName("copilot-chat");
        var resource = Assert.Single(resources);

        var instruments = resource.GetInstrumentsSummary();
        var summary = Assert.Single(instruments);
        Assert.Equal("copilot_chat.tool.call.duration", summary.Name);

        var instrumentData = telemetryRepository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource.ResourceKey,
            InstrumentName = "copilot_chat.tool.call.duration",
            MeterName = "copilot-chat",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });
        Assert.NotNull(instrumentData);

        var dimension = Assert.Single(instrumentData.Dimensions);
        var metricValue = Assert.Single(dimension.Values);
        var histogramValue = Assert.IsType<HistogramValue>(metricValue);

        Assert.Equal(2UL, histogramValue.Count);
        Assert.Equal(5.0, histogramValue.Sum);

        Assert.Collection(histogramValue.ExplicitBounds,
            b => Assert.Equal(0, b),
            b => Assert.Equal(5, b),
            b => Assert.Equal(10, b),
            b => Assert.Equal(25, b),
            b => Assert.Equal(50, b),
            b => Assert.Equal(75, b),
            b => Assert.Equal(100, b),
            b => Assert.Equal(250, b),
            b => Assert.Equal(500, b),
            b => Assert.Equal(750, b),
            b => Assert.Equal(1000, b),
            b => Assert.Equal(2500, b),
            b => Assert.Equal(5000, b),
            b => Assert.Equal(7500, b),
            b => Assert.Equal(10000, b));

        Assert.Collection(histogramValue.Values,
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(2UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c),
            c => Assert.Equal(0UL, c));
    }

    [Fact]
    public async Task CallService_JsonContentType_WithApiKey_Success()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleLogsJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/v1/logs");
        requestMessage.Content = content;
        requestMessage.Headers.TryAddWithoutValidation(OtlpApiKeyAuthenticationHandler.ApiKeyHeaderName, apiKey);

        // Act
        var responseMessage = await httpClient.SendAsync(requestMessage).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task CallService_JsonContentType_WithoutApiKey_Failure()
    {
        // Arrange
        var apiKey = "TestKey123!";
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper, config =>
        {
            config[DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = OtlpAuthMode.ApiKey.ToString();
            config[DashboardConfigNames.DashboardOtlpPrimaryApiKeyName.ConfigKey] = apiKey;
        });
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent(ExampleLogsJson, Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/logs", content).DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, responseMessage.StatusCode);
    }

    [Fact]
    public async Task CallService_JsonContentType_InvalidJson_BadRequest()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent("{ invalid json }", Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/logs", content).DefaultTimeout();

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, responseMessage.StatusCode);
    }

    [Fact]
    public async Task CallService_JsonContentType_EmptyBody_Success()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(_testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        using var httpClient = IntegrationTestHelpers.CreateHttpClient($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}");

        var content = new StringContent("{}", Encoding.UTF8, OtlpHttpEndpointsBuilder.JsonContentType);

        // Act
        var responseMessage = await httpClient.PostAsync("/v1/logs", content).DefaultTimeout();
        responseMessage.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(OtlpHttpEndpointsBuilder.JsonContentType, responseMessage.Content.Headers.ContentType?.MediaType);
    }
}
