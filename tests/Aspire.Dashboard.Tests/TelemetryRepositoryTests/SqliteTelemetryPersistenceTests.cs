// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
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

public sealed class SqliteTelemetryPersistenceTests : IDisposable
{
    private readonly string _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-telemetry-persistence-tests-").FullName;

    [Fact]
    public void Logs_ReopenFromNormalizedRowsWithStableIds()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "dashboard.db");
        long logId;
        using (var repository = CreateRepository(databasePath))
        {
            repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
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

        using (var historicalRepository = CreateRepository(databasePath, readOnly: true))
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
    public void Traces_ReopenFromNormalizedRowsWithStableEventIds()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "dashboard.db");
        var startTime = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var link = new Span.Types.Link
        {
            TraceId = ByteString.CopyFromUtf8("2"),
            SpanId = ByteString.CopyFromUtf8("2-1"),
            TraceState = "state"
        };
        Guid eventId;
        using (var repository = CreateRepository(databasePath))
        {
            repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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

        using (var historicalRepository = CreateRepository(databasePath, readOnly: true))
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
    public void Metrics_ReopenFromNormalizedRows()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "dashboard.db");
        var startTime = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        using (var repository = CreateRepository(databasePath))
        {
            repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
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

        using (var historicalRepository = CreateRepository(databasePath, readOnly: true))
        {
            var resourceKey = new ResourceKey("TestService", "TestId");
            var summary = Assert.Single(historicalRepository.GetInstrumentsSummaries(resourceKey));
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
            Assert.Equal(42, Assert.IsType<MetricValue<long>>(Assert.Single(dimension.Values)).Value);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_metric_points;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Scopes_AreSharedAcrossLogsTracesAndMetrics()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "shared-scopes.db");
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var scope = CreateScope(name: "SharedScope", attributes: [KeyValuePair.Create("scope-key", "scope-value")]);
        using (var repository = CreateRepository(databasePath))
        {
            repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
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
            repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
            repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
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
    public void ResourceViews_EquivalentAttributesShareNormalizedRows()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "resource-views.db");
        using (var repository = CreateRepository(databasePath))
        {
            var addContext = new AddContext();
            repository.AddLogs(addContext, new RepeatedField<ResourceLogs>
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
        Assert.Equal(1L, command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM telemetry_resource_view_attributes;";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public void ResourceViews_LimitRejectsNewNormalizedRow()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "resource-view-limit.db");
        using var repository = CreateRepository(databasePath);
        var addContext = new AddContext();
        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>
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

        repository.AddLogs(addContext, new RepeatedField<ResourceLogs>
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
    public void Scopes_AreDeletedAfterTheirFinalOwnerIsCleared()
    {
        var databasePath = Path.Combine(_temporaryDirectory, "scope-cleanup.db");
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var scope = CreateScope("SharedScope");
        using var repository = CreateRepository(databasePath);
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs = { new ScopeLogs { Scope = scope, LogRecords = { CreateLogRecord() } } }
            }
        });
        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>
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
        repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>
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

        repository.ClearStructuredLogs();
        Assert.Equal(1L, GetScopeCount(databasePath));
        repository.ClearTraces();
        Assert.Equal(1L, GetScopeCount(databasePath));
        repository.ClearMetrics();
        Assert.Equal(0L, GetScopeCount(databasePath));
    }

    private static long GetScopeCount(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM telemetry_scopes;";
        return (long)command.ExecuteScalar()!;
    }

    private static SqliteTelemetryRepository CreateRepository(string databasePath, bool readOnly = false)
    {
        return new SqliteTelemetryRepository(
            databasePath,
            NullLoggerFactory.Instance,
            Options.Create(new DashboardOptions()),
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

    public void Dispose()
    {
        Directory.Delete(_temporaryDirectory, recursive: true);
    }
}