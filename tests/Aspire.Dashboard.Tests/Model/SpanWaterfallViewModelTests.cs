// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SpanWaterfallViewModelTests
{
    [Fact]
    public void Create_HasChildren_ChildrenPopulated()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "1", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc)));
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app2, trace, scope, spanId: "1-1", parentSpanId: "1", startDate: new DateTime(2001, 1, 1, 1, 1, 3, DateTimeKind.Utc)));

        var viewModels = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], []));

        Assert.Collection(viewModels,
            item =>
            {
                Assert.Equal("1", item.Span.SpanId);
                Assert.Equal("1-1", Assert.Single(item.Children).Span.SpanId);
            },
            item =>
            {
                Assert.Equal("1-1", item.Span.SpanId);
                Assert.Empty(item.Children);
            });
    }

    [Fact]
    public void Create_RootSpanZeroDuration_ZeroPercentage()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var date = new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "31", parentSpanId: null, startDate: date, endDate: date));
        var log = new LogSummary
        {
            InternalId = 1,
            TimeStamp = date,
            Severity = LogLevel.Information,
            Message = "Test log",
            SpanId = "31",
            TraceId = trace.TraceId,
            ScopeName = scope.Name,
            Resource = app1,
            ExceptionText = null,
            HasGenAI = false
        };

        var viewModels = SpanWaterfallViewModel.Create(trace, [log], new SpanWaterfallViewModel.TraceDetailState([], []));

        var root = Assert.Single(viewModels);
        Assert.Equal("31", root.Span.SpanId);
        Assert.Equal(0, root.LeftOffset);
        Assert.Equal(0, root.Width);
        var spanLog = Assert.Single(root.SpanLogs);
        Assert.Equal(0, spanLog.LeftOffset);
    }

    [Fact]
    public void Create_OutgoingPeers_UsesPeerAddressWhenPeerIsNotPersisted()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "1", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc), kind: OtlpSpanKind.Client, attributes: [KeyValuePair.Create("http.url", "http://localhost:59267/getScriptTag"), KeyValuePair.Create("server.address", "localhost")]));
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app2, trace, scope, spanId: "2", parentSpanId: null, startDate: new DateTime(2001, 2, 1, 1, 1, 2, DateTimeKind.Utc), kind: OtlpSpanKind.Client));

        var viewModels = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], []));

        Assert.Collection(viewModels,
            item =>
            {
                Assert.Equal("1", item.Span.SpanId);
                Assert.Equal("localhost", item.UninstrumentedPeer);
            },
            item =>
            {
                Assert.Equal("2", item.Span.SpanId);
                Assert.Null(item.UninstrumentedPeer);
            });
    }

    [Fact]
    public void Create_OutgoingPeers_UsesPersistedNameOnlyPeer()
    {
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);
        var browserLink = new OtlpResource("Browser Link", instanceId: null, uninstrumentedPeer: true, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(
            app,
            trace,
            scope,
            spanId: "1",
            parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            kind: OtlpSpanKind.Client,
            attributes: [KeyValuePair.Create(OtlpSpan.ServerAddressAttributeKey, "localhost")],
            uninstrumentedPeer: browserLink));

        var viewModel = Assert.Single(SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [app, browserLink])));

        Assert.Equal("Browser Link", viewModel.UninstrumentedPeer);
    }

    [Fact]
    public void SpanTypeFilters_EmptyOperandsThrow()
    {
        Assert.Throws<ArgumentException>(() => new SpanHasAttributeTelemetryFilter([]));
        Assert.Throws<ArgumentException>(() => new SpanScopePrefixTelemetryFilter([]));
        Assert.Throws<ArgumentException>(() => new SpanNoMatchTelemetryFilter([]));
    }
}