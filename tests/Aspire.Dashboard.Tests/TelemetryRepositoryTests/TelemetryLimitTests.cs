// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class TelemetryLimitTests : TelemetryRepositoryTestBase
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task AddTraces_ExceedsResourceLimit_ReportsFailure()
    {
        using var repositoryContext = await CreateRepositoryAsync(maxResourceCount: 3);

        for (var i = 0; i < 3; i++)
        {
            var addContext = new AddContext();
            await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource(name: $"app{i}"),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope(),
                            Spans = { CreateSpan("trace1", $"span{i}", s_testTime, s_testTime.AddMinutes(1)) }
                        }
                    }
                }
            });
            Assert.Equal(0, addContext.FailureCount);
        }

        Assert.Equal(3, repositoryContext.Repository.GetResources().Count);

        // Adding a 4th resource should fail.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(failContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app-over-limit"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("trace2", "spanX", s_testTime, s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        Assert.Equal(1, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
        Assert.Equal(3, repositoryContext.Repository.GetResources().Count);
    }

    [Fact]
    public async Task AddTraces_ExistingResourceAfterLimitReached_Succeeds()
    {
        using var repositoryContext = await CreateRepositoryAsync(maxResourceCount: 2);

        // Add 2 resources to fill up the limit.
        for (var i = 0; i < 2; i++)
        {
            var addContext = new AddContext();
            await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource(name: $"app{i}"),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope(),
                            Spans = { CreateSpan("trace1", $"span{i}", s_testTime, s_testTime.AddMinutes(1)) }
                        }
                    }
                }
            });
            Assert.Equal(0, addContext.FailureCount);
        }

        // Adding data for an existing resource should still succeed.
        var successContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(successContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app0"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("trace2", "spanNew", s_testTime, s_testTime.AddMinutes(2)) }
                    }
                }
            }
        });

        Assert.Equal(0, successContext.FailureCount);
        Assert.Equal(1, successContext.SuccessCount);
    }

    [Fact]
    public async Task AddMetrics_ExceedsInstrumentLimit_ReportsFailure()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        // Fill instruments up to the limit.
        var metrics = new RepeatedField<Metric>();
        for (var i = 0; i < TelemetryRepositoryLimits.MaxInstrumentCount; i++)
        {
            var metric = CreateSumMetric(metricName: $"metric{i}", startTime: s_testTime.AddMinutes(1));
            // This test only needs distinct instrument definitions to reach the instrument limit.
            // Remove the helper-created data point to avoid storing 10,000 unrelated metric values.
            metric.Sum.DataPoints.Clear();
            metrics.Add(metric);
        }

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { metrics }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        var instruments = repositoryContext.Repository.GetInstrumentSummaries(resources[0].ResourceKey);
        Assert.Equal(TelemetryRepositoryLimits.MaxInstrumentCount, instruments.Count);

        // Adding one more instrument should fail.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(failContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { CreateSumMetric(metricName: "over-limit-metric", startTime: s_testTime.AddMinutes(2)) }
                    }
                }
            }
        });

        Assert.Equal(1, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);

        instruments = repositoryContext.Repository.GetInstrumentSummaries(resources[0].ResourceKey);
        Assert.Equal(TelemetryRepositoryLimits.MaxInstrumentCount, instruments.Count);
    }

    [Fact]
    public async Task AddMetrics_ExceedsKnownAttributeKeyLimit_ReportsFailure()
    {
        var attributeCount = TelemetryRepositoryLimits.MaxKnownAttributeValueCount + 1;
        using var repositoryContext = await CreateRepositoryAsync(maxAttributeCount: attributeCount);
        var attributes = Enumerable.Range(0, attributeCount)
            .Select(index => KeyValuePair.Create($"key-{index:D5}", $"value-{index:D5}"))
            .ToArray();
        var addContext = new AddContext();

        await repositoryContext.Repository.AsWriter().AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { CreateSumMetric(metricName: "test", startTime: s_testTime, attributes: attributes) }
                    }
                }
            }
        });

        Assert.Equal(1, addContext.FailureCount);
        Assert.Equal(0, addContext.SuccessCount);
    }

    [Fact]
    public async Task AddMetrics_ExceedsKnownAttributeValuesPerKeyLimit_ReportsFailure()
    {
        var testSink = new TestSink();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TestLoggerProvider(testSink)));
        using var repositoryContext = await CreateRepositoryAsync(loggerFactory: loggerFactory);
        var metrics = Enumerable.Range(0, TelemetryRepositoryLimits.MaxKnownAttributeValuesPerKey + 1)
            .Select(index => CreateSumMetric(
                metricName: "test",
                startTime: s_testTime,
                attributes: [KeyValuePair.Create("key", $"value-{index:D5}")]));
        var addContext = new AddContext();

        await repositoryContext.Repository.AsWriter().AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { metrics }
                    }
                }
            }
        });

        Assert.Equal(1, addContext.FailureCount);
        Assert.Equal(TelemetryRepositoryLimits.MaxKnownAttributeValuesPerKey, addContext.SuccessCount);
        var write = Assert.Single(testSink.Writes, write => write.Message == "Error adding metric.");
        Assert.Equal(
            $"Known attribute value limit of {TelemetryRepositoryLimits.MaxKnownAttributeValuesPerKey} reached for key 'key'.",
            write.Exception!.Message);
    }

    [Fact]
    public async Task AddLogs_ExceedsResourceLimit_FailureCountIsLogRecordCount()
    {
        using var repositoryContext = await CreateRepositoryAsync(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddLogsAsync(setupContext, new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "app0"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("logger"),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });
        Assert.Equal(0, setupContext.FailureCount);

        // Attempt to add logs for a new resource with multiple scopes and records.
        // FailureCount must equal total log records, not number of scopes.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddLogsAsync(failContext, new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "app-over-limit"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("loggerA"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "a1"),
                            CreateLogRecord(message: "a2"),
                            CreateLogRecord(message: "a3")
                        }
                    },
                    new ScopeLogs
                    {
                        Scope = CreateScope("loggerB"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "b1"),
                            CreateLogRecord(message: "b2")
                        }
                    }
                }
            }
        });

        Assert.Equal(5, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }

    [Fact]
    public async Task AddMetrics_ExceedsResourceLimit_FailureCountIsDataPointCount()
    {
        using var repositoryContext = await CreateRepositoryAsync(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(setupContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "app0"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "meter"),
                        Metrics = { CreateSumMetric(metricName: "m0", startTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });
        Assert.Equal(0, setupContext.FailureCount);

        // Attempt to add metrics for a new resource with multiple scopes and metrics.
        // FailureCount must equal total data points, not number of metrics.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(failContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "app-over-limit"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "meterA"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "m1", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "m2", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "m3", startTime: s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "meterB"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "m4", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "m5", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Each CreateSumMetric produces 1 data point, so 5 metrics = 5 data points.
        Assert.Equal(5, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }

    [Fact]
    public async Task AddTraces_ExceedsResourceLimit_FailureCountIsSpanCount()
    {
        using var repositoryContext = await CreateRepositoryAsync(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(setupContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app0"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("trace1", "span0", s_testTime, s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });
        Assert.Equal(0, setupContext.FailureCount);

        // Attempt to add traces for a new resource with multiple scopes and spans.
        // FailureCount must equal total spans, not number of scopes.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(failContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "app-over-limit"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan("trace2", "spanA1", s_testTime, s_testTime.AddMinutes(1)),
                            CreateSpan("trace2", "spanA2", s_testTime, s_testTime.AddMinutes(1))
                        }
                    },
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan("trace3", "spanB1", s_testTime, s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        Assert.Equal(3, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }

    [Fact]
    public async Task AddLogs_ExceedsScopeLimit_ReportsFailure()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        // Fill scopes up to the limit.
        var scopeLogs = new RepeatedField<ResourceLogs>();
        var rl = new ResourceLogs { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepositoryLimits.MaxScopeCount; i++)
        {
            rl.ScopeLogs.Add(new ScopeLogs
            {
                Scope = CreateScope(name: $"logger{i}")
            });
        }
        scopeLogs.Add(rl);

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddLogsAsync(addContext, scopeLogs);
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddLogsAsync(failContext, new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(name: "over-limit-logger"),
                        LogRecords =
                        {
                            CreateLogRecord(message: "a"),
                            CreateLogRecord(message: "b"),
                            CreateLogRecord(message: "c")
                        }
                    }
                }
            }
        });

        Assert.Equal(3, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }

    [Fact]
    public async Task AddTraces_ExceedsScopeLimit_ReportsFailure()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        // Fill scopes up to the limit.
        var rs = new ResourceSpans { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepositoryLimits.MaxScopeCount; i++)
        {
            rs.ScopeSpans.Add(new ScopeSpans
            {
                Scope = CreateScope(name: $"tracer{i}")
            });
        }

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans> { rs });
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(failContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(name: "over-limit-tracer"),
                        Spans =
                        {
                            CreateSpan("traceX", "spanX1", s_testTime, s_testTime.AddMinutes(1)),
                            CreateSpan("traceX", "spanX2", s_testTime, s_testTime.AddMinutes(2))
                        }
                    }
                }
            }
        });

        Assert.Equal(2, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }

    [Fact]
    public async Task AddMetrics_ExceedsScopeLimit_ReportsFailure()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        // Fill scopes up to the limit.
        var rm = new ResourceMetrics { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepositoryLimits.MaxScopeCount; i++)
        {
            rm.ScopeMetrics.Add(new ScopeMetrics
            {
                Scope = CreateScope(name: $"meter{i}")
            });
        }

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics> { rm });
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail. Each metric has 1 data point.
        var failContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddMetricsAsync(failContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "over-limit-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "m1", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "m2", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // 2 metrics × 1 data point each = 2 rejected data points.
        Assert.Equal(2, failContext.FailureCount);
        Assert.Equal(0, failContext.SuccessCount);
    }
}

public sealed class SqliteTelemetryLimitTests : TelemetryLimitTests
{
}
