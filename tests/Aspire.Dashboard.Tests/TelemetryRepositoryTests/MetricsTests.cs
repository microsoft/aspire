// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests;
using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public abstract class MetricsTests : TelemetryRepositoryTestBase
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AddMetrics()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(2)),
                            CreateSumMetric(metricName: "test2", startTime: s_testTime.AddMinutes(1)),
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1)),
                            CreateHistogramMetric(metricName: "test2", startTime: s_testTime.AddMinutes(1))
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

        var instruments = repository.GetInstrumentsSummaries(resources[0].ResourceKey);
        Assert.Collection(instruments,
            instrument =>
            {
                Assert.Equal("test", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test2", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter2", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test2", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter2", instrument.Parent.Name);
            });
    }

    [Fact]
    public void AddMetrics_MeterAttributeLimits_LimitsApplied()
    {
        // Arrange
        var repository = CreateRepository(maxAttributeCount: 5, maxAttributeLength: 16);

        var metricAttributes = new List<KeyValuePair<string, string>>();
        var meterAttributes = new List<KeyValuePair<string, string>>();

        for (var i = 0; i < 10; i++)
        {
            var value = GetValue((i + 1) * 5);
            metricAttributes.Add(new KeyValuePair<string, string>($"Metric_Key{i}", value));
            meterAttributes.Add(new KeyValuePair<string, string>($"Meter_Key{i}", value));
        }

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter", attributes: meterAttributes),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: metricAttributes)
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

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resources[0].ResourceKey,
            InstrumentName = "test",
            MeterName = "test-meter",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        })!;

        Assert.Collection(instrument.Summary.Parent.Attributes,
            p =>
            {
                Assert.Equal("Meter_Key0", p.Key);
                Assert.Equal("01234", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key1", p.Key);
                Assert.Equal("0123456789", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key2", p.Key);
                Assert.Equal("012345678901234", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key3", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key4", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            });

        var dimensionAttributes = instrument.Dimensions.Single().Attributes;

        Assert.Collection(dimensionAttributes,
            p =>
            {
                Assert.Equal("Meter_Key0", p.Key);
                Assert.Equal("01234", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key1", p.Key);
                Assert.Equal("0123456789", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key2", p.Key);
                Assert.Equal("012345678901234", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key3", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            },
            p =>
            {
                Assert.Equal("Meter_Key4", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            });
    }

    [Fact]
    public void AddMetrics_MetricAttributeLimits_LimitsApplied()
    {
        // Arrange
        var repository = CreateRepository(maxAttributeCount: 5, maxAttributeLength: 16);

        var metricAttributes = new List<KeyValuePair<string, string>>();
        var meterAttributes = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("Meter_Key0", GetValue(5))
        };

        for (var i = 0; i < 10; i++)
        {
            var value = GetValue((i + 1) * 5);
            metricAttributes.Add(new KeyValuePair<string, string>($"Metric_Key{i}", value));
        }

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter", attributes: meterAttributes),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: metricAttributes)
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

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resources[0].ResourceKey,
            InstrumentName = "test",
            MeterName = "test-meter",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        })!;

        Assert.Collection(instrument.Summary.Parent.Attributes,
            p =>
            {
                Assert.Equal("Meter_Key0", p.Key);
                Assert.Equal("01234", p.Value);
            });

        var dimensionAttributes = instrument.Dimensions.Single().Attributes;

        Assert.Collection(dimensionAttributes,
            p =>
            {
                Assert.Equal("Meter_Key0", p.Key);
                Assert.Equal("01234", p.Value);
            },
            p =>
            {
                Assert.Equal("Metric_Key0", p.Key);
                Assert.Equal("01234", p.Value);
            },
            p =>
            {
                Assert.Equal("Metric_Key1", p.Key);
                Assert.Equal("0123456789", p.Value);
            },
            p =>
            {
                Assert.Equal("Metric_Key2", p.Key);
                Assert.Equal("012345678901234", p.Value);
            },
            p =>
            {
                Assert.Equal("Metric_Key3", p.Key);
                Assert.Equal("0123456789012345", p.Value);
            });
    }

    [Fact]
    public void RoundtripSeconds()
    {
        var start = s_testTime.AddMinutes(1);
        var nanoSeconds = DateTimeToUnixNanoseconds(start);
        var end = OtlpHelpers.UnixNanoSecondsToDateTime(nanoSeconds);
        Assert.Equal(start, end);
    }

    [Fact]
    public void GetInstrument()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), exemplars: new List<Exemplar> { CreateExemplar(startTime: s_testTime.AddMinutes(1), value: 2, attributes: [KeyValuePair.Create("key1", "value1")]) }),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(2)),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key1", "value1")]),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key1", "value2")]),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key1", "value1"), KeyValuePair.Create("key2", "value1")]),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key1", "value1"), KeyValuePair.Create("key2", "")])
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

        var instrumentData = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resources[0].ResourceKey,
            InstrumentName = "test",
            MeterName = "test-meter",
            StartTime = s_testTime.AddMinutes(1),
            EndTime = s_testTime.AddMinutes(1.5),
        });

        Assert.NotNull(instrumentData);
        Assert.Equal("test", instrumentData.Summary.Name);
        Assert.Equal("Test metric description", instrumentData.Summary.Description);
        Assert.Equal("widget", instrumentData.Summary.Unit);
        Assert.Equal("test-meter", instrumentData.Summary.Parent.Name);

        Assert.Collection(instrumentData.KnownAttributeValues.OrderBy(kvp => kvp.Key),
            e =>
            {
                Assert.Equal("key1", e.Key);
                Assert.Equal(new[] { null, "value1", "value2" }, e.Value);
            },
            e =>
            {
                Assert.Equal("key2", e.Key);
                Assert.Equal(new[] { null, "value1", "" }, e.Value);
            });

        Assert.Equal(5, instrumentData.Dimensions.Count);

        var dimension = instrumentData.Dimensions.Single(d => d.Attributes.Length == 0);
        var exemplar = Assert.Single(dimension.Values[0].Exemplars);

        Assert.Equal("key1", exemplar.Attributes[0].Key);
        Assert.Equal("value1", exemplar.Attributes[0].Value);

        var instrument = resources.Single().GetInstrument("test-meter", "test", s_testTime.AddMinutes(1), s_testTime.AddMinutes(1.5));
        Assert.NotNull(instrument);

        AssertDimensionValues(instrument.Dimensions, Array.Empty<KeyValuePair<string, string>>(), valueCount: 1);
        AssertDimensionValues(instrument.Dimensions, new KeyValuePair<string, string>[] { KeyValuePair.Create("key1", "value1") }, valueCount: 1);
        AssertDimensionValues(instrument.Dimensions, new KeyValuePair<string, string>[] { KeyValuePair.Create("key1", "value2") }, valueCount: 1);
        AssertDimensionValues(instrument.Dimensions, new KeyValuePair<string, string>[] { KeyValuePair.Create("key1", "value1"), KeyValuePair.Create("key2", "value1") }, valueCount: 1);
    }

    [Fact]
    public void GetInstrumentLatestEndTime()
    {
        var repository = CreateRepository();
        repository.AsWriter().AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(2), attributes: [KeyValuePair.Create("key", "value")])
                        }
                    }
                }
            }
        });
        var resourceKey = Assert.Single(repository.GetResources()).ResourceKey;

        Assert.Equal(s_testTime.AddMinutes(2), repository.GetInstrumentLatestEndTime(resourceKey, "test-meter", "test"));
        Assert.Null(repository.GetInstrumentLatestEndTime(resourceKey, "test-meter", "missing"));
    }

    protected static Exemplar CreateExemplar(DateTime startTime, double value, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var exemplar = new Exemplar
        {
            TimeUnixNano = DateTimeToUnixNanoseconds(startTime),
            AsDouble = value,
            SpanId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("span-id")),
            TraceId = ByteString.CopyFrom(Encoding.UTF8.GetBytes("trace-id"))
        };

        if (attributes != null)
        {
            foreach (var attribute in attributes)
            {
                exemplar.FilteredAttributes.Add(new KeyValue { Key = attribute.Key, Value = new AnyValue { StringValue = attribute.Value } });
            }
        }

        return exemplar;
    }

    [Fact]
    public void AddMetrics_Capacity_ValuesRemoved()
    {
        // Arrange
        var repository = CreateRepository(maxMetricsCount: 3);

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), value: 1),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(2), value: 2),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(3), value: 3),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(4), value: 4),
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(5), value: 5),
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

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resources[0].ResourceKey,
            InstrumentName = "test",
            MeterName = "test-meter",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        })!;

        Assert.Equal("test", instrument.Summary.Name);
        Assert.Equal("Test metric description", instrument.Summary.Description);
        Assert.Equal("widget", instrument.Summary.Unit);
        Assert.Equal("test-meter", instrument.Summary.Parent.Name);

        // Only the last 3 values should be kept.
        var dimension = Assert.Single(instrument.Dimensions);
        Assert.Collection(dimension.Values,
            m =>
            {
                Assert.Equal(s_testTime.AddMinutes(2), m.Start);
                Assert.Equal(s_testTime.AddMinutes(3), m.End);
                Assert.Equal(3, ((MetricValue<long>)m).Value);
            },
            m =>
            {
                Assert.Equal(s_testTime.AddMinutes(3), m.Start);
                Assert.Equal(s_testTime.AddMinutes(4), m.End);
                Assert.Equal(4, ((MetricValue<long>)m).Value);
            },
            m =>
            {
                Assert.Equal(s_testTime.AddMinutes(4), m.Start);
                Assert.Equal(s_testTime.AddMinutes(5), m.End);
                Assert.Equal(5, ((MetricValue<long>)m).Value);
            });
    }

    [Fact]
    public void GetMetrics_MultipleInstances()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 1, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-1")]),
                            CreateSumMetric(metricName: "test1", value: 2, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-2")])
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 3, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-3")]),
                            CreateSumMetric(metricName: "test2", value: 4, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-4")])
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 5, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-5")]),
                            CreateSumMetric(metricName: "test3", value: 6, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-6")])
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resourceKey = new ResourceKey("resource1", InstanceId: null);
        var instruments = repository.GetInstrumentsSummaries(resourceKey);
        Assert.Collection(instruments,
            instrument =>
            {
                Assert.Equal("test1", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test2", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            });

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resourceKey,
            InstrumentName = "test1",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(instrument);
        Assert.Equal("test1", instrument.Summary.Name);

        Assert.Collection(instrument.Dimensions.OrderBy(d => d.Name),
            d =>
            {
                Assert.Equal(KeyValuePair.Create("key-1", "value-1"), d.Attributes.Single());
                Assert.Equal(1, ((MetricValue<long>)d.Values.Single()).Value);
            },
            d =>
            {
                Assert.Equal(KeyValuePair.Create("key-1", "value-2"), d.Attributes.Single());
                Assert.Equal(2, ((MetricValue<long>)d.Values.Single()).Value);
            },
            d =>
            {
                Assert.Equal(KeyValuePair.Create("key-1", "value-3"), d.Attributes.Single());
                Assert.Equal(3, ((MetricValue<long>)d.Values.Single()).Value);
            });

        var knownValues = Assert.Single(instrument.KnownAttributeValues);
        Assert.Equal("key-1", knownValues.Key);

        Assert.Collection(knownValues.Value.Order(),
            v => Assert.Equal("value-1", v),
            v => Assert.Equal("value-2", v),
            v => Assert.Equal("value-3", v));
    }

    [Fact]
    public void RemoveMetrics_All()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 1, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test1", value: 2, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 3, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test2", value: 4, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 5, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test3", value: 6, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        repository.AsWriter().ClearMetrics();

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resource1Key = new ResourceKey("resource1", InstanceId: null);
        var resource1Instruments = repository.GetInstrumentsSummaries(resource1Key);
        Assert.Empty(resource1Instruments);

        var resource2Key = new ResourceKey("resource2", InstanceId: null);
        var resource2Instruments = repository.GetInstrumentsSummaries(resource2Key);

        Assert.Empty(resource2Instruments);
    }

    [Fact]
    public void RemoveMetrics_SelectedResource()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 1, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test1", value: 2, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 3, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test2", value: 4, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 5, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test3", value: 6, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        repository.AsWriter().ClearMetrics(new ResourceKey("resource1", "456"));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resource1Key = new ResourceKey("resource1", InstanceId: null);
        var resource1Instruments = repository.GetInstrumentsSummaries(resource1Key);

        var resource1Instrument = Assert.Single(resource1Instruments);
        Assert.Equal("test1", resource1Instrument.Name);
        Assert.Equal("Test metric description", resource1Instrument.Description);
        Assert.Equal("widget", resource1Instrument.Unit);
        Assert.Equal("test-meter", resource1Instrument.Parent.Name);

        var resource1Test1Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource1Key,
            InstrumentName = "test1",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(resource1Test1Instrument);
        Assert.Equal("test1", resource1Test1Instrument.Summary.Name);

        var resource1Test1Dimensions = Assert.Single(resource1Test1Instrument.Dimensions);
        Assert.Collection(resource1Test1Dimensions.Values,
            v =>
            {
                Assert.Equal(1, ((MetricValue<long>)v).Value);
            },
            v =>
            {
                Assert.Equal(2, ((MetricValue<long>)v).Value);
            });

        var resource1Test2Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource1Key,
            InstrumentName = "test2",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.Null(resource1Test2Instrument);

        var resource2Key = new ResourceKey("resource2", InstanceId: null);
        var resource2Instruments = repository.GetInstrumentsSummaries(resource2Key);

        Assert.Collection(resource2Instruments,
            instrument =>
            {
                Assert.Equal("test1", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test3", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            });

        var resource2Test1Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource2Key,
            InstrumentName = "test1",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(resource2Test1Instrument);
        Assert.Equal("test1", resource2Test1Instrument.Summary.Name);

        var resource2Test1Dimensions = Assert.Single(resource2Test1Instrument.Dimensions);
        Assert.Equal(5, ((MetricValue<long>)resource2Test1Dimensions.Values.Single()).Value);

        var resource2Test3Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource2Key,
            InstrumentName = "test3",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(resource2Test3Instrument);
        Assert.Equal("test3", resource2Test3Instrument.Summary.Name);

        var resource2Test3Dimensions = Assert.Single(resource2Test3Instrument.Dimensions);
        Assert.Equal(6, ((MetricValue<long>)resource2Test3Dimensions.Values.Single()).Value);
    }

    [Fact]
    public void RemoveMetrics_MultipleSelectedResources()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 1, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-1")]),
                            CreateSumMetric(metricName: "test1", value: 2, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-2")]),
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "456"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 3, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test2", value: 4, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource2"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test1", value: 5, startTime: s_testTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "test3", value: 6, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Act
        repository.AsWriter().ClearMetrics(new ResourceKey("resource1", null));

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var resource1Key = new ResourceKey("resource1", InstanceId: null);
        var resource1Instruments = repository.GetInstrumentsSummaries(resource1Key);
        Assert.Empty(resource1Instruments);

        var resource1Test1Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource1Key,
            InstrumentName = "test1",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.Null(resource1Test1Instrument);

        var resource1Test2Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource1Key,
            InstrumentName = "test2",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.Null(resource1Test2Instrument);

        var resource2Key = new ResourceKey("resource2", InstanceId: null);
        var resource2Instruments = repository.GetInstrumentsSummaries(resource2Key);
        Assert.Collection(resource2Instruments,
            instrument =>
            {
                Assert.Equal("test1", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            },
            instrument =>
            {
                Assert.Equal("test3", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            });

        var resource2Test1Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource2Key,
            InstrumentName = "test1",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(resource2Test1Instrument);
        Assert.Equal("test1", resource2Test1Instrument.Summary.Name);

        var resource2Test1Dimensions = Assert.Single(resource2Test1Instrument.Dimensions);
        Assert.Equal(5, ((MetricValue<long>)resource2Test1Dimensions.Values.Single()).Value);

        var resource2Test3Instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resource2Key,
            InstrumentName = "test3",
            MeterName = "test-meter",
            StartTime = s_testTime,
            EndTime = s_testTime.AddMinutes(20)
        });

        Assert.NotNull(resource2Test3Instrument);
        Assert.Equal("test3", resource2Test3Instrument.Summary.Name);

        var resource2Test3Dimensions = Assert.Single(resource2Test3Instrument.Dimensions);
        Assert.Equal(6, ((MetricValue<long>)resource2Test3Dimensions.Values.Single()).Value);
    }

    [Fact]
    public void AddMetrics_InvalidInstrument()
    {
        // Arrange
        var repository = CreateRepository();

        var addContext = new AddContext();

        // Act
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: "resource1", instanceId: "123"),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "", value: 1, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-1")]),
                            CreateSumMetric(metricName: "test1", value: 2, startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("key-1", "value-2")]),
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(1, addContext.FailureCount);

        var resource1Key = new ResourceKey("resource1", InstanceId: null);
        var resource1Instruments = repository.GetInstrumentsSummaries(resource1Key);
        Assert.Collection(resource1Instruments,
            instrument =>
            {
                Assert.Equal("test1", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Equal("test-meter", instrument.Parent.Name);
            });
    }

    [Fact]
    public void AddMetrics_InvalidHistogramDataPoints()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();

        var histogramMetric = new Metric
        {
            Name = "test",
            Description = "Test metric description",
            Unit = "widget",
            Histogram = new Histogram
            {
                AggregationTemporality = AggregationTemporality.Cumulative,
                DataPoints =
                {
                    new HistogramDataPoint
                    {
                        Count = 6,
                        Sum = 1,
                        ExplicitBounds = { },
                        BucketCounts = { 1 },
                        TimeUnixNano = DateTimeToUnixNanoseconds(s_testTime.AddMinutes(1))
                    },
                    new HistogramDataPoint
                    {
                        Count = 6,
                        Sum = 1,
                        ExplicitBounds = { },
                        BucketCounts = { 1 },
                        TimeUnixNano = DateTimeToUnixNanoseconds(s_testTime.AddMinutes(2))
                    },
                    new HistogramDataPoint
                    {
                        Count = 6,
                        Sum = 1,
                        ExplicitBounds = { 1, 2, 3 },
                        BucketCounts = { 1, 2, 3 },
                        TimeUnixNano = DateTimeToUnixNanoseconds(s_testTime.AddMinutes(3))
                    }
                }
            }
        };

        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics = { histogramMetric }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(2, addContext.FailureCount);

        var resources = Assert.Single(repository.GetResources());

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = resources.ResourceKey,
            MeterName = "test-meter",
            InstrumentName = "test",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });

        Assert.NotNull(instrument);
        Assert.Equal("test", instrument.Summary.Name);
        Assert.Equal("Test metric description", instrument.Summary.Description);
        Assert.Equal("widget", instrument.Summary.Unit);
        Assert.Equal("test-meter", instrument.Summary.Parent.Name);

        var dimension = Assert.Single(instrument.Dimensions);
        Assert.Single(dimension.Values);
    }

    [Fact]
    public void AddMetrics_OverflowDimension()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1), attributes: [KeyValuePair.Create("otel.metric.overflow", "true")])
                        }
                    },
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter2"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        // Assert
        Assert.Equal(0, addContext.FailureCount);

        var instrument1 = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            InstrumentName = "test",
            MeterName = "test-meter",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });

        Assert.NotNull(instrument1);
        Assert.True(instrument1.HasOverflow);

        var instrument2 = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            InstrumentName = "test",
            MeterName = "test-meter2",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });

        Assert.NotNull(instrument2);
        Assert.False(instrument2.HasOverflow);
    }

    [Fact]
    public void AddMetrics_NoScope()
    {
        // Arrange
        var repository = CreateRepository();

        // Act
        var addContext = new AddContext();
        repository.AsWriter().AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = null,
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_testTime.AddMinutes(1))
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

        var instruments = repository.GetInstrumentsSummaries(resources[0].ResourceKey);
        Assert.Collection(instruments,
            instrument =>
            {
                Assert.Equal("test", instrument.Name);
                Assert.Equal("Test metric description", instrument.Description);
                Assert.Equal("widget", instrument.Unit);
                Assert.Same(OtlpScope.Empty, instrument.Parent);
            });
    }

    private static void AssertDimensionValues(Dictionary<ReadOnlyMemory<KeyValuePair<string, string>>, DimensionScope> dimensions, ReadOnlyMemory<KeyValuePair<string, string>> key, int valueCount)
    {
        var scope = dimensions[key];
        Assert.True(Enumerable.SequenceEqual(MemoryMarshal.ToEnumerable(key), scope.Attributes), "Key and attributes don't match.");

        Assert.Equal(valueCount, scope.Values.Count);
    }
}

public sealed class InMemoryMetricsTests : MetricsTests
{
    protected override bool UseSqlite => false;
}

public sealed class SqliteMetricsTests : MetricsTests
{
    private static readonly DateTime s_queryTestTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    protected override bool UseSqlite => true;

    [Fact]
    public void AddMetrics_ReusesInstrumentAndDimensionLookupsWithinBatch()
    {
        var repository = Assert.IsType<SqliteTelemetryRepository>(CreateRepository());
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(repository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("metric ingestion test").Start();

        var context = new AddContext();
        repository.AsWriter().AddMetrics(context, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(1), value: 1),
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(2), value: 2),
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(3), value: 2)
                        }
                    }
                }
            }
        });

        var queries = activities
            .Where(activity => activity.ParentSpanId == parent.SpanId)
            .Select(activity => (string)activity.GetTagItem("db.query.text")!)
            .ToList();
        Assert.Single(queries, query => query.Contains("SELECT instrument_id", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("FROM telemetry_metric_dimensions d", StringComparison.Ordinal));
        Assert.Single(queries, query => query.StartsWith("DELETE FROM telemetry_metric_points", StringComparison.Ordinal));
        var insertQuery = Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_points", StringComparison.Ordinal));
        Assert.Equal(2, insertQuery.Split("INSERT INTO telemetry_metric_points", StringSplitOptions.None).Length - 1);
        Assert.Equal(3, context.SuccessCount);
    }

    [Fact]
    public void AddMetrics_BatchesHistogramChildrenAndDimensionTrimming()
    {
        var repository = Assert.IsType<SqliteTelemetryRepository>(CreateRepository());
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(repository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("metric ingestion test").Start();

        var context = new AddContext();
        repository.AsWriter().AddMetrics(context, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateHistogramMetric(metricName: "histogram", startTime: s_queryTestTime.AddMinutes(1)),
                            CreateSumMetric(metricName: "sum", startTime: s_queryTestTime.AddMinutes(1), attributes: [KeyValuePair.Create("series", "one")]),
                            CreateSumMetric(metricName: "sum", startTime: s_queryTestTime.AddMinutes(1), attributes: [KeyValuePair.Create("series", "two")])
                        }
                    }
                }
            }
        });

        var queries = activities
            .Where(activity => activity.ParentSpanId == parent.SpanId)
            .Select(activity => (string)activity.GetTagItem("db.query.text")!)
            .ToList();
        Assert.Single(queries, query => query.Contains("SELECT instrument_id", StringComparison.Ordinal));
        Assert.Single(queries, query => query.StartsWith("SELECT COUNT(*) FROM telemetry_metric_instruments", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("FROM telemetry_metric_dimensions d", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.StartsWith("SELECT COUNT(*) FROM telemetry_metric_dimensions", StringComparison.Ordinal));
        var instrumentInsert = Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_instruments", StringComparison.Ordinal));
        Assert.Equal(2, instrumentInsert.Split("@InstrumentName", StringSplitOptions.None).Length - 1);
        var dimensionInsert = Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_dimensions", StringComparison.Ordinal));
        Assert.Equal(3, dimensionInsert.Split("@InstrumentId", StringSplitOptions.None).Length - 1);
        Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_dimension_attributes", StringComparison.Ordinal));
        Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_histogram_bucket_counts", StringComparison.Ordinal));
        Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_histogram_explicit_bounds", StringComparison.Ordinal));
        Assert.Single(queries, query => query.StartsWith("DELETE FROM telemetry_metric_points", StringComparison.Ordinal));
        Assert.Equal(3, context.SuccessCount);
        Assert.Equal(0, context.FailureCount);
    }

    [Fact]
    public void AddMetrics_BatchesAndDeduplicatesExemplars()
    {
        var repository = Assert.IsType<SqliteTelemetryRepository>(CreateRepository());
        repository.AsWriter().AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            CreateResourceMetrics(CreateSumMetric(
                metricName: "test",
                startTime: s_queryTestTime.AddMinutes(1),
                value: 1,
                exemplars: [CreateExemplar(s_queryTestTime.AddMinutes(1), 2, [KeyValuePair.Create("first", "value")])]))
        });
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(repository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("metric exemplar test").Start();

        var context = new AddContext();
        repository.AsWriter().AddMetrics(context, new RepeatedField<ResourceMetrics>
        {
            CreateResourceMetrics(CreateSumMetric(
                metricName: "test",
                startTime: s_queryTestTime.AddMinutes(2),
                value: 1,
                exemplars:
                [
                    CreateExemplar(s_queryTestTime.AddMinutes(1), 2, [KeyValuePair.Create("first", "value")]),
                    CreateExemplar(s_queryTestTime.AddMinutes(2), 3, [KeyValuePair.Create("second", "value")])
                ]))
        });

        var queries = activities
            .Where(activity => activity.ParentSpanId == parent.SpanId)
            .Select(activity => (string)activity.GetTagItem("db.query.text")!)
            .ToList();
        Assert.DoesNotContain(queries, query => query.Contains("SELECT EXISTS", StringComparison.Ordinal));
        var exemplarInsert = Assert.Single(queries, query => query.StartsWith("INSERT OR IGNORE INTO telemetry_metric_exemplars", StringComparison.Ordinal));
        Assert.Equal(2, exemplarInsert.Split("@PointId", StringSplitOptions.None).Length - 1);
        Assert.Single(queries, query => query.StartsWith("INSERT INTO telemetry_metric_exemplar_attributes", StringComparison.Ordinal));
        Assert.Equal(1, context.SuccessCount);
        Assert.Equal(0, context.FailureCount);

        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = CreateResource().GetResourceKey(),
            MeterName = "test-meter",
            InstrumentName = "test",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });
        var value = Assert.IsType<MetricValue<long>>(Assert.Single(Assert.Single(instrument!.Dimensions).Values));
        Assert.Collection(value.Exemplars,
            exemplar => Assert.Equal("first", Assert.Single(exemplar.Attributes).Key),
            exemplar => Assert.Equal("second", Assert.Single(exemplar.Attributes).Key));
    }

    [Fact]
    public void AddMetrics_UpdateOnlyBatch_DoesNotTrimMetricPoints()
    {
        var repository = Assert.IsType<SqliteTelemetryRepository>(CreateRepository());
        repository.AsWriter().AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            CreateResourceMetrics(CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(1), value: 1))
        });
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(repository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("metric update test").Start();

        repository.AsWriter().AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(2), value: 1),
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(3), value: 1),
                            CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(4), value: 1)
                        }
                    }
                }
            }
        });

        var queries = activities
            .Where(activity => activity.ParentSpanId == parent.SpanId)
            .Select(activity => (string)activity.GetTagItem("db.query.text")!)
            .ToList();
        Assert.DoesNotContain(queries, query => query.Contains("SELECT resource_id", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("FROM telemetry_scopes", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("FROM telemetry_scope_attributes", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("SELECT instrument_id", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.Contains("FROM telemetry_metric_dimensions d", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.StartsWith("UPDATE telemetry_resources SET has_metrics", StringComparison.Ordinal));
        Assert.DoesNotContain(queries, query => query.StartsWith("DELETE FROM telemetry_metric_points", StringComparison.Ordinal));
        var updateQuery = Assert.Single(queries, query => query.Contains("UPDATE telemetry_metric_points", StringComparison.Ordinal));
        Assert.Contains("WITH updates", updateQuery, StringComparison.Ordinal);
        Assert.Contains("FROM updates", updateQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT end_time_ticks FROM updates", updateQuery, StringComparison.Ordinal);
        var instrument = repository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = CreateResource().GetResourceKey(),
            MeterName = "test-meter",
            InstrumentName = "test",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });
        var value = Assert.IsType<MetricValue<long>>(Assert.Single(Assert.Single(instrument!.Dimensions).Values));
        Assert.Equal(4UL, value.Count);
        Assert.Equal(s_queryTestTime.AddMinutes(4), value.End);
    }

    [Fact]
    public void ClearMetrics_InvalidatesMetricIngestionCache()
    {
        var repository = CreateRepository();
        repository.AsWriter().AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            CreateResourceMetrics(CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(1), value: 1))
        });

        repository.AsWriter().ClearMetrics();

        var context = new AddContext();
        repository.AsWriter().AddMetrics(context, new RepeatedField<ResourceMetrics>
        {
            CreateResourceMetrics(CreateSumMetric(metricName: "test", startTime: s_queryTestTime.AddMinutes(2), value: 2))
        });

        Assert.Equal(1, context.SuccessCount);
        Assert.Equal(0, context.FailureCount);
        Assert.Single(repository.GetInstrumentsSummaries(CreateResource().GetResourceKey()));
    }

    private static ResourceMetrics CreateResourceMetrics(Metric metric) => new()
    {
        Resource = CreateResource(),
        ScopeMetrics =
        {
            new ScopeMetrics
            {
                Scope = CreateScope(name: "test-meter"),
                Metrics = { metric }
            }
        }
    };
}
