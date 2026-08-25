// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests;
using Aspire.Tests.Shared.DashboardModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Data.Sqlite;
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
    public async Task RunReadAsync_CancellationInterruptsLongRunningQuery()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
        using var cancellationSource = new CancellationTokenSource();
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var queryTask = SqliteTelemetryRepository.RunReadAsync(token =>
        {
            using var connection = repositoryContext.Database.OpenConnection();
            using var interrupt = connection.RegisterInterrupt(token);
            // Signal from inside the statement so cancellation can't happen before SQLite starts executing it.
            // Return immediately so the cancellation interrupts SQLite's recursive query, not managed code.
            connection.CreateFunction("query_started", () =>
            {
                queryStarted.TrySetResult();
                return 1;
            });

            using var command = connection.CreateCommand();
            // This query is deliberately too large to complete within the test timeout without interruption.
            command.CommandText = """
                WITH RECURSIVE numbers(value) AS (
                    SELECT query_started()
                    UNION ALL
                    SELECT value + 1 FROM numbers WHERE value < 1000000000
                )
                SELECT SUM(value) FROM numbers;
                """;
            return command.ExecuteScalar();
        }, cancellationSource.Token);

        await queryStarted.Task.DefaultTimeout();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queryTask).DefaultTimeout();
    }

    [Fact]
    public async Task Cache_UsesCanonicalResourceViewAndScopeAcrossSignals()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var startTime = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        var resource = CreateResource(attributes: [KeyValuePair.Create("resource-key", "resource-value")]);
        var scope = CreateScope(name: "SharedScope", attributes: [KeyValuePair.Create("scope-key", "scope-value")]);
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path);

        await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = resource,
                ScopeLogs = { new ScopeLogs { Scope = scope, LogRecords = { CreateLogRecord() } } }
            }
        });
        await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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
        await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = resource,
                ScopeMetrics = { new ScopeMetrics { Scope = scope, Metrics = { CreateSumMetric("requests", startTime) } } }
            }
        });

        var cachedResource = Assert.Single(repositoryContext.Repository.GetResources());
        var log = Assert.Single((await repositoryContext.Repository.GetLogsAsync(CreateLogsContext(), cancellationToken: CancellationToken.None)).Items);
        var span = Assert.Single(Assert.Single((await repositoryContext.Repository.GetTracesAsync(GetTracesRequest.ForResourceKey(cachedResource.ResourceKey), cancellationToken: CancellationToken.None)).PagedResult.Items).Spans);
        var instrument = Assert.Single(repositoryContext.Repository.GetInstrumentSummaries(cachedResource.ResourceKey));

        Assert.Same(cachedResource, log.ResourceView.Resource);
        Assert.Same(cachedResource, span.Source.Resource);
        Assert.Same(log.ResourceView, span.Source);
        Assert.Same(log.Scope, span.Scope);
        Assert.Same(log.Scope, instrument.Parent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstrumentedPeer_RemainsInstrumented(bool resolvePeerDuringIngestion)
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var resolvePeer = resolvePeerDuringIngestion;
        var backend = ModelTestHelpers.CreateResource(resourceName: "backend-TestId", displayName: "backend");
        using var outgoingPeerResolver = new TestOutgoingPeerResolver(onResolve: _ => resolvePeer ? (backend.Name, backend) : (null, null));
        using (var repositoryContext = await CreateRepositoryAsync(workspace.Path, outgoingPeerResolvers: [outgoingPeerResolver]))
        {
            await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(name: "backend", instanceId: "TestId"),
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope(), LogRecords = { CreateLogRecord() } } }
                }
            });
            await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
            {
                new ResourceSpans
                {
                    Resource = CreateResource(name: "frontend", instanceId: "TestId"),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope(),
                            Spans =
                            {
                                CreateSpan(
                                    traceId: "peer-trace",
                                    spanId: "peer-span",
                                    startTime: DateTime.UnixEpoch,
                                    endTime: DateTime.UnixEpoch.AddSeconds(1),
                                    attributes: [KeyValuePair.Create(OtlpSpan.PeerServiceAttributeKey, "backend")],
                                    kind: Span.Types.SpanKind.Client)
                            }
                        }
                    }
                }
            });

            if (!resolvePeerDuringIngestion)
            {
                resolvePeer = true;
                await outgoingPeerResolver.InvokePeerChanges();
            }
        }

        using var reopenedContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
        var backendResource = Assert.IsType<OtlpResource>(
            reopenedContext.Repository.GetResource(new ResourceKey("backend", "TestId")));
        Assert.False(backendResource.UninstrumentedPeer);

        var span = Assert.Single(reopenedContext.Repository.GetTrace(GetHexId("peer-trace"))!.Spans);
        Assert.Same(backendResource, span.UninstrumentedPeer);
    }

    [Fact]
    public async Task Cache_HydratesPersistedMetadataOnce()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var startTime = new DateTime(2025, 4, 5, 6, 7, 8, DateTimeKind.Utc);
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
            {
                new ResourceLogs
                {
                    Resource = CreateResource(attributes: [KeyValuePair.Create("resource-key", "resource-value")]),
                    ScopeLogs = { new ScopeLogs { Scope = CreateScope("TestScope"), LogRecords = { CreateLogRecord() } } }
                }
            });
            await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(),
                    ScopeMetrics = { new ScopeMetrics { Scope = CreateScope("TestScope"), Metrics = { CreateSumMetric("requests", startTime) } } }
                }
            });
        }

        using var historicalContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
        var activities = new ConcurrentQueue<Activity>();
        using var listener = ActivityListenerHelper.Create(historicalContext.Repository.SqlActivitySource, onActivityStopped: activities.Enqueue);
        using var parent = new Activity("cache hydration test").Start();
        var firstResource = Assert.Single(historicalContext.Repository.GetResources());
        Assert.NotEmpty(activities);
        activities.Clear();

        var secondResource = Assert.Single(historicalContext.Repository.GetResources());
        var summary = Assert.Single(historicalContext.Repository.GetInstrumentSummaries(firstResource.ResourceKey));
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
            {
                new ResourceMetrics
                {
                    Resource = CreateResource(attributes: [KeyValuePair.Create("resource-key", "resource-value")]),
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

        using var reopenedContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
        var resource = Assert.Single(reopenedContext.Repository.GetResources());
        var views = resource.GetViews().OrderBy(view => view.Properties.Length).ToArray();
        var instrument = Assert.Single(reopenedContext.Repository.GetInstrumentSummaries(resource.ResourceKey));

        Assert.Collection(views,
            view => Assert.Empty(view.Properties),
            view => Assert.Equal(KeyValuePair.Create("resource-key", "resource-value"), Assert.Single(view.Properties)));
        Assert.Same(views[1], instrument.ResourceView);
    }

    [Fact]
    public async Task Logs_ReopenFromNormalizedRowsWithStableIds()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        long logId;
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
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

            var log = Assert.Single((await repositoryContext.Repository.GetLogsAsync(CreateLogsContext(), cancellationToken: CancellationToken.None)).Items);
            logId = log.InternalId;
        }

        {
            using var historicalContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
            var log = Assert.Single((await historicalContext.Repository.GetLogsAsync(CreateLogsContext(), cancellationToken: CancellationToken.None)).Items);
            Assert.Equal(logId, log.InternalId);
            Assert.Equal("Test Value!", log.Message);
            Assert.Equal("TestLogger", log.Scope.Name);
            Assert.Equal(logId, historicalContext.Repository.GetLog(logId)!.InternalId);
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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

            var trace = Assert.Single((await repositoryContext.Repository.GetTracesAsync(GetTracesRequest.ForResourceKey(new ResourceKey("TestService", "TestId")), cancellationToken: CancellationToken.None)).PagedResult.Items);
            eventId = Assert.Single(trace.FirstSpan.Events).InternalId;
        }

        {
            using var historicalContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
            var trace = Assert.Single((await historicalContext.Repository.GetTracesAsync(GetTracesRequest.ForResourceKey(new ResourceKey("TestService", "TestId")), cancellationToken: CancellationToken.None)).PagedResult.Items);
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
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

        {
            using var historicalContext = await CreateRepositoryAsync(workspace.Path, readOnly: true);
            var resourceKey = new ResourceKey("TestService", "TestId");
            var summary = Assert.Single(historicalContext.Repository.GetInstrumentSummaries(resourceKey));
            Assert.Equal("requests", summary.Name);
            Assert.Equal("TestMeter", summary.Parent.Name);

            var instrument = await historicalContext.Repository.GetInstrumentAsync(new GetInstrumentRequest
            {
                ResourceKey = resourceKey,
                MeterName = "TestMeter",
                InstrumentName = "requests",
                StartTime = startTime.AddMinutes(-1),
                EndTime = startTime.AddMinutes(1)
            }, cancellationToken: CancellationToken.None);
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            var histogram = CreateHistogramMetric("histogram", startTime);
            histogram.Histogram.DataPoints[0].ExplicitBounds.Clear();
            histogram.Histogram.DataPoints[0].ExplicitBounds.Add([1, 2]);
            var addContext = new AddContext();
            await repositoryContext.Repository.AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
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

        using var reopenedContext = await CreateRepositoryAsync(workspace.Path);
        var changedHistogram = CreateHistogramMetric("histogram", startTime.AddMinutes(1));
        changedHistogram.Histogram.DataPoints[0].BucketCounts.Add(4);
        var changedContext = new AddContext();
        await reopenedContext.Repository.AddMetricsAsync(changedContext, new RepeatedField<ResourceMetrics>
        {
            new ResourceMetrics
            {
                Resource = CreateResource(),
                ScopeMetrics = { new ScopeMetrics { Scope = CreateScope("TestMeter"), Metrics = { changedHistogram } } }
            }
        });

        Assert.Equal(0, changedContext.SuccessCount);
        Assert.Equal(1, changedContext.FailureCount);
        var instrument = await reopenedContext.Repository.GetInstrumentAsync(new GetInstrumentRequest
        {
            ResourceKey = new ResourceKey("TestService", "TestId"),
            MeterName = "TestMeter",
            InstrumentName = "histogram",
            StartTime = DateTime.MinValue,
            EndTime = DateTime.MaxValue
        }, cancellationToken: CancellationToken.None);
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            var addContext = new AddContext();
            foreach (var attributes in new[]
            {
                new[] { KeyValuePair.Create("second", "2"), KeyValuePair.Create("first", "1") },
                new[] { KeyValuePair.Create("first", "1"), KeyValuePair.Create("second", "2") },
                new[] { KeyValuePair.Create("first", "different") }
            })
            {
                await repositoryContext.Repository.AddMetricsAsync(addContext, new RepeatedField<ResourceMetrics>
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
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
            await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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
            await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
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
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            var addContext = new AddContext();
            await repositoryContext.Repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
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
        {
            using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
            var addContext = new AddContext();
            await repositoryContext.Repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
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
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
        var addContext = new AddContext();
            await repositoryContext.Repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
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

            await repositoryContext.Repository.AddLogsAsync(addContext, new RepeatedField<ResourceLogs>
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
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
        await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(),
                ScopeLogs = { new ScopeLogs { Scope = scope, LogRecords = { CreateLogRecord() } } }
            }
        });
        await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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
        await repositoryContext.Repository.AddMetricsAsync(new AddContext(), new RepeatedField<ResourceMetrics>
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

        await repositoryContext.Repository.ClearStructuredLogsAsync();
        Assert.Equal(1L, GetScopeCount(databasePath));
        await repositoryContext.Repository.ClearTracesAsync();
        Assert.Equal(1L, GetScopeCount(databasePath));
        await repositoryContext.Repository.ClearMetricsAsync();
        Assert.Equal(0L, GetScopeCount(databasePath));
    }

    [Fact]
    public async Task ResourcesAndResourceViews_AreRetainedAfterSignalsCleared()
    {
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var databasePath = GetDatabasePath(workspace.Path);
        var startTime = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path);
        await repositoryContext.Repository.AddLogsAsync(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(attributes: [KeyValuePair.Create("signal", "logs")]),
                ScopeLogs = { new ScopeLogs { Scope = CreateScope("Logger"), LogRecords = { CreateLogRecord() } } }
            }
        });
        await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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

        await repositoryContext.Repository.ClearStructuredLogsAsync();
        await repositoryContext.Repository.ClearTracesAsync();

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
        using var repositoryContext = await CreateRepositoryAsync(workspace.Path, maxTraceCount: 1);
        await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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
        await repositoryContext.Repository.AddTracesAsync(new AddContext(), new RepeatedField<ResourceSpans>
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

    private static async Task<SqliteRepositoryTestContext<SqliteTelemetryRepository>> CreateRepositoryAsync(
        string workspacePath,
        bool readOnly = false,
        int? maxTraceCount = null,
        IEnumerable<IOutgoingPeerResolver>? outgoingPeerResolvers = null)
    {
        var options = new DashboardOptions();
        options.TelemetryLimits.MaxTraceCount = maxTraceCount ?? options.TelemetryLimits.MaxTraceCount;
        var context = await SqliteRepositoryTestHelpers.CreateTelemetryRepositoryAsync(
            GetDatabasePath(workspacePath),
            readOnly,
            dashboardOptions: Options.Create(options),
            outgoingPeerResolvers: outgoingPeerResolvers);
        return context;
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