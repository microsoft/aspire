// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
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

public class TraceTests
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
        var result = TelemetryRepository.ConvertSpanKind(value);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AddTraces()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void AddTraces_SelfParent_Reject()
    {
        // Arrange
        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b => b.AddProvider(new TestLoggerProvider(testSink)));

        var repository = CreateRepository(loggerFactory: factory);

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Empty(traces.PagedResult.Items);

        var write = Assert.Single(testSink.Writes);
        Assert.Equal("Error adding span.", write.Message);
        Assert.Equal("Circular loop detected for span '312d31' with parent '312d31'.", write.Exception!.Message);
    }

    [Fact]
    public void AddTraces_MultipleSpansLoop_Reject()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                Assert.Equal(2, trace.Spans.Count);
            });
    }

    [Fact]
    public void AddTraces_DuplicateTraceIds_Reject()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                Assert.Equal(2, trace.Spans.Count);
            });
    }

    [Fact]
    public void AddTraces_Scope_Multiple()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void AddTraces_Traces_MultipleOutOrOrder()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext1 = new AddContext();
        repository.AddTraces(addContext1, new RepeatedField<ResourceSpans>()
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
        repository.AddTraces(addContext2, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces1 = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
        repository.AddTraces(addContext3, new RepeatedField<ResourceSpans>()
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

        var traces2 = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void AddTraces_Spans_MultipleOutOrOrder()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void AddTraces_SpanEvents_ReturnData()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void AddTraces_SpanLinks_ReturnData()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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

        Assert.Collection(repository.SpanLinks,
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
    }

    [Fact]
    public void GetTraces_ReturnCopies()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext1 = new AddContext();
        repository.AddTraces(addContext1, new RepeatedField<ResourceSpans>()
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

        var traces1 = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(traces1.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
                AssertId("1-1", trace.FirstSpan.SpanId);
                AssertId("1-1", trace.RootSpan!.SpanId);
            });

        var traces2 = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.NotSame(traces1.PagedResult.Items[0], traces2.PagedResult.Items[0]);
        Assert.NotSame(traces1.PagedResult.Items[0].Spans[0].Trace, traces2.PagedResult.Items[0].Spans[0].Trace);

        var trace1 = repository.GetTrace(GetHexId("1"))!;
        var trace2 = repository.GetTrace(GetHexId("1"))!;
        Assert.NotSame(trace1, trace2);
        Assert.NotSame(trace1.Spans[0].Trace, trace2.Spans[0].Trace);
    }

    [Fact]
    public void AddTraces_AttributeAndEventLimits_LimitsApplied()
    {
        // Arrange
        var repository = CreateRepository(maxAttributeCount: 5, maxAttributeLength: 16, maxSpanEventCount: 5);

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
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void AddTraces_Links_BacklinksPopulated()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        AddTrace(repository, "1", s_testTime);
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void AddTraces_ExceedLimit_FirstInFirstOut()
    {
        // Arrange
        const int MaxTraceCount = 10;
        var repository = CreateRepository(maxTraceCount: MaxTraceCount);

        var testTime = s_testTime.AddDays(1);

        // Act
        for (var i = 0; i < 2000; i++)
        {
            var traceNumber = i + 1;
            var traceId = traceNumber.ToString(CultureInfo.InvariantCulture);

            // Insert traces out of order to stress the circular buffer type.
            var startTime = testTime.AddMinutes(i + (i % 2 == 0 ? -5 : 0));

            try
            {
                AddTrace(repository, traceId, startTime);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Error adding trace number {i}.", ex);
            }
        }

        // Assert
        var resources = repository.GetResources();
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

        // Most recent traces are returned.
        var first = GetStringId(traces.PagedResult.Items.First().TraceId);
        var last = GetStringId(traces.PagedResult.Items.Last().TraceId);
        Assert.Equal("1988", first);
        Assert.Equal("2000", last);

        // Traces returned are ordered by start time.
        var actualOrder = traces.PagedResult.Items.Select(t => t.TraceId).ToList();
        var expectedOrder = traces.PagedResult.Items.OrderBy(t => t.FirstSpan.StartTime).Select(t => t.TraceId).ToList();
        Assert.Equal(expectedOrder, actualOrder);

        Assert.Equal(MaxTraceCount * 2, repository.SpanLinks.Count);
    }

    private static void AddTrace(TelemetryRepository repository, string traceId, DateTime startTime)
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

        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
    public void AddTraces_MultipleRootSpans_RootSpanIsEarliestWithoutParent()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void GetTraces_MultipleInstances()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            },
            trace =>
            {
                AssertId("2", trace.TraceId);
            });

        var propertyKeys = repository.GetTracePropertyKeys(resourceKey)!;
        Assert.Collection(propertyKeys,
            s => Assert.Equal("key-1", s),
            s => Assert.Equal("key-2", s));
    }

    [Fact]
    public void GetTraces_AttributeFilters()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.Equals, Value = "value1" }
            ]
        });
        // Assert 1
        // Match first span.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });

        // Act 2
        traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key2", Condition = FilterCondition.Equals, Value = "value2" }
            ]
        });
        // Assert 2
        // Match second span.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });

        // Act 3
        traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.Equals, Value = "value1" },
                new FieldTelemetryFilter { Field = "key2", Condition = FilterCondition.Equals, Value = "value2" }
            ]
        });
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
    public void GetTraces_KnownFilters(string name, string value)
    {
        // Arrange
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        var repository = CreateRepository(outgoingPeerResolvers: [outgoingPeerResolver]);

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = name, Condition = FilterCondition.NotEqual, Value = value }
            ]
        });

        // Assert 1
        // Doesn't match filter.
        Assert.Empty(traces.PagedResult.Items);

        // Act 2
        traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = name, Condition = FilterCondition.Equals, Value = value }
            ]
        });

        // Assert 2
        // Matches filter.
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });
    }

    [Fact]
    public void GetTraces_FiltersPagingAndMaxDuration_ComputedFromAllMatchingTraces()
    {
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = new ResourceKey("resource1", InstanceId: null),
            StartIndex = 1,
            Count = 1,
            Filters =
            [
                new FieldTelemetryFilter { Field = "dynamic.filter", Condition = FilterCondition.Equals, Value = "match" },
                new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.GreaterThanOrEqual, Value = "20" }
            ]
        });

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
    public void GetTraces_DurationFilter_AppliesTraceLevelDuration()
    {
        // Verifies that the duration filter uses the trace's overall duration (first span
        // start to latest span end), not individual span durations. A trace with a 100ms
        // root span containing a 5ms child span should match "> 50ms" (trace is 100ms)
        // but NOT "< 10ms" (even though the child span is only 5ms).
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.GreaterThan, Value = "50" }]
        });

        Assert.Single(traces.PagedResult.Items);

        // Duration filter "< 10ms" should NOT match because trace duration is 100ms,
        // even though the child span is only 5ms.
        traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = [new FieldTelemetryFilter { Field = KnownTraceFields.DurationField, Condition = FilterCondition.LessThan, Value = "10" }]
        });

        Assert.Empty(traces.PagedResult.Items);
    }

    [Fact]
    public void GetTraces_NotEqualFilter_NonMatchingValue_ReturnsTrace()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = new ResourceKey("resource1", InstanceId: null),
            StartIndex = 0,
            Count = 10,
            Filters = [
                new FieldTelemetryFilter { Field = "key1", Condition = FilterCondition.NotEqual, Value = "other_value" }
            ]
        });

        // Assert
        Assert.Collection(traces.PagedResult.Items,
            trace =>
            {
                AssertId("1", trace.TraceId);
            });
    }

    [Fact]
    public void AddTraces_OutOfOrder_FullName()
    {
        // Arrange
        var repository = CreateRepository();
        var request = new GetTracesRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            StartIndex = 0,
            Count = 10,
            Filters = []
        };

        // Act 1
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var trace = Assert.Single(repository.GetTraces(request).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-3", trace.FullName);

        // Act 2
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        trace = Assert.Single(repository.GetTraces(request).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-2", trace.FullName);

        // Act 3
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        trace = Assert.Single(repository.GetTraces(request).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-1", trace.FullName);

        // Act 4
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        trace = Assert.Single(repository.GetTraces(request).PagedResult.Items);
        Assert.Equal("TestService: Test span. Id: 1-1", trace.FullName);
    }

    [Fact]
    public void AddTraces_SameResourceDifferentProperties_MultipleResourceViews()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        var resource = Assert.Single(repository.GetResources());
        Assert.Equal("TestService", resource.ResourceName);
        Assert.Equal("TestId", resource.InstanceId);

        // Spans have different views
        var views = resource.GetViews().OrderBy(v => v.Properties.Length).ToList();
        Assert.Collection(views,
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resource.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });
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
    public void RemoveTraces_All()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        repository.ClearTraces();

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

        Assert.NotNull(traces?.PagedResult?.Items);
        Assert.Empty(traces.PagedResult.Items);
        Assert.Equal(0, traces.PagedResult.TotalItemCount);
    }

    [Fact]
    public void RemoveTraces_SelectedResource()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        repository.ClearTraces(new ResourceKey("resource1", "123"));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void RemoveTraces_MultipleSelectedResources()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        repository.ClearTraces(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void RemoveTraces_SelectedResource_SpansFromDifferentTrace()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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
        repository.ClearTraces(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void AddTraces_HaveUninstrumentedPeers()
    {
        // Arrange
        var outgoingPeerResolver = new TestOutgoingPeerResolver();
        var repository = CreateRepository(outgoingPeerResolvers: [outgoingPeerResolver]);

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources(includeUninstrumentedPeers: true);
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

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = uninstrumentedPeerApp.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
        var repository = CreateRepository(outgoingPeerResolvers: [outgoingPeerResolver]);

        // Act
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources(includeUninstrumentedPeers: true);
        Assert.Collection(resources,
            resource =>
            {
                Assert.Equal("TestService", resource.ResourceName);
                Assert.Equal("TestId", resource.InstanceId);
                Assert.False(resource.UninstrumentedPeer);
            });

        var traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = resources[0].ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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

        resources = repository.GetResources(includeUninstrumentedPeers: true);
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

        traces = repository.GetTraces(new GetTracesRequest
        {
            ResourceKey = uninstrumentedPeerApp.ResourceKey,
            StartIndex = 0,
            Count = 10,
            Filters = []
        });

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
    public void AddTraces_UninstrumentedPeer_InstanceIdDashes_AppKeyResolvedCorrectly()
    {
        // Arrange
        var resource = ModelTestHelpers.CreateResource(resourceName: "test-abc-def", displayName: "test");
        var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: attributes => (resource.Name, resource));
        var repository = CreateRepository(outgoingPeerResolvers: [outgoingPeerResolver]);
        var addContext = new AddContext();
        repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources(includeUninstrumentedPeers: true);
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
    public void GetSpans_ReturnsAllSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        });

        // Assert
        Assert.Equal(3, result.PagedResult.TotalItemCount);
        Assert.Equal(3, result.PagedResult.Items.Count);
    }

    [Fact]
    public void GetSpans_FilterByTraceId_ReturnsMatchingSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TraceId = "31" // hex prefix of "1"
        });

        // Assert
        Assert.Equal(2, result.PagedResult.TotalItemCount);
        Assert.All(result.PagedResult.Items, s => AssertId("1", s.TraceId));
    }

    [Fact]
    public void GetSpans_FilterByHasError_ReturnsErrorSpansOnly()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = true
        });

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_FilterByHasErrorFalse_ReturnsNonErrorSpansOnly()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = false
        });

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-2", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_FilterByResource_ReturnsMatchingSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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

        var resources = repository.GetResources();
        var serviceA = resources.Single(r => r.ResourceName == "service-a");

        // Act
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = serviceA.ResourceKey,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        });

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_FilterByDuration_ReturnsMatchingSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
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
        });

        // Assert - only the long span matches
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_FilterByTextFragments_ReturnsMatchingSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TextFragments = ["example.com"]
        });

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_Pagination_ReturnsCorrectPage()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 1,
            Count = 1,
            Filters = []
        });

        // Assert
        Assert.Equal(3, result.PagedResult.TotalItemCount);
        Assert.Single(result.PagedResult.Items);
        AssertId("1-2", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_CombinedFilters_ReturnsMatchingSpans()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            HasError = true,
            TextFragments = ["example.com"]
        });

        // Assert
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        AssertId("1-1", result.PagedResult.Items[0].SpanId);
    }

    [Fact]
    public void GetSpans_EmptyRepository_ReturnsEmpty()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        });

        // Assert
        Assert.Equal(0, result.PagedResult.TotalItemCount);
        Assert.Empty(result.PagedResult.Items);
    }

    [Fact]
    public void GetSpans_UnknownResource_ReturnsEmpty()
    {
        // Arrange
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = ResourceKey.Create("nonexistent", "unknown"),
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = []
        });

        // Assert
        Assert.Equal(0, result.PagedResult.TotalItemCount);
        Assert.Empty(result.PagedResult.Items);
    }

    [Theory]
    [InlineData("747261636531", 1)] // full hex trace ID — prefix match
    [InlineData("7472616", 1)] // 7 chars — meets ShortenedIdLength, prefix match
    [InlineData("747261", 0)] // 6 chars — below ShortenedIdLength, requires exact match
    public void GetSpans_TraceIdPrefixLength_MatchesShortenedIds(string traceIdFilter, int expectedCount)
    {
        // Arrange
        var repository = CreateRepository();

        // Use a trace ID whose hex representation is "747261636531" (UTF-8 bytes of "trace1")
        var traceId = Encoding.UTF8.GetString(Convert.FromHexString("747261636531"));

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
            StartIndex = 0,
            Count = int.MaxValue,
            Filters = [],
            TraceId = traceIdFilter
        });

        // Assert
        Assert.Equal(expectedCount, result.PagedResult.TotalItemCount);
    }

    [Fact]
    public void GetSpans_DisabledFiltersAreIgnored()
    {
        var repository = CreateRepository();

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
        var result = repository.GetSpans(new GetSpansRequest
        {
            ResourceKey = null,
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
        });

        // The disabled filter should be ignored — only the enabled "span1" filter applies
        Assert.Equal(1, result.PagedResult.TotalItemCount);
        Assert.Contains("span1", result.PagedResult.Items[0].Name);
    }
}
