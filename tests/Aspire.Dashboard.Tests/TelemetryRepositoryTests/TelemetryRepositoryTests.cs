// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public class TelemetryRepositoryTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AddData_WhilePaused_IsDiscarded()
    {
        // Arrange
        var pauseManager = new PauseManager();
        var repository = CreateRepository(pauseManager: pauseManager);
        using var subscription = repository.OnNewLogs(resourceKey: null, SubscriptionType.Other, () => Task.CompletedTask);

        // Act and assert
        pauseManager.SetStructuredLogsPaused(true);
        pauseManager.SetMetricsPaused(true);
        pauseManager.SetTracesPaused(true);
        AddLog();
        AddMetric();
        AddTrace();

        var resourceKey = new ResourceKey("resource", "resource");
        Assert.Empty(repository.GetLogs(new GetLogsContext { ResourceKey = resourceKey, Count = 100, Filters = [], StartIndex = 0 }).Items);
        Assert.Null(repository.GetResource(resourceKey));
        Assert.Empty(repository.GetTraces(new GetTracesRequest { ResourceKey = resourceKey, Count = 100, Filters = [], StartIndex = 0, FilterText = string.Empty }).PagedResult.Items);

        pauseManager.SetStructuredLogsPaused(false);
        pauseManager.SetMetricsPaused(false);
        pauseManager.SetTracesPaused(false);

        AddLog();
        AddMetric();
        AddTrace();
        Assert.Single(repository.GetLogs(new GetLogsContext { ResourceKey = resourceKey, Count = 100, Filters = [], StartIndex = 0 }).Items);
        var resource = repository.GetResource(resourceKey);
        Assert.NotNull(resource);
        Assert.NotEmpty(resource.GetInstrumentsSummary());
        Assert.Single(repository.GetTraces(new GetTracesRequest { ResourceKey = resourceKey, Count = 100, Filters = [], StartIndex = 0, FilterText = string.Empty }).PagedResult.Items);

        void AddLog()
        {
            var addContext = new AddContext();
            repository.AddLogs(addContext, new RepeatedField<ResourceLogs>()
            {
                new ResourceLogs
                {
                    Resource = CreateResource(name: "resource", instanceId: "resource"),
                    ScopeLogs =
                    {
                        new ScopeLogs
                        {
                            Scope = CreateScope("TestLogger"),
                            LogRecords =
                            {
                                CreateLogRecord(time: DateTime.Now, message: "1", severity: SeverityNumber.Error),
                            }
                        }
                    }
                }
            });
        }

        void AddMetric()
        {
            var addContext = new AddContext();
            repository.AddMetrics(addContext, new RepeatedField<ResourceMetrics>()
            {
                new ResourceMetrics
                {
                    Resource = CreateResource("resource", instanceId: "resource"),
                    ScopeMetrics =
                    {
                        new ScopeMetrics
                        {
                            Scope = CreateScope(name: "test-meter"),
                            Metrics =
                            {
                                CreateSumMetric(metricName: "test", startTime: DateTime.Now.AddMinutes(1)),
                                CreateSumMetric(metricName: "test", startTime: DateTime.Now.AddMinutes(2)),
                                CreateSumMetric(metricName: "test2", startTime: DateTime.Now.AddMinutes(1)),
                            }
                        },
                        new ScopeMetrics
                        {
                            Scope = CreateScope(name: "test-meter2"),
                            Metrics =
                            {
                                CreateSumMetric(metricName: "test", startTime: DateTime.Now.AddMinutes(1)),
                                CreateHistogramMetric(metricName: "test2", startTime: DateTime.Now.AddMinutes(1))
                            }
                        }
                    }
                }
            });
        }

        void AddTrace()
        {
            var addContext = new AddContext();
            repository.AddTraces(addContext, new RepeatedField<ResourceSpans>()
            {
                new ResourceSpans
                {
                    Resource = CreateResource("resource", instanceId: "resource"),
                    ScopeSpans =
                    {
                        new ScopeSpans
                        {
                            Scope = CreateScope(),
                            Spans =
                            {
                                CreateSpan(traceId: "1", spanId: "1-1", startTime: DateTime.Now.AddMinutes(1), endTime: DateTime.Now.AddMinutes(10)),
                                CreateSpan(traceId: "1", spanId: "1-2", startTime: DateTime.Now.AddMinutes(5), endTime: DateTime.Now.AddMinutes(10), parentSpanId: "1-1")
                            }
                        }
                    }
                }
            });
        }
    }

    [Fact]
    public void Subscription_MultipleDisposes_UnsubscribeOnce()
    {
        // Arrange
        var telemetryRepository = CreateRepository();
        var unsubscribeCallCount = 0;

        var subscription = new Subscription(
            name: "Test",
            resourceKey: null,
            subscriptionType: SubscriptionType.Read,
            callback: () => Task.CompletedTask,
            unsubscribe: () => unsubscribeCallCount++,
            executionContext: null,
            telemetryRepository: telemetryRepository);

        // Act
        subscription.Dispose();
        subscription.Dispose();

        // Assert
        Assert.Equal(1, unsubscribeCallCount);
    }

    [Fact]
    public async Task Subscription_ExecuteAfterDispose_LogWithNoExecute()
    {
        // Arrange
        var tcs = new TaskCompletionSource<WriteContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var testSink = new TestSink();
        testSink.MessageLogged += (write) =>
        {
            if (write.Message == "Callback 'Test' has been disposed.")
            {
                tcs.TrySetResult(write);
            }
        };
        var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(new TestLoggerProvider(testSink));
            b.SetMinimumLevel(LogLevel.Trace);
        });

        var telemetryRepository = CreateRepository(loggerFactory: factory);

        var subscription = new Subscription(
            name: "Test",
            resourceKey: null,
            subscriptionType: SubscriptionType.Read,
            callback: () => Task.CompletedTask,
            unsubscribe: () => { },
            executionContext: null,
            telemetryRepository: telemetryRepository);

        subscription.Dispose();

        // Act
        subscription.Execute();

        // Assert
        await tcs.Task.DefaultTimeout();
    }

    [Fact]
    public void ClearSelectedSignals_ClearsSelectedDataTypes_ForSpecificResources()
    {
        // Arrange
        var repository = CreateRepository();

        AddTestData(repository, "resource1", "123");
        AddTestData(repository, "resource2", "456");

        // Verify unviewed error logs exist before clearing
        var unviewedBefore = repository.GetResourceUnviewedErrorLogsCount();
        Assert.True(unviewedBefore.TryGetValue(new ResourceKey("resource1", "123"), out var errorCount1));
        Assert.Equal(1, errorCount1);
        Assert.True(unviewedBefore.TryGetValue(new ResourceKey("resource2", "456"), out var errorCount2));
        Assert.Equal(1, errorCount2);

        // Act - Clear only structured logs for resource1
        var selectedResources = new Dictionary<string, HashSet<AspireDataType>>
        {
            ["resource1-123"] = [AspireDataType.StructuredLogs]
        };
        repository.ClearSelectedSignals(selectedResources);

        // Assert - resource1 unviewed error logs cleared
        var unviewedAfter = repository.GetResourceUnviewedErrorLogsCount();
        Assert.False(unviewedAfter.TryGetValue(new ResourceKey("resource1", "123"), out _));
        Assert.True(unviewedAfter.TryGetValue(new ResourceKey("resource2", "456"), out errorCount2));
        Assert.Equal(1, errorCount2);

        // Assert - resource1 logs cleared, but traces and metrics remain
        var logs = repository.GetLogs(new GetLogsContext { ResourceKey = null, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Single(logs.Items);
        Assert.Equal("log-resource2-456", logs.Items[0].Message);

        var traces = repository.GetTraces(new GetTracesRequest { ResourceKey = null, FilterText = string.Empty, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Equal(2, traces.PagedResult.TotalItemCount);

        var resource1Metrics = repository.GetInstrumentsSummaries(new ResourceKey("resource1", "123"));
        Assert.Single(resource1Metrics);

        // Assert - resource2 data is unaffected
        var resource2Key = new ResourceKey("resource2", "456");
        var resource2Logs = repository.GetLogs(new GetLogsContext { ResourceKey = resource2Key, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Single(resource2Logs.Items);
        Assert.Equal("log-resource2-456", resource2Logs.Items[0].Message);

        var resource2Traces = repository.GetTraces(new GetTracesRequest { ResourceKey = resource2Key, FilterText = string.Empty, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Single(resource2Traces.PagedResult.Items);

        var resource2Metrics = repository.GetInstrumentsSummaries(new ResourceKey("resource2", "456"));
        Assert.Single(resource2Metrics);
    }

    [Fact]
    public void ClearSelectedSignals_OtherResourcesRemainUnaffected()
    {
        // Arrange
        var repository = CreateRepository();

        AddTestData(repository, "resource1", "111");
        AddTestData(repository, "resource2", "222");
        AddTestData(repository, "resource3", "333");

        // Act - Clear all data types for resource2 only
        var selectedResources = new Dictionary<string, HashSet<AspireDataType>>
        {
            ["resource2-222"] = [AspireDataType.StructuredLogs, AspireDataType.Traces, AspireDataType.Metrics, AspireDataType.Resource]
        };
        repository.ClearSelectedSignals(selectedResources);

        // Assert - resource1 and resource3 data is unaffected
        var logs = repository.GetLogs(new GetLogsContext { ResourceKey = null, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Equal(2, logs.TotalItemCount);
        Assert.Contains(logs.Items, l => l.Message == "log-resource1-111");
        Assert.Contains(logs.Items, l => l.Message == "log-resource3-333");
        Assert.DoesNotContain(logs.Items, l => l.Message == "log-resource2-222");

        var traces = repository.GetTraces(new GetTracesRequest { ResourceKey = null, FilterText = string.Empty, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Equal(2, traces.PagedResult.TotalItemCount);

        var resource1Metrics = repository.GetInstrumentsSummaries(new ResourceKey("resource1", "111"));
        Assert.Single(resource1Metrics);

        var resource3Metrics = repository.GetInstrumentsSummaries(new ResourceKey("resource3", "333"));
        Assert.Single(resource3Metrics);

        // Assert - resource2 is removed from the repository since all data types were cleared
        var resource2 = repository.GetResource(new ResourceKey("resource2", "222"));
        Assert.Null(resource2);
    }

    [Fact]
    public void ClearSelectedSignals_ResourceRemovedWhenAllDataTypesCleared()
    {
        // Arrange
        var repository = CreateRepository();

        AddTestData(repository, "resource1", "123");

        // Verify resource exists before clearing
        var resourceBefore = repository.GetResource(new ResourceKey("resource1", "123"));
        Assert.NotNull(resourceBefore);

        // Act - Clear all data types for resource1
        var selectedResources = new Dictionary<string, HashSet<AspireDataType>>
        {
            ["resource1-123"] = [AspireDataType.StructuredLogs, AspireDataType.Traces, AspireDataType.Metrics, AspireDataType.Resource]
        };
        repository.ClearSelectedSignals(selectedResources);

        // Assert - Resource is removed from the repository
        var resourceAfter = repository.GetResource(new ResourceKey("resource1", "123"));
        Assert.Null(resourceAfter);

        // Assert - All telemetry data is cleared
        var logs = repository.GetLogs(new GetLogsContext { ResourceKey = null, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Empty(logs.Items);

        var traces = repository.GetTraces(new GetTracesRequest { ResourceKey = null, FilterText = string.Empty, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Empty(traces.PagedResult.Items);

        // Assert - Resources list is empty
        var resources = repository.GetResources();
        Assert.Empty(resources);
    }

    [Fact]
    public void ClearSelectedSignals_PartialClear_ResourceNotRemoved()
    {
        // Arrange
        var repository = CreateRepository();

        AddTestData(repository, "resource1", "123");

        // Act - Clear only logs and traces for resource1 (not metrics)
        var selectedResources = new Dictionary<string, HashSet<AspireDataType>>
        {
            ["resource1-123"] = [AspireDataType.StructuredLogs, AspireDataType.Traces]
        };
        repository.ClearSelectedSignals(selectedResources);

        // Assert - Resource still exists because not all data types were cleared
        var resourceAfter = repository.GetResource(new ResourceKey("resource1", "123"));
        Assert.NotNull(resourceAfter);

        // Assert - Logs and traces are cleared, but metrics remain
        var logs = repository.GetLogs(new GetLogsContext { ResourceKey = null, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Empty(logs.Items);

        var traces = repository.GetTraces(new GetTracesRequest { ResourceKey = null, FilterText = string.Empty, StartIndex = 0, Count = 10, Filters = [] });
        Assert.Empty(traces.PagedResult.Items);

        var metrics = repository.GetInstrumentsSummaries(new ResourceKey("resource1", "123"));
        Assert.Single(metrics);
    }

    #region Watcher Tests

    [Fact]
    public async Task WatchSpansAsync_ReturnsExistingSpans_ThenNewSpans()
    {
        // Arrange
        var repository = CreateRepository();

        // Add initial span
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
                            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedSpans = new List<OtlpSpan>();
        var firstSpanReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var watchTask = Task.Run(async () =>
        {
            await foreach (var span in repository.WatchSpansAsync(resourceKey: null, cts.Token))
            {
                receivedSpans.Add(span);
                if (receivedSpans.Count == 1)
                {
                    firstSpanReceived.TrySetResult();
                }
                if (receivedSpans.Count >= 2)
                {
                    break;
                }
            }
        });

        // Wait for initial span to be received
        await firstSpanReceived.Task;

        // Add another span while watching
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
                            CreateSpan(traceId: "trace2", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3))
                        }
                    }
                }
            }
        });

        // Wait for task to complete
        await watchTask;

        // Assert
        Assert.Equal(2, receivedSpans.Count);
        // SpanId is stored as UTF-8 bytes that get hex-encoded when read back
        Assert.Contains("span1", receivedSpans[0].Name);
        Assert.Contains("span2", receivedSpans[1].Name);
    }

    [Fact]
    public async Task WatchSpansAsync_CanBeCancelled()
    {
        // Arrange
        var repository = CreateRepository();
        using var cts = new CancellationTokenSource();
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var watchTask = Task.Run(async () =>
        {
            var count = 0;
            watchStarted.TrySetResult();
            try
            {
                await foreach (var span in repository.WatchSpansAsync(resourceKey: null, cts.Token))
                {
                    count++;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            return count;
        });

        // Wait for watcher to start
        await watchStarted.Task;

        // Cancel the watch
        cts.Cancel();

        // Assert - task should complete
        await watchTask;
        Assert.True(watchTask.IsCompleted);
    }

    [Fact]
    public async Task WatchLogsAsync_ReturnsExistingLogs_ThenNewLogs()
    {
        // Arrange
        var repository = CreateRepository();

        // Add initial log
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime, message: "log1", severity: SeverityNumber.Info)
                        }
                    }
                }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedLogs = new List<OtlpLogEntry>();
        var firstLogReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var watchTask = Task.Run(async () =>
        {
            await foreach (var log in repository.WatchLogsAsync(resourceKey: null, filters: null, cts.Token))
            {
                receivedLogs.Add(log);
                if (receivedLogs.Count == 1)
                {
                    firstLogReceived.TrySetResult();
                }
                if (receivedLogs.Count >= 2)
                {
                    break;
                }
            }
        });

        // Wait for initial log to be received
        await firstLogReceived.Task;

        // Add another log while watching
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddMinutes(1), message: "log2", severity: SeverityNumber.Info)
                        }
                    }
                }
            }
        });

        // Wait for task to complete
        await watchTask;

        // Assert
        Assert.Equal(2, receivedLogs.Count);
        Assert.Equal("log1", receivedLogs[0].Message);
        Assert.Equal("log2", receivedLogs[1].Message);
    }

    [Fact]
    public async Task WatchLogsAsync_CanBeCancelled()
    {
        // Arrange
        var repository = CreateRepository();
        using var cts = new CancellationTokenSource();
        var watchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Act
        var watchTask = Task.Run(async () =>
        {
            var count = 0;
            watchStarted.TrySetResult();
            try
            {
                await foreach (var log in repository.WatchLogsAsync(resourceKey: null, filters: null, cts.Token))
                {
                    count++;
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            return count;
        });

        // Wait for watcher to start
        await watchStarted.Task;

        // Cancel the watch
        cts.Cancel();

        // Assert - task should complete
        await watchTask;
        Assert.True(watchTask.IsCompleted);
    }

    [Fact]
    public async Task WatchSpansAsync_ReturnsExistingSpans_OrderedByStartTime()
    {
        // Arrange
        var repository = CreateRepository();

        // Add spans with non-chronological start times across different traces
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
                            // Span with latest start time added first
                            CreateSpan(traceId: "trace1", spanId: "span-late", startTime: s_testTime.AddMinutes(10), endTime: s_testTime.AddMinutes(11))
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
                            // Span with earliest start time added second
                            CreateSpan(traceId: "trace2", spanId: "span-early", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(2))
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
                            // Span with middle start time added last
                            CreateSpan(traceId: "trace3", spanId: "span-mid", startTime: s_testTime.AddMinutes(5), endTime: s_testTime.AddMinutes(6))
                        }
                    }
                }
            }
        });

        const int expectedSpans = 3;

        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        using var doneCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, doneCts.Token);
        var receivedSpans = new List<OtlpSpan>();

        // Act
        try
        {
            await foreach (var span in repository.WatchSpansAsync(resourceKey: null, linkedCts.Token))
            {
                receivedSpans.Add(span);
                if (receivedSpans.Count == expectedSpans)
                {
                    doneCts.Cancel();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when all spans received
        }

        // Assert - spans should be ordered by start time regardless of insertion order
        Assert.Equal(expectedSpans, receivedSpans.Count);
        Assert.Equal("Test span. Id: span-early", receivedSpans[0].Name);
        Assert.Equal("Test span. Id: span-mid", receivedSpans[1].Name);
        Assert.Equal("Test span. Id: span-late", receivedSpans[2].Name);
    }

    [Fact]
    public async Task WatchSpansAsync_FiltersById_WhenResourceKeyProvided()
    {
        // Arrange
        var repository = CreateRepository();

        // Add spans for two different resources
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
                            CreateSpan(traceId: "trace1", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            },
            new ResourceSpans
            {
                Resource = CreateResource(name: "service2", instanceId: "inst2"),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: "trace2", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3))
                        }
                    }
                }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var receivedSpans = new List<OtlpSpan>();

        // Act - Watch only service1
        try
        {
            await foreach (var span in repository.WatchSpansAsync(new ResourceKey("service1", "inst1"), cts.Token))
            {
                receivedSpans.Add(span);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - should only receive span from service1
        Assert.Single(receivedSpans);
        Assert.Contains("span1", receivedSpans[0].Name);
    }

    [Fact]
    public async Task WatchLogsAsync_FiltersAppliedWhenPushing()
    {
        // Arrange
        var repository = CreateRepository();

        // Create a filter that matches only logs containing "match"
        var filters = new List<TelemetryFilter>
        {
            new FieldTelemetryFilter
            {
                Field = nameof(OtlpLogEntry.Message),
                Value = "match",
                Condition = FilterCondition.Contains
            }
        };

        // Add an initial matching log so we know when watcher is ready
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime, message: "initial match log", severity: SeverityNumber.Info)
                        }
                    }
                }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedLogs = new List<OtlpLogEntry>();
        var firstLogReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start watching with filter
        var watchTask = Task.Run(async () =>
        {
            await foreach (var log in repository.WatchLogsAsync(resourceKey: null, filters: filters, cts.Token))
            {
                receivedLogs.Add(log);
                if (receivedLogs.Count == 1)
                {
                    firstLogReceived.TrySetResult();
                }
                // Stop after receiving 2 logs (initial + pushed matching log)
                if (receivedLogs.Count >= 2)
                {
                    break;
                }
            }
        });

        // Wait for initial log to be received (proves watcher is registered)
        await firstLogReceived.Task;

        // Add more logs - one matches filter, one doesn't
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddSeconds(1), message: "this should match filter", severity: SeverityNumber.Info),
                            CreateLogRecord(time: s_testTime.AddSeconds(2), message: "this should not pass", severity: SeverityNumber.Info)
                        }
                    }
                }
            }
        });

        // Wait for task to complete
        await watchTask;

        // Assert - only the matching logs should be received (initial + pushed match)
        Assert.Equal(2, receivedLogs.Count);
        Assert.All(receivedLogs, l => Assert.Contains("match", l.Message));
    }

    [Fact]
    public async Task WatchLogsAsync_SeverityFilterApplied()
    {
        // Arrange
        var repository = CreateRepository();

        // Create a filter for Error and above
        var filters = new List<TelemetryFilter>
        {
            new FieldTelemetryFilter
            {
                Field = nameof(OtlpLogEntry.Severity),
                Value = LogLevel.Error.ToString(),
                Condition = FilterCondition.GreaterThanOrEqual
            }
        };

        // Add an initial error log so we know when watcher is ready
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime, message: "initial error", severity: SeverityNumber.Error)
                        }
                    }
                }
            }
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receivedLogs = new List<OtlpLogEntry>();
        var firstLogReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start watching with severity filter
        var watchTask = Task.Run(async () =>
        {
            await foreach (var log in repository.WatchLogsAsync(resourceKey: null, filters: filters, cts.Token))
            {
                receivedLogs.Add(log);
                if (receivedLogs.Count == 1)
                {
                    firstLogReceived.TrySetResult();
                }
                // Stop after receiving 3 logs (initial + 2 pushed matching logs)
                if (receivedLogs.Count >= 3)
                {
                    break;
                }
            }
        });

        // Wait for initial log to be received (proves watcher is registered)
        await firstLogReceived.Task;

        // Add logs with different severity levels
        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: "service1", instanceId: "inst1"),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords =
                        {
                            CreateLogRecord(time: s_testTime.AddSeconds(1), message: "info log", severity: SeverityNumber.Info),
                            CreateLogRecord(time: s_testTime.AddSeconds(2), message: "error log", severity: SeverityNumber.Error),
                            CreateLogRecord(time: s_testTime.AddSeconds(3), message: "critical log", severity: SeverityNumber.Fatal)
                        }
                    }
                }
            }
        });

        // Wait for task to complete
        await watchTask;

        // Assert - only Error and Critical logs should be received (initial + 2 pushed)
        Assert.Equal(3, receivedLogs.Count);
        Assert.Contains(receivedLogs, l => l.Message == "initial error");
        Assert.Contains(receivedLogs, l => l.Message == "error log");
        Assert.Contains(receivedLogs, l => l.Message == "critical log");
    }

    #endregion

    private static void AddTestData(TelemetryRepository repository, string resourceName, string instanceId)
    {
        var compositeName = $"{resourceName}-{instanceId}";

        repository.AddLogs(new AddContext(), new RepeatedField<ResourceLogs>()
        {
            new ResourceLogs
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeLogs =
                {
                    new ScopeLogs
                    {
                        Scope = CreateScope("TestLogger"),
                        LogRecords = { CreateLogRecord(time: s_testTime.AddMinutes(1), message: $"log-{compositeName}", severity: SeverityNumber.Error) }
                    }
                }
            }
        });

        repository.AddTraces(new AddContext(), new RepeatedField<ResourceSpans>()
        {
            new ResourceSpans
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeSpans =
                {
                    new ScopeSpans
                    {
                        Scope = CreateScope(),
                        Spans =
                        {
                            CreateSpan(traceId: compositeName, spanId: $"{compositeName}-1", startTime: s_testTime.AddMinutes(1), endTime: s_testTime.AddMinutes(10))
                        }
                    }
                }
            }
        });

        repository.AddMetrics(new AddContext(), new RepeatedField<ResourceMetrics>()
        {
            new ResourceMetrics
            {
                Resource = CreateResource(name: resourceName, instanceId: instanceId),
                ScopeMetrics =
                {
                    new ScopeMetrics
                    {
                        Scope = CreateScope(name: "test-meter"),
                        Metrics =
                        {
                            CreateSumMetric(metricName: $"metric-{compositeName}", value: 1, startTime: s_testTime.AddMinutes(1))
                        }
                    }
                }
            }
        });
    }
}
