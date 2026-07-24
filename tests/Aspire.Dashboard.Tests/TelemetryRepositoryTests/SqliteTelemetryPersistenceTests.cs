// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests;
using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public sealed class SqliteTelemetryPersistenceTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Cache_UsesCanonicalResourceViewAndScopeAcrossSignals()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var startTime = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var resource = CreateResource(attributes: [KeyValuePair.Create("resource-key", "resource-value")]);
        var scope = CreateScope(name: "SharedScope", attributes: [KeyValuePair.Create("scope-key", "scope-value")]);
        using var repository = CreateRepository(workspace.Path);

        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = resource,
                ScopeLogs = { new ScopeLogs { Scope = scope, LogRecords = { CreateLogRecord() } } }
            }
        });
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = resource,
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = scope,
                        Spans = { CreateSpan("cache-trace", "cache-span", startTime, startTime.AddSeconds(1)) }
                    }
                }
            }
        });
        await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = resource,
                ScopeMetrics = { new ScopeMetrics { Scope = scope, Metrics = { CreateSumMetric("requests", startTime) } } }
            }
        });

        var cachedResource = Assert.Single(repository.GetResources());
        var log = Assert.Single(repository.GetLogs(CreateLogsContext()).Items);
        var span = Assert.Single(Assert.Single(repository.GetTraces(GetTracesRequest.ForResourceKey(cachedResource.ResourceKey)).PagedResult.Items).Spans);
        var instrument = Assert.Single(repository.GetInstrumentSummaries(cachedResource.ResourceKey));

        Assert.Same(cachedResource, log.ResourceView.Resource);
        Assert.Same(cachedResource, span.Source.Resource);
        Assert.Same(log.ResourceView, span.Source);
        Assert.Same(log.Scope, span.Scope);
        Assert.Same(log.Scope, instrument.Parent);
    }

    [Fact]
    public async Task Cache_HydratesPersistedMetadataOnce()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var startTime = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(attributes: [KeyValuePair.Create("resource-key", "resource-value")]),
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope("TestScope"), LogRecords = { CreateLogRecord() } } }
                }
            });
            await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics = { new ScopeMetrics { Scope = CreateScope("TestScope"), Metrics = { CreateSumMetric("requests", startTime) } } }
                }
            });
        }

        using var historicalRepository = CreateRepository(workspace.Path, readOnly: true);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(historicalRepository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("cache hydration test").Start();
        var firstResource = Assert.Single(historicalRepository.GetResources());
        Assert.NotEmpty(activities);
        activities.Clear();

        var secondResource = Assert.Single(historicalRepository.GetResources());
        var summary = Assert.Single(historicalRepository.GetInstrumentSummaries(firstResource.ResourceKey));
        var views = firstResource.GetViews().OrderBy(view => view.Properties.Length).ToList();

        Assert.Same(firstResource, secondResource);
        Assert.Collection(views,
            view =>
            {
                Assert.Same(firstResource, view.Resource);
                Assert.Empty(view.Properties);
            },
            view =>
            {
                Assert.Same(firstResource, view.Resource);
                var property = Assert.Single(view.Properties);
                Assert.Equal(KeyValuePair.Create("resource-key", "resource-value"), property);
            });
        Assert.Equal("requests", summary.Name);
        Assert.Empty(activities);
    }

    [Fact]
    public async Task Metrics_ReopenWithResourceView()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Scope = CreateScope("TestScope"),
                            Metrics = { CreateSumMetric("requests", new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc)) }
                        }
                    }
                }
            });
        }

        using var reopenedRepository = CreateRepository(workspace.Path, readOnly: true);
        var resource = Assert.Single(reopenedRepository.GetResources());
        var view = Assert.Single(resource.GetViews());

        Assert.Same(resource, view.Resource);
        Assert.Empty(view.Properties);
    }

    [Fact]
    public async Task Logs_ReopenFromNormalizedRowsWithStableIds()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        long logId;
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = CreateScope("TestLogger"),
                            LogRecords = { CreateLogRecord() }
                        }
                    }
                }
            });

            var log = Assert.Single(repository.GetLogs(CreateLogsContext()).Items);
            logId = log.InternalId;
        }

        using (var historicalRepository = CreateRepository(workspace.Path, readOnly: true))
        {
            var log = Assert.Single(historicalRepository.GetLogs(CreateLogsContext()).Items);
            Assert.Equal(logId, log.InternalId);
            Assert.Equal("Test Value!", log.Message);
            Assert.Equal("TestLogger", log.Scope.Name);
            Assert.Equal(logId, historicalRepository.GetLog(logId)!.InternalId);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'telemetry_records';";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_logs;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Traces_ReopenFromNormalizedRowsWithStableEventIds()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var link = new Span.Types.Link
        {
            TraceId = ByteString.CopyFromUtf8("2"),
            SpanId = ByteString.CopyFromUtf8("2-1"),
            TraceState = "state"
        };
        Guid eventId;
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope("TestSource"),
                            Spans =
                            {
                                CreateSpan(
                                    traceId: "1",
                                    spanId: "1-1",
                                    startTime,
                                    startTime.AddSeconds(2),
                                    events: [CreateSpanEvent("event", 1, [KeyValuePair.Create("event-key", "event-value")])],
                                    links: [link],
                                    attributes: [KeyValuePair.Create("span-key", "span-value")]),
                                CreateSpan("1", "1-2", startTime.AddSeconds(1), startTime.AddSeconds(2), parentSpanId: "1-1")
                            }
                        }
                    }
                }
            });

            var trace = Assert.Single(repository.GetTraces(GetTracesRequest.ForResourceKey(new ResourceKey("TestService", "TestId"))).PagedResult.Items);
            eventId = Assert.Single(trace.FirstSpan.Events).InternalId;
        }

        using (var historicalRepository = CreateRepository(workspace.Path, readOnly: true))
        {
            var trace = Assert.Single(historicalRepository.GetTraces(GetTracesRequest.ForResourceKey(new ResourceKey("TestService", "TestId"))).PagedResult.Items);
            Assert.Equal("TestSource", trace.FirstSpan.Scope.Name);
            Assert.Equal(KeyValuePair.Create("span-key", "span-value"), Assert.Single(trace.FirstSpan.Attributes));
            var spanEvent = Assert.Single(trace.FirstSpan.Events);
            Assert.Equal(eventId, spanEvent.InternalId);
            Assert.Equal(KeyValuePair.Create("event-key", "event-value"), Assert.Single(spanEvent.Attributes));
            var persistedLink = Assert.Single(trace.FirstSpan.Links);
            Assert.Equal(link.TraceId.ToHexString(), persistedLink.TraceId);
            Assert.Equal(link.SpanId.ToHexString(), persistedLink.SpanId);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'telemetry_records';";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_spans;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Metrics_ReopenFromNormalizedRows()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Scope = CreateScope("TestMeter"),
                            Metrics = { CreateSumMetric("requests", startTime, attributes: [KeyValuePair.Create("route", "/api")], value: 42) }
                        }
                    }
                }
            });
        }

        using (var historicalRepository = CreateRepository(workspace.Path, readOnly: true))
        {
            var resourceKey = new ResourceKey("TestService", "TestId");
            var summary = Assert.Single(historicalRepository.GetInstrumentSummaries(resourceKey));
            Assert.Equal("requests", summary.Name);
            Assert.Equal("TestMeter", summary.Parent.Name);

            var instrument = historicalRepository.GetInstrument(new GetInstrumentRequest
            {
                ResourceKey = resourceKey,
                MeterName = "TestMeter",
                InstrumentName = "requests",
                StartTime = startTime.AddMinutes(-1),
                EndTime = startTime.AddMinutes(1)
            });
            var dimension = Assert.Single(instrument!.Dimensions);
            Assert.Equal(KeyValuePair.Create("route", "/api"), Assert.Single(dimension.Attributes));
            var routeValues = Assert.Single(instrument.KnownAttributeValues);
            Assert.Equal("route", routeValues.Key);
            Assert.Equal("/api", Assert.Single(routeValues.Value));
            Assert.Equal(42, Assert.IsType<MetricValue<long>>(Assert.Single(dimension.Values)).Value);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Metrics_HistogramPackedStorage_ReopensAndRejectsChangedBucketCountLength()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        using (var repository = CreateRepository(workspace.Path))
        {
            var histogram = CreateHistogramMetric("histogram", startTime);
            histogram.Histogram.DataPoints[0].ExplicitBounds.Clear();
            histogram.Histogram.DataPoints[0].ExplicitBounds.Add([1, 2]);
            var addContext = new AddContext();
            await repository.AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics = { new ScopeMetrics { Scope = CreateScope("TestMeter"), Metrics = { histogram } } }
                }
            });
            Assert.Equal(1, addContext.SuccessCount);
            Assert.Equal(0, addContext.FailureCount);
        }

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT length(bucket_counts) FROM telemetry_metric_points;";
            Assert.Equal(24L, command.ExecuteScalar());
            command.CommandText = "SELECT length(explicit_bounds) FROM telemetry_metric_points;";
            Assert.Equal(16L, command.ExecuteScalar());
            command.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_schema
                WHERE type = 'table'
                  AND name IN ('telemetry_metric_histograms', 'telemetry_metric_histogram_bucket_counts', 'telemetry_metric_histogram_explicit_bounds');
                """;
            Assert.Equal(0L, command.ExecuteScalar());
        }

        using var reopenedRepository = CreateRepository(workspace.Path);
        var changedHistogram = CreateHistogramMetric("histogram", startTime.AddMinutes(1));
        changedHistogram.Histogram.DataPoints[0].BucketCounts.Add(4);
        var changedContext = new AddContext();
        await reopenedRepository.AddMetricsAsync(changedContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics = { new ScopeMetrics { Scope = CreateScope("TestMeter"), Metrics = { changedHistogram } } }
            }
        });

        Assert.Equal(0, changedContext.SuccessCount);
        Assert.Equal(1, changedContext.FailureCount);
        var instrument = reopenedRepository.GetInstrument(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "TestMeter",
            InstrumentName = "histogram",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        });
        var value = Assert.IsType<HistogramValue>(Assert.Single(Assert.Single(instrument!.Dimensions).Values));
        Assert.Equal([1UL, 2UL, 3UL], value.Values);
        Assert.Equal([1d, 2d], value.ExplicitBounds);
    }

    [Fact]
    public async Task Metrics_EquivalentAttributesShareIndexedDimension()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        using (var repository = CreateRepository(workspace.Path))
        {
            var addContext = new AddContext();
            foreach (var attributes in new[]
            {
                new[] { KeyValuePair.Create("second", "2"), KeyValuePair.Create("first", "1") },
                new[] { KeyValuePair.Create("first", "1"), KeyValuePair.Create("second", "2") },
                new[] { KeyValuePair.Create("first", "different") }
            })
            {
                await repository.AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
                {
                    new ResourceMetrics
                    {
                        Resource = CreateResource(),
                        ScopeMetrics =
                        {
                            new ScopeMetrics
                            {
                                Scope = CreateScope("TestMeter"),
                                Metrics = { CreateSumMetric("requests", startTime, attributes: attributes) }
                            }
                        }
                    }
                });
            }
            Assert.Equal(0, addContext.FailureCount);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_dimensions;";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'index' AND name = 'ix_telemetry_metric_dimensions_hash';";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Scopes_AreSharedAcrossLogsTracesAndMetrics()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var scope = CreateScope(name: "SharedScope", attributes: [KeyValuePair.Create("scope-key", "scope-value")]);
        using (var repository = CreateRepository(workspace.Path))
        {
            await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = scope,
                            LogRecords = { CreateLogRecord() }
                        }
                    }
                }
            });
            await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource(),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = scope,
                            Spans = { CreateSpan("shared-trace", "shared-span", startTime, startTime.AddSeconds(1)) }
                        }
                    }
                }
            });
            await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Scope = scope,
                            Metrics = { CreateSumMetric("requests", startTime) }
                        }
                    }
                }
            });
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_scopes WHERE scope_name = 'SharedScope';";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = """
            SELECT COUNT(DISTINCT scope_id)
            FROM (
                SELECT scope_id FROM telemetry_logs
                UNION ALL
                SELECT scope_id FROM telemetry_spans
                UNION ALL
                SELECT scope_id FROM telemetry_metric_instruments
            );
            """;
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = """
            SELECT COUNT(*)
            FROM telemetry_scope_attributes
            WHERE attribute_key = 'scope-key' AND attribute_value = 'scope-value';
            """;
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table' AND name IN (
                'telemetry_log_scopes',
                'telemetry_log_scope_attributes',
                'telemetry_trace_scopes',
                'telemetry_trace_scope_attributes',
                'telemetry_metric_scopes',
                'telemetry_metric_scope_attributes');
            """;
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Scopes_ReopenAndReusePersistedScopesWithAndWithoutAttributes()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var attributedScope = CreateScope(name: "AttributedScope", attributes: [KeyValuePair.Create("scope-key", "scope-value")]);
        var emptyScope = CreateScope(name: "EmptyScope");

        for (var iteration = 0; iteration < 2; iteration++)
        {
            using var repository = CreateRepository(workspace.Path);
            var addContext = new AddContext();
            await repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(),
                    ScopeLogs =
                    {
                        new ScopeLogs { Scope = attributedScope, LogRecords = { CreateLogRecord() } },
                        new ScopeLogs { Scope = emptyScope, LogRecords = { CreateLogRecord() } }
                    }
                }
            });
            Assert.Equal(0, addContext.FailureCount);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_scopes;";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_scope_attributes;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_logs;";
        Assert.Equal(4L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ResourceViews_EquivalentAttributesShareNormalizedRows()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using (var repository = CreateRepository(workspace.Path))
        {
            var addContext = new AddContext();
            await repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(attributes: [KeyValuePair.Create("second", "2"), KeyValuePair.Create("first", "1")]),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = CreateScope(),
                            LogRecords = { CreateLogRecord() }
                        }
                    }
                },
                new ResourceLogs
                {
                    Resource = CreateResource(attributes: [KeyValuePair.Create("first", "1"), KeyValuePair.Create("second", "2")]),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = CreateScope(),
                            LogRecords = { CreateLogRecord() }
                        }
                    }
                }
            });
            Assert.Equal(0, addContext.FailureCount);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_views;";
        Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_view_attributes;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task ResourceViews_LimitRejectsNewNormalizedRow()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        using var repository = CreateRepository(workspace.Path);
        var addContext = new AddContext();
            await repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });
        Assert.Equal(0, addContext.FailureCount);

        using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                WITH RECURSIVE numbers(value) AS (
                    SELECT 1
                    UNION ALL
                    SELECT value + 1 FROM numbers WHERE value < {TelemetryRepositoryLimits.MaxResourceViewCount - 1}
                )
                INSERT INTO telemetry_resource_views (resource_id)
                SELECT resource_id
                FROM telemetry_resources
                CROSS JOIN numbers;
                """;
            command.ExecuteNonQuery();
            command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_views;";
            Assert.Equal((long)TelemetryRepositoryLimits.MaxResourceViewCount, command.ExecuteScalar());
        }

            await repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("new", "value")]),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope(),
                        LogRecords = { CreateLogRecord() }
                    }
                }
            }
        });

        Assert.Equal(1, addContext.FailureCount);
    }

    [Fact]
    public async Task Scopes_AreDeletedAfterTheirFinalOwnerIsCleared()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var scope = CreateScope("SharedScope");
        using var repository = CreateRepository(workspace.Path);
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs = { new ScopeLogs { Scope = scope, LogRecords = { CreateLogRecord() } } }
            }
        });
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = scope,
                        Spans = { CreateSpan("shared-trace", "shared-span", startTime, startTime.AddSeconds(1)) }
                    }
                }
            }
        });
        await repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = scope,
                        Metrics = { CreateSumMetric("requests", startTime) }
                    }
                }
            }
        });

        await repository.ClearStructuredLogsAsync();
        Assert.Equal(1L, GetScopeCount(databasePath));
        await repository.ClearTracesAsync();
        Assert.Equal(1L, GetScopeCount(databasePath));
        await repository.ClearMetricsAsync();
        Assert.Equal(0L, GetScopeCount(databasePath));
    }

    [Fact]
    public async Task ResourcesAndResourceViews_AreRetainedAfterSignalsCleared()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        using var repository = CreateRepository(workspace.Path);
        await repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("signal", "logs")]),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope("Logger"), LogRecords = { CreateLogRecord() } } }
            }
        });
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("signal", "traces")]),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope("Tracer"),
                        Spans = { CreateSpan("trace", "span", startTime, startTime.AddSeconds(1)) }
                    }
                }
            }
        });

        await repository.ClearStructuredLogsAsync();
        await repository.ClearTracesAsync();

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resources;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_views;";
        Assert.Equal(3L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_view_attributes;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task TraceTrimming_DoesNotRecalculateResourceFlags()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        using var repository = CreateRepository(workspace.Path, maxTraceCount: 1);
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "FirstService", instanceId: "first"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("first-trace", "first-span", startTime, startTime.AddSeconds(1)) }
                    }
                }
            }
        });
        await repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: "SecondService", instanceId: "second"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans = { CreateSpan("second-trace", "second-span", startTime.AddMinutes(1), startTime.AddMinutes(1).AddSeconds(1)) }
                    }
                }
            }
        });

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_traces;";
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resources WHERE has_traces = 1;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    private static long GetScopeCount(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_scopes;";
        return (long)command.ExecuteScalar()!;
    }

    private static string GetDatabasePath(string workspacePath) => Path.Combine(workspacePath, "dashboard.db");

    private static SqliteTelemetryRepository CreateRepository(string workspacePath, bool readOnly = false, int? maxTraceCount = null)
    {
        var options = new DashboardOptions();
        options.TelemetryLimits.MaxTraceCount = maxTraceCount ?? options.TelemetryLimits.MaxTraceCount;
        return new SqliteTelemetryRepository(
            GetDatabasePath(workspacePath),
            NullLoggerFactory.Instance,
            Options.Create(options),
            new PauseManager(),
            [],
            readOnly);
    }

    private static GetLogsContext CreateLogsContext()
    {
        return new GetLogsContext
        {
            ResourceKeys = [],
            StartIndex = 0,
            Count = 10,
            Filters = []
        };
    }
}