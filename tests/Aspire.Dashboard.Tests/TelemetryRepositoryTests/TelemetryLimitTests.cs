// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
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
}
