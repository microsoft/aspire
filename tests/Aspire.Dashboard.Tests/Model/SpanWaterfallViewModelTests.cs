// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Proto.Common.V1;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class SpanWaterfallViewModelTests
{
    [Fact]
    public void Create_HasChildren_ChildrenPopulated()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: false, context);

        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "1", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc)));
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app2, trace, scope, spanId: "1-1", parentSpanId: "1", startDate: new DateTime(2001, 1, 1, 1, 1, 3, DateTimeKind.Utc)));

        // Act
        var vm = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], []));

        // Assert
        Assert.Collection(vm,
            e =>
            {
                Assert.Equal("1", e.Span.SpanId);
                Assert.Equal("1-1", Assert.Single(e.Children).Span.SpanId);
            },
            e =>
            {
                Assert.Equal("1-1", e.Span.SpanId);
                Assert.Empty(e.Children);
            });
    }

    [Fact]
    public void Create_RootSpanZeroDuration_ZeroPercentage()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app1View = new OtlpResourceView(app1, new RepeatedField<KeyValue>());

        var date = new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "31", parentSpanId: null, startDate: date, endDate: date));
        var log = new OtlpLogEntry(TelemetryTestHelpers.CreateLogRecord(traceId: trace.TraceId, spanId: "1"), app1View, scope, context);

        // Act
        var vm = SpanWaterfallViewModel.Create(trace, [log], new SpanWaterfallViewModel.TraceDetailState([], [], []));

        // Assert
        Assert.Collection(vm,
            e =>
            {
                Assert.Equal("31", e.Span.SpanId);
                Assert.Equal(0, e.LeftOffset);
                Assert.Equal(0, e.Width);

                var spanLog = Assert.Single(e.SpanLogs);
                Assert.Equal(0, spanLog.LeftOffset);
            });
    }

    [Fact]
    public void Create_OutgoingPeers_BrowserLink()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: false, context);

        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "1", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc), kind: OtlpSpanKind.Client, attributes: [KeyValuePair.Create("http.url", "http://localhost:59267/6eed7c2dedc14419901b813e8fe87a86/getScriptTag"), KeyValuePair.Create("server.address", "localhost")]));
        trace.AddSpan(TelemetryTestHelpers.CreateOtlpSpan(app2, trace, scope, spanId: "2", parentSpanId: null, startDate: new DateTime(2001, 2, 1, 1, 1, 2, DateTimeKind.Utc), kind: OtlpSpanKind.Client));

        // Act
        var vm = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([new BrowserLinkOutgoingPeerResolver()], [], []));

        // Assert
        Assert.Collection(vm,
            e =>
            {
                Assert.Equal("1", e.Span.SpanId);
                Assert.Equal("Browser Link", e.UninstrumentedPeer);
            },
            e =>
            {
                Assert.Equal("2", e.Span.SpanId);
                Assert.Null(e.UninstrumentedPeer);
            });
    }

    [Theory]
    [InlineData("1234", true)]  // Matches span ID
    [InlineData("app1", true)]  // Matches resource name
    [InlineData("Test", true)]  // Matches display summary
    [InlineData("peer-service", true)]  // Matches uninstrumented peer
    [InlineData("nonexistent", false)]  // Doesn't match anything
    public void MatchesFilter_VariousCases_ReturnsExpected(string filter, bool expected)
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        // Create a span with an attribute that simulates uninstrumented peer
        var attributes = new[]
        {
            new KeyValuePair<string, string>("peer.service", "peer-service")
        };

        var span = TelemetryTestHelpers.CreateOtlpSpan(
            app,
            trace,
            scope,
            spanId: "12345",
            parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: attributes,
            statusCode: OtlpSpanStatusCode.Unset,
            statusMessage: null,
            kind: OtlpSpanKind.Client);

        trace.AddSpan(span);

        var vm = SpanWaterfallViewModel.Create(
            trace,
            [],
            new SpanWaterfallViewModel.TraceDetailState([], [], [])).First();

        // Act
        var result = vm.MatchesFilter(filter, typeFilter: null, structuredFilters: null, a => a.Resource.ResourceName, out _);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("http", null, new string[] { }, false)]
    [InlineData("http", null, new string[] { "http.request.method" }, true)]
    [InlineData("database", "Azure", new string[] { "http.request.method" }, false)]
    [InlineData("database", null, new string[] { "db.system.name" }, true)]
    [InlineData("database", null, new string[] { "db.system" }, true)]
    [InlineData("database", null, new string[] { }, false)]
    [InlineData("messaging", null, new string[] { "messaging.system" }, true)]
    [InlineData("messaging", null, new string[] { }, false)]
    [InlineData("rpc", "Azure", new string[] { "rpc.system" }, true)]
    [InlineData("rpc", null, new string[] { }, false)]
    [InlineData("genai", null, new string[] { "gen_ai.system" }, true)]
    [InlineData("genai", null, new string[] { "gen_ai.provider.name" }, true)]
    [InlineData("genai", null, new string[] { "gen_ai.operation.name" }, true)]
    [InlineData("genai", null, new string[] { }, false)]
    [InlineData("cloud", "Azure", new string[0], true)]
    [InlineData("cloud", "AZURE", new string[0], true)]
    [InlineData("cloud", "AZURE.", new string[0], true)]
    [InlineData("cloud", "Azure.Whatever", new string[0], true)]
    [InlineData("cloud", "AWSSDK", new string[0], true)]
    [InlineData("cloud", "Other", new string[0], false)]
    public void MatchesFilter_SpanType_ReturnsExpected(string spanTypeName, string? scopeName, string[] presentAttributeNames, bool expected)
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context, name: scopeName);
        var spanType = SpanType.CreateKnownSpanTypes(new TestStringLocalizer<ControlsStrings>()).Single(t => t.Id?.Name == spanTypeName);
        var otherSpanType = SpanType.CreateKnownSpanTypes(new TestStringLocalizer<ControlsStrings>()).Single(t => t.Id?.Name == "other");

        // Create a span with an attribute that simulates uninstrumented peer
        var attributes = presentAttributeNames.Select(n => new KeyValuePair<string, string>(n, Guid.NewGuid().ToString())).ToArray();

        var span = TelemetryTestHelpers.CreateOtlpSpan(
            app,
            trace,
            scope,
            spanId: "12345",
            parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: attributes,
            statusCode: OtlpSpanStatusCode.Unset,
            statusMessage: null,
            kind: OtlpSpanKind.Client);

        trace.AddSpan(span);

        var vm = SpanWaterfallViewModel.Create(
            trace,
            [],
            new SpanWaterfallViewModel.TraceDetailState([], [], [])).First();

        // Act 1
        var result1 = vm.MatchesFilter(string.Empty, typeFilter: spanType.Id?.Filter, structuredFilters: null, a => a.Resource.ResourceName, out _);

        // Assert 1
        Assert.Equal(expected, result1);

        // If the span type matches then it shouldn't match "Other" type.
        if (result1)
        {
            // Act 2
            var result2 = vm.MatchesFilter(string.Empty, typeFilter: otherSpanType.Id?.Filter, structuredFilters: null, a => a.Resource.ResourceName, out _);

            // Assert 2
            Assert.False(result2);
        }
    }

    [Fact]
    public void MatchesFilter_ParentSpanIncludedWhenChildMatched()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var parentSpan = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "parent", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc));
        var childSpan = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "child", parentSpanId: "parent", startDate: new DateTime(2001, 1, 1, 1, 1, 3, DateTimeKind.Utc));
        trace.AddSpan(parentSpan);
        trace.AddSpan(childSpan);

        var vms = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], []));
        var parent = vms[0];
        var child = vms[1];

        // Act and assert
        Assert.True(parent.MatchesFilter("child", typeFilter: null, structuredFilters: null, a => a.Resource.ResourceName, out _));
        Assert.True(child.MatchesFilter("child", typeFilter: null, structuredFilters: null, a => a.Resource.ResourceName, out _));
    }

    [Fact]
    public void MatchesFilter_ChildSpanIncludedWhenParentMatched()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var parentSpan = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "parent", parentSpanId: null, startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc));
        var childSpan = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "child", parentSpanId: "parent", startDate: new DateTime(2001, 1, 1, 1, 1, 3, DateTimeKind.Utc));
        trace.AddSpan(parentSpan);
        trace.AddSpan(childSpan);

        var vms = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], []));
        var parent = vms[0];
        var child = vms[1];

        // Act and assert
        Assert.True(parent.MatchesFilter("parent", typeFilter: null, structuredFilters: null, a => a.Resource.ResourceName, out var descendents));
        Assert.Equal("child", Assert.Single(descendents).Span.SpanId);
        Assert.False(child.MatchesFilter("parent", typeFilter: null, structuredFilters: null, a => a.Resource.ResourceName, out _));
    }

    [Theory]
    [InlineData("http.method", FilterCondition.Equals, "GET", true)]  // Exact match
    [InlineData("http.method", FilterCondition.Equals, "POST", false)]  // Wrong value
    [InlineData("http.method", FilterCondition.Contains, "GE", true)]  // Partial match
    [InlineData("http.method", FilterCondition.Contains, "DELETE", false)]  // Partial no match
    [InlineData("http.method", FilterCondition.NotEqual, "POST", true)]  // Not equal passes
    [InlineData("http.method", FilterCondition.NotEqual, "GET", false)]  // Not equal fails
    [InlineData("http.method", FilterCondition.NotContains, "POS", true)]  // Not contains passes
    [InlineData("http.method", FilterCondition.NotContains, "GE", false)]  // Not contains fails
    [InlineData("nonexistent.attr", FilterCondition.Equals, "GET", false)]  // Field not present
    public void MatchesFilter_StructuredFilter_SingleFilter_ReturnsExpected(string field, FilterCondition condition, string value, bool expected)
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var attributes = new[]
        {
            new KeyValuePair<string, string>("http.method", "GET"),
            new KeyValuePair<string, string>("http.status_code", "200")
        };

        var span = TelemetryTestHelpers.CreateOtlpSpan(
            app, trace, scope, spanId: "1", parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: attributes,
            statusCode: OtlpSpanStatusCode.Unset, statusMessage: null, kind: OtlpSpanKind.Client);
        trace.AddSpan(span);

        var vm = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], [])).First();

        var filters = new List<FieldTelemetryFilter>
        {
            new() { Field = field, Condition = condition, Value = value }
        };

        // Act
        var result = vm.MatchesFilter(string.Empty, typeFilter: null, structuredFilters: filters, a => a.Resource.ResourceName, out _);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MatchesFilter_StructuredFilter_MultipleFilters_AllMustMatch()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var attributes = new[]
        {
            new KeyValuePair<string, string>("http.method", "GET"),
            new KeyValuePair<string, string>("http.status_code", "200")
        };

        var span = TelemetryTestHelpers.CreateOtlpSpan(
            app, trace, scope, spanId: "1", parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: attributes,
            statusCode: OtlpSpanStatusCode.Unset, statusMessage: null, kind: OtlpSpanKind.Client);
        trace.AddSpan(span);

        var vm = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], [])).First();

        // One filter matches, one doesn't — AND logic means the span shouldn't match.
        var filters = new List<FieldTelemetryFilter>
        {
            new() { Field = "http.method", Condition = FilterCondition.Equals, Value = "GET" },
            new() { Field = "http.status_code", Condition = FilterCondition.Equals, Value = "500" }
        };

        // Act
        var result = vm.MatchesFilter(string.Empty, typeFilter: null, structuredFilters: filters, a => a.Resource.ResourceName, out _);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void MatchesFilter_StructuredFilter_DisabledFilterIsIgnored()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var attributes = new[]
        {
            new KeyValuePair<string, string>("http.method", "GET")
        };

        var span = TelemetryTestHelpers.CreateOtlpSpan(
            app, trace, scope, spanId: "1", parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: attributes,
            statusCode: OtlpSpanStatusCode.Unset, statusMessage: null, kind: OtlpSpanKind.Client);
        trace.AddSpan(span);

        var vm = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], [])).First();

        // Filter would exclude the span, but it's disabled.
        var filters = new List<FieldTelemetryFilter>
        {
            new() { Field = "http.method", Condition = FilterCondition.Equals, Value = "POST", Enabled = false }
        };

        // Act
        var result = vm.MatchesFilter(string.Empty, typeFilter: null, structuredFilters: filters, a => a.Resource.ResourceName, out _);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void MatchesFilter_StructuredFilter_ParentMatchesWhenChildHasMatchingFilter()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var parentSpan = TelemetryTestHelpers.CreateOtlpSpan(
            app, trace, scope, spanId: "parent", parentSpanId: null,
            startDate: new DateTime(2001, 1, 1, 1, 1, 2, DateTimeKind.Utc),
            attributes: [new KeyValuePair<string, string>("http.method", "GET")],
            statusCode: OtlpSpanStatusCode.Unset, statusMessage: null, kind: OtlpSpanKind.Server);

        var childSpan = TelemetryTestHelpers.CreateOtlpSpan(
            app, trace, scope, spanId: "child", parentSpanId: "parent",
            startDate: new DateTime(2001, 1, 1, 1, 1, 3, DateTimeKind.Utc),
            attributes: [new KeyValuePair<string, string>("db.system", "postgresql")],
            statusCode: OtlpSpanStatusCode.Unset, statusMessage: null, kind: OtlpSpanKind.Client);

        trace.AddSpan(parentSpan);
        trace.AddSpan(childSpan);

        var vms = SpanWaterfallViewModel.Create(trace, [], new SpanWaterfallViewModel.TraceDetailState([], [], []));
        var parent = vms[0];
        var child = vms[1];

        // Filter matches child but not parent — parent should still match via descendant logic.
        var filters = new List<FieldTelemetryFilter>
        {
            new() { Field = "db.system", Condition = FilterCondition.Equals, Value = "postgresql" }
        };

        // Act
        var parentResult = parent.MatchesFilter(string.Empty, typeFilter: null, structuredFilters: filters, a => a.Resource.ResourceName, out _);
        var childResult = child.MatchesFilter(string.Empty, typeFilter: null, structuredFilters: filters, a => a.Resource.ResourceName, out _);

        // Assert
        Assert.True(parentResult);
        Assert.True(childResult);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
