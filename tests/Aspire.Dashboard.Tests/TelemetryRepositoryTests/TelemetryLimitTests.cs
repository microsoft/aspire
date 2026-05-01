// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public class TelemetryLimitTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AddTraces_ExceedsResourceLimit_ReportsFailure()
    {
        var repository = CreateRepository(maxResourceCount: 3);

        for (var i = 0; i < 3; i++)
        {
            var addContext = new AddContext();
            repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
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

        Assert.Equal(3, repository.GetResources().Count);

        // Adding a 4th resource should fail.
        var failContext = new AddContext();
        repository.AddTraces(failContext, new RepeatedField<ResourceSpans>
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
        Assert.Equal(3, repository.GetResources().Count);
    }

    [Fact]
    public void AddTraces_ExistingResourceAfterLimitReached_Succeeds()
    {
        var repository = CreateRepository(maxResourceCount: 2);

        // Add 2 resources to fill up the limit.
        for (var i = 0; i < 2; i++)
        {
            var addContext = new AddContext();
            repository.AddTraces(addContext, new RepeatedField<ResourceSpans>
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
        repository.AddTraces(successContext, new RepeatedField<ResourceSpans>
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
    public void AddMetrics_ExceedsInstrumentLimit_ReportsFailure()
    {
        var repository = CreateRepository();

        // Fill instruments up to the limit.
        var metrics = new RepeatedField<Metric>();
        for (var i = 0; i < TelemetryRepository.MaxInstrumentCount; i++)
        {
            metrics.Add(CreateSumMetric(metricName: $"metric{i}", startTime: s_testTime.AddMinutes(1)));
        }

        var addContext = new AddContext();
        repository.AddMetrics(addContext, new RepeatedField<ResourceMetrics>
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

        var resources = repository.GetResources();
        var instruments = repository.GetInstrumentsSummaries(resources[0].ResourceKey);
        Assert.Equal(TelemetryRepository.MaxInstrumentCount, instruments.Count);

        // Adding one more instrument should fail.
        var failContext = new AddContext();
        repository.AddMetrics(failContext, new RepeatedField<ResourceMetrics>
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

        instruments = repository.GetInstrumentsSummaries(resources[0].ResourceKey);
        Assert.Equal(TelemetryRepository.MaxInstrumentCount, instruments.Count);
    }

    [Fact]
    public void AddLogs_ExceedsResourceLimit_FailureCountIsLogRecordCount()
    {
        var repository = CreateRepository(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        repository.AddLogs(setupContext, new RepeatedField<ResourceLogs>
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
        repository.AddLogs(failContext, new RepeatedField<ResourceLogs>
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
    public void AddMetrics_ExceedsResourceLimit_FailureCountIsDataPointCount()
    {
        var repository = CreateRepository(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        repository.AddMetrics(setupContext, new RepeatedField<ResourceMetrics>
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
        repository.AddMetrics(failContext, new RepeatedField<ResourceMetrics>
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
    public void AddTraces_ExceedsResourceLimit_FailureCountIsSpanCount()
    {
        var repository = CreateRepository(maxResourceCount: 1);

        // Fill the single resource slot.
        var setupContext = new AddContext();
        repository.AddTraces(setupContext, new RepeatedField<ResourceSpans>
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
        repository.AddTraces(failContext, new RepeatedField<ResourceSpans>
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
    public void AddLogs_ExceedsScopeLimit_ReportsFailure()
    {
        var repository = CreateRepository();

        // Fill scopes up to the limit.
        var scopeLogs = new RepeatedField<ResourceLogs>();
        var rl = new ResourceLogs { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepository.MaxScopeCount; i++)
        {
            rl.ScopeLogs.Add(new ScopeLogs
            {
                Scope = CreateScope(name: $"logger{i}"),
                LogRecords = { CreateLogRecord() }
            });
        }
        scopeLogs.Add(rl);

        var addContext = new AddContext();
        repository.AddLogs(addContext, scopeLogs);
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail.
        var failContext = new AddContext();
        repository.AddLogs(failContext, new RepeatedField<ResourceLogs>
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
    public void AddTraces_ExceedsScopeLimit_ReportsFailure()
    {
        var repository = CreateRepository();

        // Fill scopes up to the limit.
        var rs = new ResourceSpans { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepository.MaxScopeCount; i++)
        {
            rs.ScopeSpans.Add(new ScopeSpans
            {
                Scope = CreateScope(name: $"tracer{i}"),
                Spans = { CreateSpan($"trace{i}", $"span{i}", s_testTime, s_testTime.AddMinutes(1)) }
            });
        }

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans> { rs });
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail.
        var failContext = new AddContext();
        repository.AddTraces(failContext, new RepeatedField<ResourceSpans>
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
    public void AddMetrics_ExceedsScopeLimit_ReportsFailure()
    {
        var repository = CreateRepository();

        // Fill scopes up to the limit.
        var rm = new ResourceMetrics { Resource = CreateResource() };
        for (var i = 0; i < TelemetryRepository.MaxScopeCount; i++)
        {
            rm.ScopeMetrics.Add(new ScopeMetrics
            {
                Scope = CreateScope(name: $"meter{i}"),
                Metrics = { CreateSumMetric(metricName: $"metric{i}", startTime: s_testTime.AddMinutes(1)) }
            });
        }

        var addContext = new AddContext();
        repository.AddMetrics(addContext, new RepeatedField<ResourceMetrics> { rm });
        Assert.Equal(0, addContext.FailureCount);

        // Adding one more scope should fail. Each metric has 1 data point.
        var failContext = new AddContext();
        repository.AddMetrics(failContext, new RepeatedField<ResourceMetrics>
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
