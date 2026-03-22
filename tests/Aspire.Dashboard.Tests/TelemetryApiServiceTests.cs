// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Api;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Xunit;
using static Aspire.Tests.Shared.Telemetry.TelemetryTestHelpers;

namespace Aspire.Dashboard.Tests;

public class TelemetryApiServiceTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task FollowSpansAsync_StreamsAllSpans()
    {
        // Arrange
        var repository = CreateRepository();

        // Add 5 spans
        for (var i = 1; i <= 5; i++)
        {
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
                                CreateSpan(traceId: $"trace{i}", spanId: $"span{i}", startTime: s_testTime.AddMinutes(i), endTime: s_testTime.AddMinutes(i + 1))
                            }
                        }
                    }
                }
            });
        }

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - stream spans
        var receivedItems = new List<string>();
        await foreach (var item in service.FollowSpansAsync(null, null, null, cts.Token))
        {
            receivedItems.Add(item);
            if (receivedItems.Count >= 5)
            {
                break;
            }
        }

        // Assert - should receive all 5 items
        Assert.Equal(5, receivedItems.Count);
    }

    [Fact]
    public async Task FollowLogsAsync_StreamsAllLogs()
    {
        // Arrange
        var repository = CreateRepository();

        // Add 5 logs
        for (var i = 1; i <= 5; i++)
        {
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
                                CreateLogRecord(time: s_testTime.AddMinutes(i), message: $"log{i}", severity: SeverityNumber.Info)
                            }
                        }
                    }
                }
            });
        }

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - stream logs
        var receivedItems = new List<string>();
        await foreach (var item in service.FollowLogsAsync(null, null, null, cts.Token))
        {
            receivedItems.Add(item);
            if (receivedItems.Count >= 5)
            {
                break;
            }
        }

        // Assert - should receive all 5 items
        Assert.Equal(5, receivedItems.Count);
    }

    [Fact]
    public void GetSpans_HasErrorFalse_ExcludesErrorSpans()
    {
        // Arrange
        var repository = CreateRepository();

        // Add spans - one with error, one without
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
                            CreateSpan(traceId: "trace1", spanId: "ok-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok }),
                            CreateSpan(traceId: "trace2", spanId: "error-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        });

        var service = CreateService(repository);

        // Act - get spans with hasError=false
        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: false, limit: null);

        // Assert - should only return the non-error span
        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
        
        // Serialize to check content
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("error-span", json);
        Assert.Contains("ok-span", json);
    }

    [Fact]
    public void GetSpans_HasErrorTrue_OnlyReturnsErrorSpans()
    {
        // Arrange
        var repository = CreateRepository();

        // Add spans - one with error, one without
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
                            CreateSpan(traceId: "trace1", spanId: "ok-span", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok }),
                            CreateSpan(traceId: "trace2", spanId: "error-span", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        });

        var service = CreateService(repository);

        // Act - get spans with hasError=true
        var result = service.GetSpans(resourceNames: null, traceId: null, hasError: true, limit: null);

        // Assert - should only return the error span
        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
        
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("error-span", json);
        Assert.DoesNotContain("ok-span", json);
    }

    [Fact]
    public void GetTraces_HasErrorFalse_ExcludesTracesWithErrors()
    {
        // Arrange
        var repository = CreateRepository();

        // Add two traces - one with error span, one without
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
                            CreateSpan(traceId: "ok-trace", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

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
                            CreateSpan(traceId: "error-trace", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        });

        var service = CreateService(repository);

        // Act - get traces with hasError=false (no error, should exclude the error trace)
        var result = service.GetTraces(resourceNames: null, hasError: false, limit: null);

        // Assert - should only return 1 trace (the one without errors)
        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
        
        // Verify with null filter returns both
        var allResult = service.GetTraces(resourceNames: null, hasError: null, limit: null);
        Assert.NotNull(allResult);
        Assert.Equal(2, allResult.ReturnedCount);
    }

    [Fact]
    public void GetTraces_HasErrorTrue_OnlyReturnsTracesWithErrors()
    {
        // Arrange
        var repository = CreateRepository();

        // Add two traces - one with error span, one without
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
                            CreateSpan(traceId: "ok-trace", spanId: "span1", startTime: s_testTime, endTime: s_testTime.AddMinutes(1), status: new Status { Code = Status.Types.StatusCode.Ok })
                        }
                    }
                }
            }
        });

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
                            CreateSpan(traceId: "error-trace", spanId: "span2", startTime: s_testTime.AddMinutes(2), endTime: s_testTime.AddMinutes(3), status: new Status { Code = Status.Types.StatusCode.Error })
                        }
                    }
                }
            }
        });

        var service = CreateService(repository);

        // Act - get traces with hasError=true (error only)
        var result = service.GetTraces(resourceNames: null, hasError: true, limit: null);

        // Assert - should only return 1 trace (the one with errors)
        Assert.NotNull(result);
        Assert.Equal(1, result.ReturnedCount);
        
        // Verify with null filter returns both
        var allResult = service.GetTraces(resourceNames: null, hasError: null, limit: null);
        Assert.NotNull(allResult);
        Assert.Equal(2, allResult.ReturnedCount);
    }

    [Fact]
    public async Task FollowSpansAsync_WithInvalidResourceName_ReturnsNoSpans()
    {
        // Arrange
        var repository = CreateRepository();

        // Add spans for service1
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

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act - stream spans for a non-existent resource
        var receivedItems = new List<string>();
        try
        {
            await foreach (var item in service.FollowSpansAsync(["nonexistent-service"], null, null, cts.Token))
            {
                receivedItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - timeout
        }

        // Assert - should receive NO items because the resource doesn't exist
        Assert.Empty(receivedItems);
    }

    [Fact]
    public async Task FollowLogsAsync_WithInvalidResourceName_ReturnsNoLogs()
    {
        // Arrange
        var repository = CreateRepository();

        // Add logs for service1
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

        var service = CreateService(repository);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        // Act - stream logs for a non-existent resource
        var receivedItems = new List<string>();
        try
        {
            await foreach (var item in service.FollowLogsAsync(["nonexistent-service"], null, null, cts.Token))
            {
                receivedItems.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected - timeout
        }

        // Assert - should receive NO items because the resource doesn't exist
        Assert.Empty(receivedItems);
    }

    /// <summary>
    /// Creates a TelemetryApiService instance for testing with optional custom dependencies.
    /// </summary>
    private static TelemetryApiService CreateService(
        TelemetryRepository? repository = null,
        IOutgoingPeerResolver[]? peerResolvers = null)
    {
        return new TelemetryApiService(
            repository ?? CreateRepository(),
            peerResolvers ?? []);
    }
}
