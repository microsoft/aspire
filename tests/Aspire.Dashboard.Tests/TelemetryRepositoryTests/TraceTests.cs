// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests.Shared.DashboardModel;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class TraceTests : TelemetryRepositoryTestBase
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(OtlpSpanKind.Server, Span.Types.SpanKind.Server)]
    [InlineData(OtlpSpanKind.Client, Span.Types.SpanKind.Client)]
    [InlineData(OtlpSpanKind.Consumer, Span.Types.SpanKind.Consumer)]
    [InlineData(OtlpSpanKind.Producer, Span.Types.SpanKind.Producer)]
    [InlineData(OtlpSpanKind.Internal, Span.Types.SpanKind.Internal)]
    [InlineData(OtlpSpanKind.Internal, Span.Types.SpanKind.Unspecified)]
    [InlineData(OtlpSpanKind.Unspecified, (Span.Types.SpanKind)1000)]
    public void ConvertSpanKind(OtlpSpanKind expected, Span.Types.SpanKind value)
    {
        var result = OtlpHelpers.ConvertSpanKind(value);
        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task AddTraces()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                AssertId("1-1", trace.RootSpan!.SpanId);
                Assert.Equal(2, trace.Spans.Count);
            });
    }

    [Fact]
    public async Task GetTraceSummaries_ReturnsPageData()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "frontend", instanceId: "frontend-1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(3),
                                attributes:
                                [
                                    KeyValuePair.Create("custom", "match"),
                                    KeyValuePair.Create("gen_ai.system", "test")
                                ],
                                status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(
                                traceId: "2",
                                spanId: "2-1",
                                startTime: s_testTime.AddMinutes(2),
                                endTime: s_testTime.AddMinutes(4),
                                attributes: [KeyValuePair.Create("custom", "other")])
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "backend", instanceId: "backend-1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: s_testTime.AddMinutes(2),
                                endTime: s_testTime.AddMinutes(6),
                                status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

        var request = new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            TraceNameFilterText = "frontend",
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = "custom",
                    Condition = FilterCondition.Equals,
                    Value = "match"
                }
            ]
        };

        var summaries = await repositoryContext.Repository.GetTraceSummariesAsync(request, cancellationToken: CancellationToken.None);
        var traces = await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None);

        Assert.Equal(traces.PagedResult.TotalItemCount, summaries.PagedResult.TotalItemCount);
        Assert.Equal(traces.MaxDuration, summaries.MaxDuration);
        var summary = Assert.Single(summaries.PagedResult.Items);
        AssertId("1", summary.TraceId);
        Assert.Equal("frontend: Test span. Id: 1-1", summary.FullName);
        Assert.Equal(s_testTime.AddMinutes(1), summary.StartTime);
        Assert.Equal(TimeSpan.FromMinutes(5), summary.Duration);
        Assert.Equal(new ResourceKey("frontend", "frontend-1"), summary.RootResource.ResourceKey);
        Assert.True(summary.HasError);
        Assert.True(summary.HasGenAI);
        Assert.Collection(summary.Resources,
            resource =>
            {
                Assert.Equal(new ResourceKey("frontend", "frontend-1"), resource.Resource.ResourceKey);
                Assert.Equal(1, resource.TotalSpans);
                Assert.Equal(1, resource.ErroredSpans);
            },
            resource =>
            {
                Assert.Equal(new ResourceKey("backend", "backend-1"), resource.Resource.ResourceKey);
                Assert.Equal(1, resource.TotalSpans);
                Assert.Equal(0, resource.ErroredSpans);
            });

        var latestRequest = new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = [],
            LatestItemCount = 1
        };
        var latestSummaries = await repositoryContext.Repository.GetTraceSummariesAsync(latestRequest, cancellationToken: CancellationToken.None);
        var latestTraces = await repositoryContext.Repository.GetTracesAsync(latestRequest, cancellationToken: CancellationToken.None);
        Assert.Equal(2, latestSummaries.PagedResult.TotalItemCount);
        var latestSummary = Assert.Single(latestSummaries.PagedResult.Items);
        AssertId("2", latestSummary.TraceId);
        Assert.Equal(s_testTime.AddMinutes(2), latestSummary.StartTime);
        Assert.Equal(latestTraces.PagedResult.TotalItemCount, latestSummaries.PagedResult.TotalItemCount);
        var latestTrace = Assert.Single(latestTraces.PagedResult.Items);
        AssertId("2", latestTrace.TraceId);
        Assert.Equal(s_testTime.AddMinutes(2), latestTrace.FirstSpan.StartTime);

        var emptyPage = await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = request.ResourceKeys,
            StartIndex = 10,
            Count = request.Count,
            TraceNameFilterText = request.TraceNameFilterText,
            Filters = request.Filters
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(emptyPage.PagedResult.Items);
        Assert.Equal(1, emptyPage.PagedResult.TotalItemCount);
        Assert.Equal(TimeSpan.FromMinutes(5), emptyPage.MaxDuration);
    }

    [Fact]
    public async Task GetTraceSummaries_SpanTypeFiltersMatchGetTraces()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("TestScope"),
                        Spans =
                        {
                            CreateSpan(traceId: "http", spanId: "1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("http.request.method", "GET")]),
                            CreateSpan(traceId: "database", spanId: "2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(2), attributes: [KeyValuePair.Create("db.system", "sqlite")]),
                            CreateSpan(traceId: "messaging", spanId: "3", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), attributes: [KeyValuePair.Create("messaging.system", "kafka")]),
                            CreateSpan(traceId: "rpc", spanId: "4", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(4), attributes: [KeyValuePair.Create("rpc.system", "grpc")]),
                            CreateSpan(traceId: "genai", spanId: "5", startTime: s_testTime.AddMinutes(4), endTime: s_testTime.AddMinutes(5), attributes: [KeyValuePair.Create("gen_ai.provider.name", "test")])
                        }
                    },
                    new ScopeSpans
                    {
                        Scope = CreateScope("azure.messaging"),
                        Spans =
                        {
                            CreateSpan(traceId: "cloud-azure", spanId: "6", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(6))
                        }
                    },
                    new ScopeSpans
                    {
                        Scope = CreateScope("AWSSDK"),
                        Spans =
                        {
                            CreateSpan(traceId: "cloud-aws", spanId: "7", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(7))
                        }
                    },
                    new ScopeSpans
                    {
                        Scope = CreateScope("Azureish"),
                        Spans =
                        {
                            CreateSpan(traceId: "other", spanId: "8", startTime: s_testTime.AddMinutes(7), endTime: s_testTime.AddMinutes(8), attributes: [KeyValuePair.Create("http.request.method", string.Empty)])
                        }
                    }
                }
            }
        ]);

        var cases = new (SpanType SpanType, string[] ExpectedTraceIds)[]
        {
            (SpanType.Http, ["http"]),
            (SpanType.Database, ["database"]),
            (SpanType.Messaging, ["messaging"]),
            (SpanType.Rpc, ["rpc"]),
            (SpanType.GenAI, ["genai"]),
            (SpanType.Cloud, ["cloud-azure", "cloud-aws"]),
            (SpanType.Other, ["other"])
        };
        foreach (var (spanType, expectedTraceIds) in cases)
        {
            var request = new GetTracesRequest
            {
                ResourceKeys = [],
                StartIndex = 0,
                Count = 10,
                Filters = [spanType.Filter]
            };

            var traces = (await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items;
            var summaries = (await repositoryContext.Repository.GetTraceSummariesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items;

            Assert.Equal(expectedTraceIds.Select(GetHexId), traces.Select(trace => trace.TraceId));
            Assert.Equal(expectedTraceIds.Select(GetHexId), summaries.Select(summary => summary.TraceId));
        }
    }

    [Fact]
    public async Task GetTraceSummaries_LateParent_PreservesResourceOrder()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "z-child"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        ]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "a-parent"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(5),
                                endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        ]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        var trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(summary.TraceId));

        Assert.Equal(
            TraceHelpers.GetOrderedResources(trace).Select(resource => resource.Resource.ResourceKey),
            summary.Resources.Select(resource => resource.Resource.ResourceKey));
        Assert.Collection(summary.Resources,
            resource => Assert.Equal("a-parent", resource.Resource.ResourceName),
            resource => Assert.Equal("z-child", resource.Resource.ResourceName));
    }

    [Fact]
    public async Task GetTraceSummaries_SameOrderTime_UninstrumentedPeerAfterInstrumentedResource()
    {
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: _ => ("dashboard.db", null));
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "unknown_service:Aspire.Dashboard"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "dashboard.db")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        ]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);

        Assert.Collection(summary.Resources,
            resource =>
            {
                Assert.Equal("unknown_service:Aspire.Dashboard", resource.Resource.ResourceName);
                Assert.False(resource.Resource.UninstrumentedPeer);
            },
            resource =>
            {
                Assert.Equal("dashboard.db", resource.Resource.ResourceName);
                Assert.True(resource.Resource.UninstrumentedPeer);
            });
    }

    [Fact]
    public async Task GetTraceSummaries_IncrementalAppend_UpdatesSummaryValues()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "frontend"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        ]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "backend"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: s_testTime.AddSeconds(1),
                                endTime: s_testTime.AddMinutes(2),
                                attributes: [KeyValuePair.Create("gen_ai.provider.name", "test")],
                                status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        ]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);

        Assert.True(summary.HasError);
        Assert.True(summary.HasGenAI);
        Assert.Collection(summary.Resources,
            resource =>
            {
                Assert.Equal("frontend", resource.Resource.ResourceName);
                Assert.Equal(1, resource.TotalSpans);
                Assert.Equal(0, resource.ErroredSpans);
            },
            resource =>
            {
                Assert.Equal("backend", resource.Resource.ResourceName);
                Assert.Equal(1, resource.TotalSpans);
                Assert.Equal(1, resource.ErroredSpans);
            });
    }

    [Fact]
    public async Task AddTraces_SelfParent_Reject()
    {
        // Arrange
        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));

        using var repositoryContext = await CreateRepositoryAsync(loggerFactory: factory);

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(1, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Empty(traces.PagedResult.Items);

        var write = Assert.Single(testSink.Writes);
        Assert.Equal("Error adding span.", write.Message);
        Assert.Equal("Circular loop detected for span '312d31' with parent '312d31'.", write.Exception!.Message);
    }

    [Fact]
    public async Task AddTraces_MultipleSpansLoop_Reject()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-3"),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-2")
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(1, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                Assert.Equal(2, trace.Spans.Count);
            });
    }

    [Fact]
    public async Task AddTraces_CircularReferenceAcrossIngestionCalls_Reject()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(4), parentSpanId: "1-2")
                        }
                    }
                }
            }
        });

        var context = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(context, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(5), parentSpanId: "1-3")
                        }
                    }
                }
            }
        });

        Assert.Equal(1, context.FailureCount);
        var trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(trace.Spans,
            span => AssertId("1-2", span.SpanId),
            span => AssertId("1-3", span.SpanId));
    }

    [Fact]
    public async Task AddTraces_DuplicateTraceIds_Reject()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(1, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                Assert.Equal(2, trace.Spans.Count);
            });
    }

    [Fact]
    public async Task AddTraces_Scope_Multiple()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("scope1"),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                        }
                    }
                }
            }
        });
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("scope2"),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                AssertId("1-1", trace.RootSpan!.SpanId);
                Assert.Equal(2, trace.Spans.Count);

                Assert.Collection(trace.Spans,
                    span => Assert.Equal("scope1", span.Scope.Name),
                    span => Assert.Equal("scope2", span.Scope.Name));
            });
    }

    [Fact]
    public async Task AddTraces_Traces_MultipleOutOrOrder()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext1 = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext1, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext1.FailureCount);

        var addContext2 = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext2, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext2.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces1 = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces1.PagedResult.Items,
            trace =>
            {
                AssertId("2", trace.TraceId);
                AssertId("2-1", trace.FirstSpan.SpanId);
                AssertId("2-1", trace.RootSpan!.SpanId);
            },
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-2", trace.FirstSpan.SpanId);
                Assert.Null(trace.RootSpan);
            });

        var addContext3 = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext3, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext3.FailureCount);

        var traces2 = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces2.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                Assert.Same(OtlpScope.Empty, trace.FirstSpan.Scope);
                AssertId("1-1", trace.RootSpan!.SpanId);
            },
            trace =>
            {
                AssertId("2", trace.TraceId);
                AssertId("2-1", trace.FirstSpan.SpanId);
                Assert.Same(OtlpScope.Empty, trace.FirstSpan.Scope);
                AssertId("2-1", trace.RootSpan!.SpanId);
            });
    }

    [Fact]
    public async Task AddTraces_Spans_MultipleOutOrOrder()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-5", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-4", startTime: s_testTime.AddMinutes(4), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                AssertId("1-1", trace.RootSpan!.SpanId);
                Assert.Collection(trace.Spans,
                    s => AssertId("1-1", s.SpanId),
                    s => AssertId("1-2", s.SpanId),
                    s => AssertId("1-3", s.SpanId),
                    s => AssertId("1-4", s.SpanId),
                    s => AssertId("1-5", s.SpanId));
            });
    }

    [Fact]
    public async Task AddTraces_SpanEvents_ReturnData()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), events: new List<Span.Types.Event>
                            {
                                new Span.Types.Event
                                {
                                    Name = "Event 2",
                                    TimeUnixNano = 2,
                                    Attributes =
                                    {
                                        new KeyValue { Key = "key2", Value = new AnyValue { StringValue = "Value!" } }
                                    }
                                },
                                new Span.Types.Event
                                {
                                    Name = "Event 1",
                                    TimeUnixNano = 1,
                                    Attributes =
                                    {
                                        new KeyValue { Key = "key1", Value = new AnyValue { StringValue = "Value!" } }
                                    }
                                }
                            })
                        }
                    }
                }
            }
        });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                Assert.Collection(trace.FirstSpan.Events,
                    e =>
                    {
                        Assert.Equal("Event 1", e.Name);
                        Assert.Collection(e.Attributes,
                            a =>
                            {
                                Assert.Equal("key1", a.Key);
                                Assert.Equal("Value!", a.Value);
                            });
                    },
                    e =>
                    {
                        Assert.Equal("Event 2", e.Name);
                    });
            });
    }

    [Fact]
    public async Task AddTraces_SpanLinks_ReturnData()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), links: new List<Span.Types.Link>
                            {
                                new Span.Types.Link
                                {
                                    TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("1")),
                                    SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("1-1")),
                                    Attributes =
                                    {
                                        new KeyValue { Key = "key2", Value = new AnyValue { StringValue = "Value!" } }
                                    }
                                },
                                new Span.Types.Link
                                {
                                    TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("2")),
                                    SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("2-1")),
                                    Attributes =
                                    {
                                        new KeyValue { Key = "key1", Value = new AnyValue { StringValue = "Value!" } }
                                    }
                                }
                            })
                        }
                    }
                }
            }
        });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                Assert.Collection(trace.FirstSpan.Links,
                    l =>
                    {
                        AssertId("1", l.TraceId);
                        AssertId("1-1", l.SpanId);
                        Assert.Collection(l.Attributes,
                            a =>
                            {
                                Assert.Equal("key2", a.Key);
                                Assert.Equal("Value!", a.Value);
                            });
                    },
                    l =>
                    {
                        AssertId("2", l.TraceId);
                        AssertId("2-1", l.SpanId);
                        Assert.Collection(l.Attributes,
                            a =>
                            {
                                Assert.Equal("key1", a.Key);
                                Assert.Equal("Value!", a.Value);
                            });
                    });
            });

    }

    [Fact]
    public async Task GetTraces_ReturnCopies()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext1 = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext1, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        var traces1 = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces1.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                AssertId("1-1", trace.RootSpan!.SpanId);
            });

        var traces2 = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.NotSame(traces1.PagedResult.Items[0], traces2.PagedResult.Items[0]);
        Assert.NotSame(traces1.PagedResult.Items[0].Spans[0].Trace, traces2.PagedResult.Items[0].Spans[0].Trace);

        var trace1 = repositoryContext.Repository.GetTrace(GetHexId("1"))!;
        var trace2 = repositoryContext.Repository.GetTrace(GetHexId("1"))!;
        Assert.NotSame(trace1, trace2);
        Assert.NotSame(trace1.Spans[0].Trace, trace2.Spans[0].Trace);
    }

    [Fact]
    public async Task AddTraces_AttributeAndEventLimits_LimitsApplied()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync(maxAttributeCount: 5, maxAttributeLength: 16, maxSpanEventCount: 5);

        var attributes = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < 10; i++)
        {
            var value = GetValue((i + 1) * 5);
            attributes.Add(new KeyValuePair<string, string>($"Key{i}", value));
        }

        var events = new List<Span.Types.Event>();
        for (var i = 0; i < 10; i++)
        {
            events.Add(CreateSpanEvent($"Event {i}", i, attributes));
        }

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: attributes, events: events)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        var trace = Assert.Single(traces.PagedResult.Items);

        AssertId("1", trace.TraceId);
        AssertId("1-1", trace.FirstSpan.SpanId);
        Assert.Collection(trace.FirstSpan.Attributes,
            p =>
            {
                Assert.Equal("Key0", p.Key);
                Assert.Equal("01234", p.Value);
            },
            p =>
            {
                Assert.Equal("Key1", p.Key);
                Assert.Equal("0123456789", p.Value);
            },
            p =>
            {
                Assert.Equal("Key2", p.Key);
                Assert.Equal("012345678901234", p.Value);
            },
            p =>
            {
                Assert.Equal("Key3", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            },
            p =>
            {
                Assert.Equal("Key4", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            });

        Assert.Equal(5, trace.FirstSpan.Events.Count);
        Assert.Equal(5, trace.FirstSpan.Events[0].Attributes.Length);
    }

    [Fact]
    public async Task AddTraces_Links_BacklinksPopulated()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        await AddTrace(repositoryContext.Repository, "1", s_testTime);
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        var trace = Assert.Single(traces.PagedResult.Items);

        Assert.Collection(trace.Spans,
            s =>
            {
                var link = Assert.Single(s.Links);
                AssertId("1-2", link.SpanId);
                AssertId("1-1", link.SourceSpanId);

                var backLink = Assert.Single(s.BackLinks);
                AssertId("1-1", backLink.SpanId);
                AssertId("1-2", backLink.SourceSpanId);
            },
            s =>
            {
                var link = Assert.Single(s.Links);
                AssertId("1-1", link.SpanId);
                AssertId("1-2", link.SourceSpanId);

                var backLink = Assert.Single(s.BackLinks);
                AssertId("1-2", backLink.SpanId);
                AssertId("1-1", backLink.SourceSpanId);
            });
    }

    [Fact]
    public async Task AddTraces_ExceedLimit_OrderedByTimestampAndTraceId()
    {
        // Arrange
        const int MaxTraceCount = 10;
        using var repositoryContext = await CreateRepositoryAsync(maxTraceCount: MaxTraceCount);

        var testTime = s_testTime.AddDays(1);
        var expectedTraces = new List<(string TraceId, DateTime StartTime)>();

        // Act
        for (var i = 0; i < MaxTraceCount * 3; i++)
        {
            var traceNumber = i + 1;
            var traceId = traceNumber.ToString(CultureInfo.InvariantCulture);

            // Insert traces out of order to stress the circular buffer type.
            var startTime = testTime.AddMinutes(i + (i % 2 == 0 ? -5 : 0));
            expectedTraces.Add((GetHexId(traceId), startTime));

            try
            {
                await AddTrace(repositoryContext.Repository, traceId, startTime);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error adding trace number {i}.", ex);
            }
        }

        // Assert
        var resources = repositoryContext.Repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        var actualOrder = traces.PagedResult.Items.Select(t => t.TraceId).ToList();
        var expectedOrder = expectedTraces
            .OrderBy(trace => trace.StartTime)
            .ThenBy(trace => trace.TraceId, StringComparer.Ordinal)
            .TakeLast(MaxTraceCount)
            .Select(trace => trace.TraceId)
            .ToList();
        Assert.Equal(expectedOrder, actualOrder);

        Assert.Equal(MaxTraceCount * 2, traces.PagedResult.Items.SelectMany(t => t.Spans).SelectMany(s => s.Links).Count());
    }

    private static async Task AddTrace(ITelemetryRepository repository, string traceId, DateTime startTime)
    {
        var addContext = new AddContext();

        var link1 = new Span.Types.Link
        {
            TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(traceId)),
            SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"{traceId}-2")),
            Attributes =
            {
                new KeyValue { Key = "key2", Value = new AnyValue { StringValue = "Value!" } }
            }
        };
        var link2 = new Span.Types.Link
        {
            TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes(traceId)),
            SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"{traceId}-1")),
            Attributes =
            {
                new KeyValue { Key = "key2", Value = new AnyValue { StringValue = "Value!" } }
            }
        };

        await repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: traceId, spanId: $"{traceId}-2", startTime: startTime.AddMinutes(5), endTime: startTime.AddMinutes(1), parentSpanId: $"{traceId}-1", links: new List<Span.Types.Link>
                            {
                                link2
                            }),
                            CreateSpan(traceId: traceId, spanId: $"{traceId}-1", startTime: startTime.AddMinutes(1), endTime: startTime.AddMinutes(10), links: new List<Span.Types.Link>
                            {
                                link1
                            })
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);
    }

    [Fact]
    public async Task AddTraces_MultipleRootSpans_RootSpanIsEarliestWithoutParent()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(4), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-2", trace.FirstSpan.SpanId); // First by time
                AssertId("1-3", trace.RootSpan!.SpanId); // First by time and without a parent
                Assert.Equal(3, trace.Spans.Count);
            });
    }

    [Fact]
    public async Task GetTraces_MultipleInstances()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("key-1", "value-1")]) }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("key-2", "value-2")]) }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource2"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10)) }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            },
            trace =>
            {
                AssertId("2", trace.TraceId);
            });

        var propertyKeys = await repositoryContext.Repository.GetTracePropertyKeysAsync(resourceKey, TestContext.Current.CancellationToken);
        Assert.Collection(propertyKeys,
            s => Assert.Equal("key-1", s),
            s => Assert.Equal("key-2", s));
    }

    [Fact]
    public async Task AddTraces_MissingAndEmptyInstanceIdsAreDistinct()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        var missingInstanceIdResource = CreateResource(name: "resource", instanceId: "placeholder");
        missingInstanceIdResource.Attributes.Remove(missingInstanceIdResource.Attributes.Single(attribute => attribute.Key == OtlpResource.SERVICE_INSTANCE_ID));
        var addContext = new AddContext();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = missingInstanceIdResource,
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)) }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource", instanceId: string.Empty),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)) }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);
        var resources = repositoryContext.Repository.GetResources();
        Assert.Equal(2, resources.Count);
        Assert.Contains(resources, resource => resource.ResourceKey == new ResourceKey("resource", InstanceId: null));
        Assert.Contains(resources, resource => resource.ResourceKey == new ResourceKey("resource", InstanceId: string.Empty));
    }

    [Fact]
    public async Task GetTraceFieldValues_AllFieldsMatchMaterializedTraces()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3), attributes: [KeyValuePair.Create("custom", "one")], kind: Span.Types.SpanKind.Client),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(5), parentSpanId: "1-1", attributes: [KeyValuePair.Create("custom", "two")], kind: Span.Types.SpanKind.Server)
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        var resource = Assert.Single(repositoryContext.Repository.GetResources());
        var traces = (await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resource.ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items;

        foreach (var field in KnownTraceFields.AllFields.Append("custom"))
        {
            var expected = OtlpSpan.GetFieldValuesFromTraces(traces, field);
            var actual = await repositoryContext.Repository.GetTraceFieldValuesAsync(field, TestContext.Current.CancellationToken);
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (value, count) in expected)
            {
                Assert.Equal(count, actual[value]);
            }
        }

        Assert.Empty(await repositoryContext.Repository.GetTraceFieldValuesAsync(KnownTraceFields.DurationField, TestContext.Current.CancellationToken));
        Assert.Empty(await repositoryContext.Repository.GetTraceFieldValuesAsync(KnownTraceFields.TimestampField, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetTraces_AttributeFilters()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("key1", "value1")]) }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create("key2", "value2")]) }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);

        // Act 1
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.Equals, Value = "value1" }
            ]
        }, cancellationToken: CancellationToken.None);
        // Assert 1
        // Match first span.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });

        // Act 2
        traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key2", Condition = FilterCondition.Equals, Value = "value2" }
            ]
        }, cancellationToken: CancellationToken.None);
        // Assert 2
        // Match second span.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });

        // Act 3
        traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.Equals, Value = "value1" },
                new FieldTelemetryFilter { Field = "key2", Condition = FilterCondition.Equals, Value = "value2" }
            ]
        }, cancellationToken: CancellationToken.None);
        // Assert 3
        // Match neither span.
        Assert.Empty(traces.PagedResult.Items);
    }

    [Theory]
    [InlineData(KnownTraceFields.TraceIdField, "31")]
    [InlineData(KnownTraceFields.SpanIdField, "312d31")]
    [InlineData(KnownTraceFields.StatusField, "Unset")]
    [InlineData(KnownTraceFields.KindField, "Client")]
    [InlineData(KnownResourceFields.ServiceNameField, "resource1")]
    [InlineData(KnownResourceFields.ServiceNameField, "TestPeer")]
    [InlineData(KnownSourceFields.NameField, "TestScope")]
    [InlineData(KnownTraceFields.DurationField, "540000")]
    public async Task GetTraces_KnownFilters(string name, string value)
    {
        // Arrange
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("key1", "value1"), KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client) }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);

        // Act 1
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = name, Condition = FilterCondition.NotEqual, Value = value }
            ]
        }, cancellationToken: CancellationToken.None);

        // Assert 1
        // Doesn't match filter.
        Assert.Empty(traces.PagedResult.Items);

        // Act 2
        traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = name, Condition = FilterCondition.Equals, Value = value }
            ]
        }, cancellationToken: CancellationToken.None);

        // Assert 2
        // Matches filter.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });
    }

    [Fact]
    public async Task GetTraces_FiltersPagingAndMaxDuration_ComputedFromAllMatchingTraces()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMilliseconds(1), endTime: s_testTime.AddMilliseconds(11), attributes: [KeyValuePair.Create("dynamic.filter", "match")]),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMilliseconds(2), endTime: s_testTime.AddMilliseconds(22), attributes: [KeyValuePair.Create("dynamic.filter", "match")]),
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMilliseconds(3), endTime: s_testTime.AddMilliseconds(33), attributes: [KeyValuePair.Create("dynamic.filter", "other")]),
                            CreateSpan(traceId: "4", spanId: "4-1", startTime: s_testTime.AddMilliseconds(4), endTime: s_testTime.AddMilliseconds(44), attributes: [KeyValuePair.Create("dynamic.filter", "match")]),
                            CreateSpan(traceId: "5", spanId: "5-1", startTime: s_testTime.AddMilliseconds(5), endTime: s_testTime.AddMilliseconds(55), attributes: [KeyValuePair.Create("dynamic.filter", "match")])
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        // This pins the behavior expected from an optimized single-pass implementation:
        // dynamic field filters, known duration filters, paging, total count, and max
        // duration must all be computed from the same filtered trace set. MaxDuration
        // intentionally comes from all matching traces, not just the returned page.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [new ResourceKey("resource1", InstanceId: null)],
            StartIndex = 1,
            Count = 1,
            Filters =
            [
                new FieldTelemetryFilter { Field = "dynamic.filter", Condition = FilterCondition.Equals, Value = "match" },
                new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.GreaterThanOrEqual, Value = "20" }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Equal(3, traces.PagedResult.TotalItemCount);
        Assert.Equal(TimeSpan.FromMilliseconds(50), traces.MaxDuration);
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("4", trace.TraceId);
                Assert.Equal(TimeSpan.FromMilliseconds(40), trace.Duration);
            });
    }

    [Fact]
    public async Task GetTraces_DurationFilter_AppliesTraceLevelDuration()
    {
        // Verifies that the duration filter uses the trace's overall duration (first span
        // start to latest span end), not individual span durations. A trace with a 100ms
        // root span containing a 5ms child span should match "> 50ms" (trace is 100ms)
        // but NOT "< 10ms" (even though the child span is only 5ms).
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Root span: 100ms duration
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMilliseconds(0), endTime: s_testTime.AddMilliseconds(100)),
                            // Child span: 5ms duration (well under any "short" threshold)
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMilliseconds(10), endTime: s_testTime.AddMilliseconds(15), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);

        // Duration filter "> 50ms" should match because trace duration is 100ms.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.GreaterThan, Value = "50" }]
        }, cancellationToken: CancellationToken.None);

        Assert.Single(traces.PagedResult.Items);

        // Duration filter "< 10ms" should NOT match because trace duration is 100ms,
        // even though the child span is only 5ms.
        traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = [new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.LessThan, Value = "10" }]
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(traces.PagedResult.Items);
    }

    [Fact]
    public async Task GetTraces_NotEqualFilter_NonMatchingValue_ReturnsTrace()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("key1", "value1")]) }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);

        // Act - filter for key1 != "other_value" should return the trace since key1 is "value1"
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [new ResourceKey("resource1", InstanceId: null)],
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.NotEqual, Value = "other_value" }
            ]
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });
    }

    [Fact]
    public async Task AddTraces_OutOfOrder_FullName()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();
        var request = new GetTracesRequest
        {
            ResourceKeys = [new ResourceKey("TestService", "TestId")],
            StartIndex = 0,
            Count = 10,
            Filters = []
        };

        // Act 1
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        // Assert 1
        var trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-3", trace.FullName);

        // Act 2
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        // Assert 2
        trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-2", trace.FullName);

        // Act 3
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        // Assert 3
        trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-1", trace.FullName);

        // Act 4
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-4", startTime: s_testTime, endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        // Assert 4
        trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(request, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-1", trace.FullName);
    }

    [Fact]
    public async Task AddTraces_SameResourceDifferentProperties_MultipleResourceViews()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("prop1", "value1")]),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("prop2", "value1"), KeyValuePair.Create("prop1", "value2")]),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("prop1", "value2"), KeyValuePair.Create("prop2", "value1")]),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        // Spans belong to the same resource
        var resource = Assert.Single(repositoryContext.Repository.GetResources());
        Assert.Equal("TestService", resource.ResourceName);
        Assert.Equal("TestId", resource.InstanceId);

        // Spans have different views
        var views = resource.GetViews().OrderBy(v => v.Properties.Length).ToList();
        Assert.Equal(3, views.Count);
        Assert.Empty(views[0].Properties);
        Assert.Collection(views.Where(v => v.Properties.Length > 0),
            v =>
            {
                Assert.Collection(v.Properties,
                    p =>
                    {
                        Assert.Equal("prop1", p.Key);
                        Assert.Equal("value1", p.Value);
                    });
            },
            v =>
            {
                Assert.Collection(v.Properties,
                    p =>
                    {
                        Assert.Equal("prop1", p.Key);
                        Assert.Equal("value2", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("prop2", p.Key);
                        Assert.Equal("value1", p.Value);
                    });
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resource.ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);
        var trace = Assert.Single(traces.PagedResult.Items);

        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("1-1", s.SpanId);
                Assert.Collection(s.Source.Properties,
                    p =>
                    {
                        Assert.Equal("prop1", p.Key);
                        Assert.Equal("value1", p.Value);
                    });
            },
            s =>
            {
                AssertId("1-2", s.SpanId);
                Assert.Collection(s.Source.Properties,
                    p =>
                    {
                        Assert.Equal("prop1", p.Key);
                        Assert.Equal("value2", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("prop2", p.Key);
                        Assert.Equal("value1", p.Value);
                    });
            },
            s =>
            {
                AssertId("1-3", s.SpanId);
                Assert.Collection(s.Source.Properties,
                    p =>
                    {
                        Assert.Equal("prop1", p.Key);
                        Assert.Equal("value2", p.Value);
                    },
                    p =>
                    {
                        Assert.Equal("prop2", p.Key);
                        Assert.Equal("value1", p.Value);
                    });
            });
    }

    [Fact]
    public async Task RemoveTraces_All()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "2", spanId: "2-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource2", "789"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "3", spanId: "3-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "3-1")
                        }
                    }
                }
            }
        });

        // Act
        await repositoryContext.Repository.AsWriter().ClearTracesAsync();

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        Assert.NotNull(traces?.PagedResult?.Items);
        Assert.Empty(traces.PagedResult.Items);
        Assert.Equal(0, traces.PagedResult.TotalItemCount);
    }

    [Fact]
    public async Task RemoveTraces_SelectedResource()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "2", spanId: "2-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource2", "789"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "3", spanId: "3-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "3-1")
                        }
                    }
                }
            }
        });

        // Act
        await repositoryContext.Repository.AsWriter().ClearTracesAsync(new ResourceKey("resource1", "123"));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        Assert.NotNull(traces?.PagedResult?.Items);
        Assert.Equal(2, traces.PagedResult.TotalItemCount);

        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("2", trace.TraceId);
                Assert.Collection(trace.Spans,
                    s =>
                    {
                        AssertId("2-1", s.SpanId);
                    },
                    s =>
                    {
                        AssertId("2-2", s.SpanId);
                    });
            },
            trace =>
            {
                AssertId("3", trace.TraceId);
                Assert.Collection(trace.Spans,
                    s =>
                    {
                        AssertId("3-1", s.SpanId);
                    },
                    s =>
                    {
                        AssertId("3-2", s.SpanId);
                    });
            });
    }

    [Fact]
    public async Task RemoveTraces_MultipleSelectedResources()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "2", spanId: "2-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource2", "789"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "3", spanId: "3-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "3-1"),
                        }
                    },
                }
            }
        });

        // Act
        await repositoryContext.Repository.AsWriter().ClearTracesAsync(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        Assert.NotNull(traces?.PagedResult?.Items);
        var trace = Assert.Single(traces.PagedResult.Items);

        AssertId("3", trace.TraceId);
        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("3-1", s.SpanId);
            },
            s =>
            {
                AssertId("3-2", s.SpanId);
            });
    }

    [Fact]
    public async Task RemoveTraces_SelectedResource_SpansFromDifferentTrace()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "123"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource1", "456"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource("resource2", "789"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "3", spanId: "3-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "3-1"),
                            // Spans on traces originating from other resources
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-2"),
                            CreateSpan(traceId: "2", spanId: "2-3", startTime: s_testTime.AddMinutes(6), endTime: s_testTime.AddMinutes(10), parentSpanId: "2-2")
                        }
                    },
                }
            }
        });

        // Act
        await repositoryContext.Repository.AsWriter().ClearTracesAsync(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        Assert.NotNull(traces?.PagedResult?.Items);
        var trace = Assert.Single(traces.PagedResult.Items);

        AssertId("3", trace.TraceId);
        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("3-1", s.SpanId);
            },
            s =>
            {
                AssertId("3-2", s.SpanId);
            });
    }

    [Fact]
    public async Task AddTraces_HaveUninstrumentedPeers()
    {
        // Arrange
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-2")], kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true);
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestPeer", resource.ResourceName);
                Assert.Null(resource.InstanceId);
                Assert.True(resource.UninstrumentedPeer);
            },
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var uninstrumentedPeerApp = resources.Single(a => a.UninstrumentedPeer);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [uninstrumentedPeerApp.ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        var trace = Assert.Single(traces.PagedResult.Items);
        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("1-1", s.SpanId);
                Assert.Null(s.UninstrumentedPeer);
            },
            s =>
            {
                AssertId("1-2", s.SpanId);
                Assert.NotNull(s.UninstrumentedPeer);
                Assert.Equal("TestPeer", s.UninstrumentedPeer.ResourceName);
            });

        var serviceNames = await repositoryContext.Repository.GetTraceFieldValuesAsync(KnownResourceFields.ServiceNameField, TestContext.Current.CancellationToken);
        Assert.Equal(2, serviceNames["TestService"]);
        Assert.Equal(1, serviceNames["TestPeer"]);
    }

    [Fact]
    public async Task AddTraces_ChildAddedLater_UpdatesUninstrumentedPeers()
    {
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [new TestOutgoingPeerResolver()]);
        var writer = repositoryContext.Repository.AsWriter();

        await writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(10),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")],
                                kind: Span.Types.SpanKind.Client,
                                status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        });

        var trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(GetHexId("1")));
        Assert.NotNull(Assert.Single(trace.Spans).UninstrumentedPeer);

        await writer.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: s_testTime.AddMinutes(5),
                                endTime: s_testTime.AddMinutes(10),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-2")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(GetHexId("1")));
        Assert.Collection(
            trace.Spans,
            span => Assert.Null(span.UninstrumentedPeer),
            span => Assert.Equal("TestPeer", span.UninstrumentedPeer?.ResourceName));

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource =>
            {
                Assert.Equal("TestService", resource.Resource.ResourceName);
                Assert.Equal(2, resource.TotalSpans);
                Assert.Equal(1, resource.ErroredSpans);
            },
            resource =>
            {
                Assert.Equal("TestPeer", resource.Resource.ResourceName);
                Assert.Equal(1, resource.TotalSpans);
                Assert.Equal(0, resource.ErroredSpans);
            });
    }

    [Fact]
    public async Task ClearTraces_UninstrumentedPeer_RemovesOrphanedPeerResource()
    {
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);
        Assert.Contains(repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true), resource => resource.UninstrumentedPeer);

        await repositoryContext.Repository.AsWriter().ClearTracesAsync();

        // The peer resource only ever existed because a span named it. Once the spans are gone nothing else
        // deletes it, so without cleanup it lingers in the resource list for the life of the database.
        Assert.DoesNotContain(repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true), resource => resource.UninstrumentedPeer);
    }

    [Fact]
    public async Task ClearTraces_UninstrumentedPeer_RemovesAssociatedTrace()
    {
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(10),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        var peerResource = Assert.Single(
            repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true),
            resource => resource.UninstrumentedPeer);

        await repositoryContext.Repository.AsWriter().ClearTracesAsync(peerResource.ResourceKey);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(traces.PagedResult.Items);
        Assert.DoesNotContain(repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true), resource => resource.UninstrumentedPeer);
    }

    [Fact]
    public async Task AddTraces_OnPeerUpdated_HaveUninstrumentedPeers()
    {
        // Arrange
        var matchPeer = false;
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: attributes =>
        {
            if (matchPeer)
            {
                var name = "TestPeer";
                var matchedResourced = ModelTestHelpers.CreateResource(resourceName: "TestPeer");

                return (name, matchedResourced);
            }
            else
            {
                return (null, null);
            }
        });
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        // Act
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-2")], kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resources = repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true);
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resources[0].ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        var trace = Assert.Single(traces.PagedResult.Items);
        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("1-1", s.SpanId);
                Assert.Null(s.UninstrumentedPeer);
            },
            s =>
            {
                AssertId("1-2", s.SpanId);
                Assert.Null(s.UninstrumentedPeer);
            });

        matchPeer = true;
        await outgoingPeerResolver.InvokePeerChanges();

        resources = repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true);
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestPeer", resource.ResourceName);
                Assert.Null(resource.InstanceId);
                Assert.True(resource.UninstrumentedPeer);
            },
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var uninstrumentedPeerApp = resources.Single(a => a.UninstrumentedPeer);

        traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [uninstrumentedPeerApp.ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        trace = Assert.Single(traces.PagedResult.Items);
        Assert.Collection(trace.Spans,
            s =>
            {
                AssertId("1-1", s.SpanId);
                Assert.Null(s.UninstrumentedPeer);
            },
            s =>
            {
                AssertId("1-2", s.SpanId);
                Assert.NotNull(s.UninstrumentedPeer);
                Assert.Equal("TestPeer", s.UninstrumentedPeer.ResourceName);
            });
    }

    [Fact]
    public async Task AddTraces_NameOnlyPeerResolver_PersistsUninstrumentedPeer()
    {
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: _ => ("Browser Link", null));
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "localhost")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);
        var peerResource = Assert.Single(repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true), resource => resource.UninstrumentedPeer);
        Assert.Equal("Browser Link", peerResource.ResourceName);
        Assert.Null(peerResource.InstanceId);

        var trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(GetHexId("1")));
        var span = Assert.Single(trace.Spans);
        Assert.Equal(peerResource.ResourceKey, span.UninstrumentedPeer?.ResourceKey);
    }

    [Fact]
    public async Task AddTraces_NameOnlyPeerResolverChange_PersistsUninstrumentedPeer()
    {
        var matchPeer = false;
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: _ => matchPeer ? ("Browser Link", null) : (null, null));
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);

        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "localhost")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        Assert.Equal(0, addContext.FailureCount);
        Assert.Null(Assert.Single(repositoryContext.Repository.GetTrace(GetHexId("1"))!.Spans).UninstrumentedPeer);

        matchPeer = true;
        await outgoingPeerResolver.InvokePeerChanges();

        var peerResource = Assert.Single(repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true), resource => resource.UninstrumentedPeer);
        Assert.Equal("Browser Link", peerResource.ResourceName);
        var span = Assert.Single(repositoryContext.Repository.GetTrace(GetHexId("1"))!.Spans);
        Assert.Equal(peerResource.ResourceKey, span.UninstrumentedPeer?.ResourceKey);

        matchPeer = false;
        await outgoingPeerResolver.InvokePeerChanges();

        Assert.Null(Assert.Single(repositoryContext.Repository.GetTrace(GetHexId("1"))!.Spans).UninstrumentedPeer);
        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(summary.Resources, resource => Assert.Equal("TestService", resource.Resource.ResourceName));
        Assert.Empty((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [peerResource.ResourceKey],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
    }

    [Fact]
    public async Task AddTraces_UninstrumentedPeer_InstanceIdDashes_AppKeyResolvedCorrectly()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-abc-def", displayName: "test");
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: attributes => (resource.Name, resource));
        using var repositoryContext = await CreateRepositoryAsync(
            outgoingPeerResolvers: [outgoingPeerResolver]);
        var addContext = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(addContext, new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "source", instanceId: "abc"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-1")], kind: Span.Types.SpanKind.Client),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "value-2")], kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        });

        var resources = repositoryContext.Repository.GetResources(includeUninstrumentedPeers: true);
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("source", resource.ResourceName);
                Assert.Equal("abc", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            },
            resource =>
            {
                Assert.Equal("test", resource.ResourceName);
                Assert.Equal("abc-def", resource.InstanceId);
                Assert.True(resource.UninstrumentedPeer);
            });
    }

    [Fact]
    public async Task GetSpans_ReturnsAllSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(8))
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(3, result.PagedResult.TotalItemCount);
        Assert.Equal(3, result.PagedResult.Items.Count);
    }

    [Fact]
    public async Task GetSpans_FilterByTraceId_ReturnsMatchingSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(8))
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TraceId = "31" // hex prefix of "1"
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(2, result.PagedResult.TotalItemCount);
        Assert.All(result.PagedResult.Items, s => AssertId("1", s.TraceId));
    }

    [Fact]
    public async Task GetSpans_FilterBySpanType_ReturnsMatchingSpans()
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddSeconds(1), attributes: [KeyValuePair.Create("db.system.name", "postgresql")]),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddSeconds(1), endTime: s_testTime.AddSeconds(2))
                        }
                    }
                }
            }
        });

        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [new SpanHasAttributeTelemetryFilter(["db.system.name", "db.system"])]
        }, CancellationToken.None);

        var span = Assert.Single(result.PagedResult.Items);
        AssertId("1-1", span.SpanId);
    }

    [Fact]
    public async Task GetSpans_FilterByHasError_ReturnsErrorSpansOnly()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = true
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public async Task GetSpans_FilterByHasErrorFalse_ReturnsNonErrorSpansOnly()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = false
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-2", result.PagedResult.Items[0].SpanId);
    }

    [Theory]
    [InlineData(FilterCondition.Equals, "1", "3")]
    [InlineData(FilterCondition.NotEqual, "2")]
    public async Task GetTraces_StatusFilter_ReturnsMatchingTraces(FilterCondition condition, params string[] expectedTraceIds)
    {
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(2), status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Ok }),
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(4), status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(traceId: "3", spanId: "3-2", startTime: s_testTime.AddMinutes(4), endTime: s_testTime.AddMinutes(5), status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

        var result = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.StatusField,
                    Value = nameof(OtlpSpanStatusCode.Error),
                    Condition = condition
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Equal(expectedTraceIds.Length, result.PagedResult.TotalItemCount);
        Assert.Equal(expectedTraceIds.Length, result.PagedResult.Items.Count);
        for (var index = 0; index < expectedTraceIds.Length; index++)
        {
            AssertId(expectedTraceIds[index], result.PagedResult.Items[index].TraceId);
        }
    }

    [Fact]
    public async Task GetSpans_FilterByResource_ReturnsMatchingSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service-a", instanceId: "a1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "service-b", instanceId: "b1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(8))
                        }
                    }
                }
            }
        });

        var resources = repositoryContext.Repository.GetResources();
        var serviceA = resources.Single(r => r.ResourceName == "service-a");

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [serviceA.ResourceKey],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Theory]
    [InlineData(FilterCondition.NotEqual, "TestService", 0)]
    [InlineData(FilterCondition.NotEqual, "TestPeer", 0)]
    [InlineData(FilterCondition.NotEqual, "other", 1)]
    [InlineData(FilterCondition.NotContains, "Service", 0)]
    [InlineData(FilterCondition.NotContains, "Peer", 0)]
    [InlineData(FilterCondition.NotContains, "other", 1)]
    public async Task GetSpans_NegativeServiceNameFilter_AppliesToSourceAndPeer(FilterCondition condition, string value, int expectedCount)
    {
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        using var repositoryContext = await CreateRepositoryAsync(outgoingPeerResolvers: [outgoingPeerResolver]);

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1),
                                attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "peer")],
                                kind: Span.Types.SpanKind.Client)
                        }
                    }
                }
            }
        ]);

        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownResourceFields.ServiceNameField,
                    Condition = condition,
                    Value = value
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Equal(expectedCount, result.PagedResult.TotalItemCount);
        Assert.Equal(expectedCount, result.PagedResult.Items.Count);
    }

    [Fact]
    public async Task GetSpans_FilterByDuration_ReturnsMatchingSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // 9 minutes = 540000ms
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            // 1 minute = 60000ms
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(6), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Act - filter for spans with duration >= 100000ms (100s)
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.DurationField,
                    Condition = FilterCondition.GreaterThanOrEqual,
                    Value = "100000"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        // Assert - only the long span matches
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public async Task GetSpans_FilterByTextFragments_ReturnsMatchingSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), attributes: [KeyValuePair.Create("http.url", "https://example.com/api")]),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", attributes: [KeyValuePair.Create("db.system", "postgresql")])
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TextFragments = ["example.com"]
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public async Task GetSpans_FilterByTextFragments_MatchesDisplaySummaries()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime,
                                endTime: s_testTime.AddMinutes(1),
                                attributes:
                                [
                                    KeyValuePair.Create("http.request.method", "GET"),
                                    KeyValuePair.Create("http.response.status_code", "200")
                                ],
                                kind: Span.Types.SpanKind.Client),
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(2),
                                attributes: [KeyValuePair.Create("db.system", "postgresql")],
                                kind: Span.Types.SpanKind.Client),
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-3",
                                startTime: s_testTime.AddMinutes(2),
                                endTime: s_testTime.AddMinutes(3),
                                attributes:
                                [
                                    KeyValuePair.Create("rpc.system", "grpc"),
                                    KeyValuePair.Create("rpc.service", "Greeter"),
                                    KeyValuePair.Create("rpc.method", "SayHello"),
                                    KeyValuePair.Create("rpc.grpc.status_code", "0")
                                ],
                                kind: Span.Types.SpanKind.Client),
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-4",
                                startTime: s_testTime.AddMinutes(3),
                                endTime: s_testTime.AddMinutes(4),
                                attributes:
                                [
                                    KeyValuePair.Create("messaging.system", "kafka"),
                                    KeyValuePair.Create("messaging.operation", "publish"),
                                    KeyValuePair.Create("messaging.destination.name", "orders")
                                ],
                                kind: Span.Types.SpanKind.Producer)
                        }
                    }
                }
            }
        ]);

        var expectedMatches = new[]
        {
            (Text: "HTTP GET 200", SpanId: "1-1"),
            (Text: "DATA postgresql Test span. Id: 1-2", SpanId: "1-2"),
            (Text: "RPC Greeter/SayHello OK", SpanId: "1-3"),
            (Text: "MSG kafka publish orders", SpanId: "1-4")
        };
        foreach (var expectedMatch in expectedMatches)
        {
            var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
            {
                ResourceKeys = [],
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = [],
                TextFragments = [expectedMatch.Text]
            }, cancellationToken: CancellationToken.None);

            Assert.Equal(1, result.PagedResult.TotalItemCount);
            AssertId(expectedMatch.SpanId, Assert.Single(result.PagedResult.Items).SpanId);
        }
    }

    [Theory]
    [InlineData(KnownTraceFields.KindField, "Client")]
    [InlineData(KnownTraceFields.StatusField, "Error")]
    public async Task GetSpans_FilterByKindOrStatusText_ReturnsMatchingSpans(string field, string value)
    {
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: s_testTime.AddMinutes(1),
                                endTime: s_testTime.AddMinutes(2),
                                kind: Span.Types.SpanKind.Client,
                                status: new Status { Code = Status.Types.StatusCode.Error }),
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: s_testTime.AddMinutes(2),
                                endTime: s_testTime.AddMinutes(3),
                                kind: Span.Types.SpanKind.Server,
                                status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        ]);

        var fieldResult = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = field,
                    Condition = FilterCondition.Equals,
                    Value = value
                }
            ]
        }, cancellationToken: CancellationToken.None);
        var textResult = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TextFragments = [value]
        }, cancellationToken: CancellationToken.None);

        Assert.Equal(1, fieldResult.PagedResult.TotalItemCount);
        AssertId("1-1", Assert.Single(fieldResult.PagedResult.Items).SpanId);
        Assert.Equal(1, textResult.PagedResult.TotalItemCount);
        AssertId("1-1", Assert.Single(textResult.PagedResult.Items).SpanId);
    }

    [Fact]
    public async Task GetSpans_Pagination_ReturnsCorrectPage()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1"),
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(3), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Act - get second page (skip 1, take 1)
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 1,
            Count = 1,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(3, result.PagedResult.TotalItemCount);
        Assert.Single(result.PagedResult.Items);
        AssertId("1-2", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public async Task GetSpans_CombinedFilters_ReturnsMatchingSpans()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10), status: new Status { Code = Status.Types.StatusCode.Error }, attributes: [KeyValuePair.Create("http.url", "https://example.com")]),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(10), parentSpanId: "1-1", status: new Status { Code = Status.Types.StatusCode.Ok }, attributes: [KeyValuePair.Create("http.url", "https://example.com")]),
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(8), status: new Status { Code = Status.Types.StatusCode.Error }, attributes: [KeyValuePair.Create("db.system", "redis")])
                        }
                    }
                }
            }
        });

        // Act - filter for error spans with "example.com" text
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = true,
            TextFragments = ["example.com"]
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public async Task GetSpans_EmptyRepository_ReturnsEmpty()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(0, result.PagedResult.TotalItemCount);
        Assert.Empty(result.PagedResult.Items);
    }

    [Fact]
    public async Task GetSpans_UnknownResource_ReturnsEmpty()
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [ResourceKey.Create("nonexistent", "unknown")],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(0, result.PagedResult.TotalItemCount);
        Assert.Empty(result.PagedResult.Items);
    }

    [Theory]
    [InlineData("747261636531", 1)] // full hex trace ID — prefix match
    [InlineData("7472616", 1)] // 7 chars — meets ShortenedIdLength, prefix match
    [InlineData("747261", 0)] // 6 chars — below ShortenedIdLength, requires exact match
    public async Task GetSpans_TraceIdPrefixLength_MatchesShortenedIds(string traceIdFilter, int expectedCount)
    {
        // Arrange
        using var repositoryContext = await CreateRepositoryAsync();

        // Use a trace ID whose hex representation is "747261636531" (UTF-8 bytes of "trace1")
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: traceId, spanId: "1-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10)),
                            CreateSpan(traceId: "other", spanId: "2-1", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(8))
                        }
                    }
                }
            }
        });

        // Act
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TraceId = traceIdFilter
        }, cancellationToken: CancellationToken.None);

        // Assert
        Assert.Equal(expectedCount, result.PagedResult.TotalItemCount);
    }

    [Fact]
    public async Task GetSpans_DisabledFiltersAreIgnored()
    {
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1)),
                            CreateSpan(traceId: "trace1", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3))
                        }
                    }
                }
            }
        });

        // Enabled filter matches span name containing "span1", disabled filter would exclude everything
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.NameField,
                    Value = "span1",
                    Condition = FilterCondition.Contains,
                    Enabled = true
                },
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.NameField,
                    Value = "IMPOSSIBLE",
                    Condition = FilterCondition.Contains,
                    Enabled = false
                }
            ]
        }, cancellationToken: CancellationToken.None);

        // The disabled filter should be ignored — only the enabled "span1" filter applies
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        Assert.Contains("span1", result.PagedResult.Items[0].Name);
    }

    [Fact]
    public async Task GetTraces_NotContainsFilter_ExcludesTraceWhenAnySpanMatches()
    {
        // Verifies that a "not contains" filter on the trace Name field excludes the trace
        // when ANY span's name contains the filtered text, even if other spans in the same
        // trace do not contain it. This is the fix for https://github.com/microsoft/aspire/issues/18684.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Root span whose name contains the filter text.
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5)),
                            // Child span whose name does NOT contain "1-1".
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Filter: Name not contains "1-1" — the root span's name is "Test span. Id: 1-1" which
        // contains "1-1", so the trace should be excluded even though the child span doesn't match.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.NameField,
                    Condition = FilterCondition.NotContains,
                    Value = "1-1"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(traces.PagedResult.Items);
    }

    [Fact]
    public async Task GetTraces_NotContainsFilter_IncludesTraceWhenNoSpanMatches()
    {
        // Verifies that a "not contains" filter includes the trace when none of its spans'
        // names contain the filtered text.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Filter: Name not contains "NONEXISTENT" — no span name contains this text,
        // so the trace should be included.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.NameField,
                    Condition = FilterCondition.NotContains,
                    Value = "NONEXISTENT"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Collection(traces.PagedResult.Items,
            trace => AssertId("1", trace.TraceId));
    }

    [Fact]
    public async Task GetTraces_NotEqualFilter_ExcludesTraceWhenAnySpanMatches()
    {
        // Verifies that a "not equal" filter excludes the trace when ANY span's field value
        // equals the filtered text.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5)),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3), parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Filter: Name != "Test span. Id: 1-1" — the root span matches exactly, so the trace
        // should be excluded even though the child span doesn't match.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.NameField,
                    Condition = FilterCondition.NotEqual,
                    Value = "Test span. Id: 1-1"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(traces.PagedResult.Items);
    }

    [Fact]
    public async Task GetTraces_NotContainsWithPositiveFilter_CombinesCorrectly()
    {
        // Verifies that combining a positive filter with a negative filter works correctly:
        // the trace must have at least one span matching the positive filter AND all spans
        // must satisfy the negative filter.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5),
                                attributes: [KeyValuePair.Create("env", "prod")]),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3),
                                parentSpanId: "1-1", attributes: [KeyValuePair.Create("env", "prod")])
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(15),
                                attributes: [KeyValuePair.Create("env", "staging")])
                        }
                    }
                }
            },
            // Trace 3: satisfies both conditions — env=prod and no span name contains "1-1".
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "3", spanId: "3-1", startTime: s_testTime.AddMinutes(20), endTime: s_testTime.AddMinutes(25),
                                attributes: [KeyValuePair.Create("env", "prod")])
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Positive: attribute env contains "prod"
        // Negative: name not contains "1-1" (excludes trace 1 because root span name matches)
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter { Field = "env", Condition = FilterCondition.Contains, Value = "prod" },
                new FieldTelemetryFilter { Field = KnownTraceFields.NameField, Condition = FilterCondition.NotContains, Value = "1-1" }
            ]
        }, cancellationToken: CancellationToken.None);

        // Trace 1 is excluded (root span name contains "1-1" even though env=prod matches).
        // Trace 2 doesn't match the positive filter (env=staging, not prod).
        // Trace 3 satisfies both: env=prod AND no span name contains "1-1".
        Assert.Collection(traces.PagedResult.Items,
            trace => AssertId("3", trace.TraceId));
    }

    [Fact]
    public async Task GetTraces_NotContainsFilter_AbsentAttributeDoesNotExcludeTrace()
    {
        // Verifies that a negative filter on an attribute field does NOT exclude a trace
        // just because some spans lack the attribute. A span without the field trivially
        // satisfies "not contains X" — it cannot contain the value.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Span with http.method = POST (satisfies "not contains GET").
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5),
                                attributes: [KeyValuePair.Create("http.method", "POST")]),
                            // Span without http.method attribute at all — should NOT cause exclusion.
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3),
                                parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Filter: http.method not contains "GET" — span 1-1 has POST (passes), span 1-2
        // has no http.method (trivially passes). The trace should be included.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = "http.method",
                    Condition = FilterCondition.NotContains,
                    Value = "GET"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Collection(traces.PagedResult.Items,
            trace => AssertId("1", trace.TraceId));
    }

    [Fact]
    public async Task GetTraces_NotContainsFilter_AbsentAttributeWithViolatingSpanExcludes()
    {
        // Verifies that a trace is excluded when one span has the attribute and violates
        // the negative condition, even though another span lacks the attribute entirely.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Span with http.method = GET (violates "not contains GET").
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5),
                                attributes: [KeyValuePair.Create("http.method", "GET")]),
                            // Span without http.method — trivially passes, but trace still excluded.
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3),
                                parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = "http.method",
                    Condition = FilterCondition.NotContains,
                    Value = "GET"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Empty(traces.PagedResult.Items);
    }

    [Fact]
    public async Task GetSpans_NotContainsFilter_AbsentAttributeIncludesSpan()
    {
        // Verifies that span-level negative filtering correctly includes spans that lack the
        // filtered attribute. A span without the field trivially satisfies "not contains X".
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5),
                                attributes: [KeyValuePair.Create("http.method", "GET")]),
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3),
                                parentSpanId: "1-1", attributes: [KeyValuePair.Create("http.method", "POST")]),
                            // Span without http.method attribute — should still be included.
                            CreateSpan(traceId: "1", spanId: "1-3", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(4),
                                parentSpanId: "1-1")
                        }
                    }
                }
            }
        });

        // Filter: http.method not contains "GET"
        // Span 1-1 has GET → excluded
        // Span 1-2 has POST → included (POST doesn't contain GET)
        // Span 1-3 has no http.method → included (absent field trivially satisfies "not contains")
        var result = await repositoryContext.Repository.GetSpansAsync(new GetSpansRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = int.MaxValue,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = "http.method",
                    Condition = FilterCondition.NotContains,
                    Value = "GET"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Equal(2, result.PagedResult.TotalItemCount);
        Assert.Contains(result.PagedResult.Items, s => GetStringId(s.SpanId) == "1-2");
        Assert.Contains(result.PagedResult.Items, s => GetStringId(s.SpanId) == "1-3");
    }

    [Fact]
    public async Task GetTraces_NotEqualTimestampFilter_ExcludesTraceViaUnoptimizedPath()
    {
        // Verifies that the non-optimized MatchesFilters path (used for date/numeric fields)
        // correctly applies ALL-span semantics for negative filters. A timestamp NotEqual
        // filter excludes a trace when any span's timestamp matches the filter value.
        using var repositoryContext = await CreateRepositoryAsync();

        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Span 1: starts at s_testTime (violates "timestamp != s_testTime")
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: s_testTime, endTime: s_testTime.AddMinutes(5)),
                            // Span 2: starts later (satisfies "timestamp != s_testTime")
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(3),
                                parentSpanId: "1-1")
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            // Trace 2: starts at a different time (satisfies the filter)
                            CreateSpan(traceId: "2", spanId: "2-1", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(15))
                        }
                    }
                }
            }
        });

        var resourceKey = new ResourceKey("service1", InstanceId: null);

        // Timestamp is a date field, so StringFilter.TryCreate returns false and
        // CreateOptimizedTraceFilters falls through to the non-optimized path.
        // Filter: Timestamp != "1970-01-01T00:00:00Z" — span 1-1 violates this (its timestamp
        // equals the filter value), so trace 1 is excluded. Trace 2 passes.
        var traces = await repositoryContext.Repository.GetTracesAsync(new GetTracesRequest
        {
            ResourceKeys = [resourceKey],
            StartIndex = 0,
            Count = 10,
            Filters =
            [
                new FieldTelemetryFilter
                {
                    Field = KnownTraceFields.TimestampField,
                    Condition = FilterCondition.NotEqual,
                    Value = "1970-01-01T00:00:00Z"
                }
            ]
        }, cancellationToken: CancellationToken.None);

        Assert.Collection(traces.PagedResult.Items,
            trace => AssertId("2", trace.TraceId));
    }
}

public sealed class SqliteTraceTests : TraceTests
{
    [Fact]
    public async Task GetTraceSummaries_MultipleRootResourcesInPayload_OrdersByReceipt()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            CreateRootResourceSpans("z-first", "1-1", testTime.AddMinutes(2)),
            CreateRootResourceSpans("a-second", "1-2", testTime)
        ]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource => Assert.Equal("z-first", resource.Resource.ResourceName),
            resource => Assert.Equal("a-second", resource.Resource.ResourceName));
    }

    [Fact]
    public async Task GetTraceSummaries_MultipleRootResourcesAcrossCalls_OrdersByReceipt()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateRootResourceSpans("z-first", "1-1", testTime.AddMinutes(2))]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateRootResourceSpans("a-second", "1-2", testTime)]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource => Assert.Equal("z-first", resource.Resource.ResourceName),
            resource => Assert.Equal("a-second", resource.Resource.ResourceName));
    }

    [Fact]
    public async Task GetTraceSummaries_PeerRecalculationPreservesRootReceiptOrder()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: _ => (null, null));
        using var repositoryContext = await CreateRepositoryAsync(
            timeProvider: new FixedTimeProvider(DateTimeOffset.MaxValue.AddTicks(-100)),
            outgoingPeerResolvers: [outgoingPeerResolver]);
        var repository = Assert.IsType<SqliteTelemetryRepository>(repositoryContext.Repository);
        await repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            CreateRootResourceSpans("first", "1-1", testTime),
            CreateRootResourceSpans("z-second", "1-2", testTime)
        ]);

        await outgoingPeerResolver.InvokePeerChanges();
        await repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateRootResourceSpans("a-third", "1-3", testTime)]);

        var summary = Assert.Single((await repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource => Assert.Equal("first", resource.Resource.ResourceName),
            resource => Assert.Equal("z-second", resource.Resource.ResourceName),
            resource => Assert.Equal("a-third", resource.Resource.ResourceName));
    }

    [Fact]
    public async Task GetTraceSummaries_NonRootResourceEarlierSpanAddedLater_UpdatesOrder()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            CreateResourceSpans("root", "1-1", testTime, parentSpanId: null),
            CreateResourceSpans("z-updated", "1-2", testTime.AddMinutes(5), parentSpanId: "1-1"),
            CreateResourceSpans("middle", "1-3", testTime.AddMinutes(4), parentSpanId: "1-1")
        ]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateResourceSpans("z-updated", "1-4", testTime.AddMinutes(3), parentSpanId: "1-1")]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource => Assert.Equal("root", resource.Resource.ResourceName),
            resource =>
            {
                Assert.Equal("z-updated", resource.Resource.ResourceName);
                Assert.Equal(2, resource.TotalSpans);
            },
            resource => Assert.Equal("middle", resource.Resource.ResourceName));
    }

    [Fact]
    public async Task GetTraceSummaries_ResourceSeenAsRootLater_PromotesOrderByReceipt()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            CreateResourceSpans("first-root", "1-1", testTime, parentSpanId: null),
            CreateResourceSpans("promoted", "1-2", testTime.AddMinutes(2), parentSpanId: "1-1"),
            CreateResourceSpans("non-root", "1-3", testTime.AddMinutes(1), parentSpanId: "1-1")
        ]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateResourceSpans("promoted", "1-4", testTime.AddMinutes(4), parentSpanId: null)]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateResourceSpans("later-root", "1-5", testTime.AddMinutes(3), parentSpanId: null)]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(
            new AddContext(),
            [CreateResourceSpans("promoted", "1-6", testTime.AddMinutes(5), parentSpanId: null)]);

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(
            summary.Resources,
            resource => Assert.Equal("first-root", resource.Resource.ResourceName),
            resource =>
            {
                Assert.Equal("promoted", resource.Resource.ResourceName);
                Assert.Equal(3, resource.TotalSpans);
            },
            resource => Assert.Equal("later-root", resource.Resource.ResourceName),
            resource => Assert.Equal("non-root", resource.Resource.ResourceName));
    }

    private static ResourceSpans CreateRootResourceSpans(string resourceName, string spanId, DateTime startTime)
        => CreateResourceSpans(resourceName, spanId, startTime, parentSpanId: null);

    private static ResourceSpans CreateResourceSpans(string resourceName, string spanId, DateTime startTime, string? parentSpanId)
    {
        return new ResourceSpans
        {
            Resource = CreateResource(name: resourceName),
            ScopeSpans =
            {
                new ScopeSpans
                {
                    Scope = CreateScope(),
                    Spans =
                    {
                        CreateSpan(
                            traceId: "1",
                            spanId: spanId,
                            startTime: startTime,
                            endTime: startTime.AddMinutes(1),
                            parentSpanId: parentSpanId)
                    }
                }
            }
        };
    }

    [Fact]
    public async Task AddTraces_CircularReferenceAcrossPersistedAndIncomingSpans_Reject()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                parentSpanId: "1-2",
                                startTime: testTime.AddMinutes(1),
                                endTime: testTime.AddMinutes(2))
                        }
                    }
                }
            }
        ]);

        var context = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(context,
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-3",
                                startTime: testTime.AddMinutes(2),
                                endTime: testTime.AddMinutes(3)),
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-3",
                                parentSpanId: "1-1",
                                startTime: testTime.AddMinutes(3),
                                endTime: testTime.AddMinutes(4))
                        }
                    }
                }
            }
        ]);

        Assert.Equal(1, context.SuccessCount);
        Assert.Equal(1, context.FailureCount);
        var trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(GetHexId("1")));
        Assert.Collection(trace.Spans,
            span => AssertId("1-1", span.SpanId),
            span => AssertId("1-2", span.SpanId));
    }

    [Fact]
    public async Task GetTraceSummaries_EqualStartTime_MatchesMaterializedTrace()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "first"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-1", startTime: testTime, endTime: testTime.AddMinutes(1))
                        }
                    }
                }
            }
        ]);
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "second"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "1-2", startTime: testTime, endTime: testTime.AddMinutes(1))
                        }
                    }
                }
            }
        ]);

        var trace = Assert.IsType<OtlpTrace>(repositoryContext.Repository.GetTrace(GetHexId("1")));
        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);

        Assert.Equal(trace.FullName, summary.FullName);
        Assert.Equal(trace.RootOrFirstSpan.Source.Resource.ResourceKey, summary.RootResource.ResourceKey);
    }

    [Fact]
    public async Task AddTraces_LargeAppend_DoesNotExceedSqliteParameterLimit()
    {
        const int appendedSpanCount = 8_192;
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "1", spanId: "root", startTime: testTime, endTime: testTime.AddMinutes(1))
                        }
                    }
                }
            }
        ]);
        var resourceSpans = new ResourceSpans
        {
            Resource = CreateResource(),
            ScopeSpans =
            {
                new ScopeSpans
                {
                    Scope = CreateScope()
                }
            }
        };
        for (var index = 0; index < appendedSpanCount; index++)
        {
            resourceSpans.ScopeSpans[0].Spans.Add(CreateSpan(
                traceId: "1",
                spanId: $"child-{index}",
                parentSpanId: $"missing-parent-{index}",
                startTime: testTime.AddSeconds(1),
                endTime: testTime.AddMinutes(1)));
        }

        var context = new AddContext();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(context, [resourceSpans]);

        Assert.Equal(appendedSpanCount, context.SuccessCount);
        Assert.Equal(0, context.FailureCount);
        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        Assert.Collection(summary.Resources, resource => Assert.Equal(appendedSpanCount + 1, resource.TotalSpans));
    }

    [Fact]
    public async Task GetTraceSummaries_AfterResourceDeletion_DeletesAffectedTraces()
    {
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync();
        await repositoryContext.Repository.AsWriter().AddTracesAsync(new AddContext(),
        [
            new ResourceSpans
            {
                Resource = CreateResource(name: "frontend"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-1",
                                startTime: testTime,
                                endTime: testTime.AddMinutes(5),
                                attributes: [KeyValuePair.Create("gen_ai.system", "test")],
                                status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "backend"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(
                                traceId: "1",
                                spanId: "1-2",
                                parentSpanId: "1-1",
                                startTime: testTime.AddMinutes(1),
                                endTime: testTime.AddMinutes(2)),
                            CreateSpan(
                                traceId: "2",
                                spanId: "2-1",
                                startTime: testTime.AddMinutes(3),
                                endTime: testTime.AddMinutes(4))
                        }
                    }
                }
            }
        ]);

        await repositoryContext.Repository.AsWriter().ClearSelectedSignalsAsync(new Dictionary<string, HashSet<AspireDataType>>
        {
            [new ResourceKey("frontend", "TestId").GetCompositeName()] = [AspireDataType.Resource]
        });

        Assert.Null(repositoryContext.Repository.GetTrace(GetHexId("1")));
        Assert.NotNull(repositoryContext.Repository.GetTrace(GetHexId("2")));

        var summary = Assert.Single((await repositoryContext.Repository.GetTraceSummariesAsync(new GetTracesRequest
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        }, cancellationToken: CancellationToken.None)).PagedResult.Items);
        AssertId("2", summary.TraceId);
        Assert.Equal("backend: Test span. Id: 2-1", summary.FullName);
        Assert.Equal(testTime.AddMinutes(3), summary.StartTime);
        Assert.Equal(TimeSpan.FromMinutes(1), summary.Duration);
        Assert.False(summary.HasError);
        Assert.False(summary.HasGenAI);
        Assert.Collection(summary.Resources, resource =>
        {
            Assert.Equal("backend", resource.Resource.ResourceName);
            Assert.Equal(1, resource.TotalSpans);
            Assert.Equal(0, resource.ErroredSpans);
        });
    }

    [Fact]
    public async Task AddTraces_LargeSpanAndAttributeBatchesRoundTrip()
    {
        const int spanCount = 101;
        using var repositoryContext = await CreateRepositoryAsync();
        var repository = Assert.IsType<SqliteTelemetryRepository>(repositoryContext.Repository);
        var resourceSpans = new ResourceSpans
        {
            Resource = CreateResource(),
            ScopeSpans =
            {
                new ScopeSpans { Scope = CreateScope() }
            }
        };
        var testTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < spanCount; index++)
        {
            resourceSpans.ScopeSpans[0].Spans.Add(CreateSpan(
                traceId: "trace",
                spanId: $"span-{index}",
                startTime: testTime.AddTicks(index),
                endTime: testTime.AddTicks(index + 1),
                attributes: [KeyValuePair.Create("index", index.ToString(CultureInfo.InvariantCulture))]));
        }

        var context = new AddContext();
        await repository.AsWriter().AddTracesAsync(context, [resourceSpans]);

        Assert.Equal(spanCount, context.SuccessCount);
        Assert.Equal(0, context.FailureCount);

        var trace = Assert.IsType<OtlpTrace>(repository.GetTrace(GetHexId("trace")));
        Assert.Equal(spanCount, trace.Spans.Count);
        for (var index = 0; index < spanCount; index++)
        {
            var span = trace.Spans[index];
            AssertId($"span-{index}", span.SpanId);
            Assert.Equal(KeyValuePair.Create("index", index.ToString(CultureInfo.InvariantCulture)), Assert.Single(span.Attributes));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

}
