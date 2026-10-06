// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class TelemetryErrorRecorderTests
{
    [Fact]
    public async Task RecordError_Exception_RecordsOriginalException()
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");

        recorder.RecordError("Test message", exception);

        var request = Assert.Single(await ReadFaultRequestsAsync(sender));
        AssertFault(request, exception);
    }

    [Fact]
    public async Task RecordError_AggregateException_RecordsDistinctLeafExceptions()
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");
        var duplicate = CreateException(new InvalidOperationException("Test error"), "original stack");
        var differentType = CreateException(new ArgumentException("Test error"), "original stack");
        var differentMessage = CreateException(new InvalidOperationException("Different error"), "original stack");
        var differentStack = CreateException(new InvalidOperationException("Test error"), "different stack");
        var aggregate = new AggregateException(
            "Exceptions were encountered while disposing components.",
            exception,
            duplicate,
            differentType,
            differentMessage,
            differentStack,
            new AggregateException(duplicate, new AggregateException(exception)));
        Assert.Null(aggregate.StackTrace);

        recorder.RecordError("Test message", aggregate);

        Assert.Collection(await ReadFaultRequestsAsync(sender),
            request => AssertFault(request, exception),
            request => AssertFault(request, differentType),
            request => AssertFault(request, differentMessage),
            request => AssertFault(request, differentStack));
    }

    [Fact]
    public async Task RecordError_AggregateExceptionWithoutStacks_DeduplicatesExceptions()
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);
        var exception = new InvalidOperationException("Test error");
        var aggregate = new AggregateException(exception, new InvalidOperationException("Test error"));

        recorder.RecordError("Test message", aggregate);

        var request = Assert.Single(await ReadFaultRequestsAsync(sender));
        AssertFault(request, exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordError_EmptyAggregateException_RecordsOriginalException(bool nested)
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);
        var exception = nested ? new AggregateException(new AggregateException()) : new AggregateException();

        recorder.RecordError("Test message", exception);

        var request = Assert.Single(await ReadFaultRequestsAsync(sender));
        AssertFault(request, exception);
    }

    [Fact]
    public async Task RecordError_RepeatedCalls_RecordsEachOccurrence()
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);
        var exception = CreateException(new InvalidOperationException("Test error"), "original stack");
        var aggregate = new AggregateException(exception, exception);

        recorder.RecordError("Test message", aggregate);
        recorder.RecordError("Test message", aggregate);

        Assert.Collection(await ReadFaultRequestsAsync(sender),
            request => AssertFault(request, exception),
            request => AssertFault(request, exception));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordError_WriteToLogging_LogsOriginalExceptionOnce(bool writeToLogging)
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = true };
        await sender.TryStartTelemetrySessionAsync();
        var sink = new TestSink();
        using var loggerFactory = new TestLoggerFactory(sink, enabled: true);
        var logger = new TestLogger<TelemetryErrorRecorder>(loggerFactory);
        var service = new DashboardTelemetryService(NullLogger<DashboardTelemetryService>.Instance, sender);
        var recorder = new TelemetryErrorRecorder(service, logger);
        var exception = new InvalidOperationException("Test error");
        var aggregate = new AggregateException(exception, exception);

        recorder.RecordError("Test message", aggregate, writeToLogging);

        var logs = sink.Writes;
        if (writeToLogging)
        {
            var log = Assert.Single(logs);
            Assert.Same(aggregate, log.Exception);
            Assert.Equal("Test message", log.Message);
        }
        else
        {
            Assert.Empty(logs);
        }

        var request = Assert.Single(await ReadFaultRequestsAsync(sender));
        AssertFault(request, exception);
    }

    [Fact]
    public async Task RecordError_TelemetryDisabled_DoesNotQueueFaults()
    {
        await using var sender = new TestDashboardTelemetrySender { IsTelemetryEnabled = false };
        await sender.TryStartTelemetrySessionAsync();
        var recorder = CreateRecorder(sender);

        recorder.RecordError("Test message", new AggregateException(new InvalidOperationException("Test error")));

        Assert.Empty(await ReadFaultRequestsAsync(sender));
    }

    private static TelemetryErrorRecorder CreateRecorder(TestDashboardTelemetrySender sender)
    {
        var service = new DashboardTelemetryService(NullLogger<DashboardTelemetryService>.Instance, sender);

        return new TelemetryErrorRecorder(service, NullLogger<TelemetryErrorRecorder>.Instance);
    }

    private static Exception CreateException(Exception exception, string stackTrace)
    {
        ExceptionDispatchInfo.SetRemoteStackTrace(exception, stackTrace);

        return exception;
    }

    private static void AssertFault(PostFaultRequest request, Exception exception)
    {
        Assert.Equal(TelemetryEventKeys.Error, request.EventName);
        Assert.Equal($"{exception.GetType().FullName}: {exception.Message}", request.Description);
        Assert.Equal(FaultSeverity.Critical, request.Severity);
        Assert.NotNull(request.Properties);
        Assert.Equal(new AspireTelemetryProperty(exception.GetType().FullName!), request.Properties[TelemetryPropertyKeys.ExceptionType]);
        Assert.Equal(new AspireTelemetryProperty(exception.Message), request.Properties[TelemetryPropertyKeys.ExceptionMessage]);
        Assert.Equal(new AspireTelemetryProperty(exception.StackTrace ?? string.Empty), request.Properties[TelemetryPropertyKeys.ExceptionStackTrace]);
        Assert.Equal(new AspireTelemetryProperty(VersionHelpers.RuntimeVersion?.ToString() ?? string.Empty), request.Properties[TelemetryPropertyKeys.ExceptionRuntimeVersion]);
    }

    internal static async Task<List<PostFaultRequest>> ReadFaultRequestsAsync(TestDashboardTelemetrySender sender)
    {
        var requests = new List<PostFaultRequest>();
        using var handler = new TestHttpMessageHandler((request, _) =>
        {
            Assert.Equal(TelemetryEndpoints.TelemetryPostFault, request.RequestUri!.AbsolutePath);
            var content = Assert.IsType<JsonContent>(request.Content);
            requests.Add(Assert.IsType<PostFaultRequest>(content.Value));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new TelemetryEventCorrelation { Id = Guid.NewGuid() }))
            });
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        while (sender.RequestChannel.Reader.TryRead(out var requestFunc))
        {
            await requestFunc(client, _ => throw new InvalidOperationException("Faults should not have correlations."));
        }

        return requests;
    }
}
