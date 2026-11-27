// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.TelemetryRepositoryTests;

public class OtlpSpanTests
{
    private static readonly DateTime s_testTime = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AllProperties()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var span = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime,
            statusCode: OtlpSpanStatusCode.Ok, statusMessage: "Status message!", attributes: [new KeyValuePair<string, string>(KnownTraceFields.StatusMessageField, "value")]);

        // Act
        var knownProperties = span.GetKnownProperties();
        var attributeProperties = span.GetAttributeProperties();

        // Assert
        Assert.Collection(knownProperties,
            a =>
            {
                Assert.Equal("trace.spanid", a.Key);
                Assert.Equal("abc", a.Value);
            },
            a =>
            {
                Assert.Equal("trace.name", a.Key);
                Assert.Equal("Test", a.Value);
            },
            a =>
            {
                Assert.Equal("trace.kind", a.Key);
                Assert.Equal("Unspecified", a.Value);
            },
            a =>
            {
                Assert.Equal("trace.status", a.Key);
                Assert.Equal("Ok", a.Value);
            },
            a =>
            {
                Assert.Equal("trace.statusmessage", a.Key);
                Assert.Equal("Status message!", a.Value);
            });
        Assert.Collection(attributeProperties,
            a =>
            {
                Assert.Equal("unknown-trace.statusmessage", a.Key);
                Assert.Equal("value", a.Value);
            });
    }

    [Fact]
    public void GetDestination_NoChildOrPeer_Null()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var span = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime);

        // Act
        var destination = span.GetDestination();

        // Assert
        Assert.Null(destination);
    }

    [Fact]
    public void GetDestination_HasPeer_ReturnPeerResource()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: true, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var span = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime,
            kind: OtlpSpanKind.Client, uninstrumentedPeer: app2);

        // Act
        var destination = span.GetDestination();

        // Assert
        Assert.Equal(app2, destination);
    }

    [Fact]
    public void GetDestination_HasSingleChild_ReturnChildResource()
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app1 = new OtlpResource("app1", "instance", uninstrumentedPeer: false, context);
        var app2 = new OtlpResource("app2", "instance", uninstrumentedPeer: true, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var parentSpan = TelemetryTestHelpers.CreateOtlpSpan(app1, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime,
            kind: OtlpSpanKind.Client);
        var childSpan = TelemetryTestHelpers.CreateOtlpSpan(app2, trace, scope, spanId: "abc-2", parentSpanId: "abc", startDate: s_testTime,
            kind: OtlpSpanKind.Server);

        trace.AddSpan(parentSpan);
        trace.AddSpan(childSpan);

        // Act
        var destination = parentSpan.GetDestination();

        // Assert
        Assert.Equal(app2, destination);
    }

    public static IEnumerable<object[]> DisplaySummary_SpanData =>
        new List<object[]>
        {
            new object[]
            {
                "HTTP GET 200",
                "SpanName!",
                OtlpSpanKind.Client,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("http.request.method", "GET"),
                    KeyValuePair.Create("http.response.status_code", "200")
                }
            },
            new object[]
            {
                "HTTP GET 200",
                "SpanName!",
                OtlpSpanKind.Client,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("http.method", "GET"),
                    KeyValuePair.Create("http.status_code", "200")
                }
            },
            new object[]
            {
                "SpanName!",
                "SpanName!",
                OtlpSpanKind.Server,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("http.method", "GET"),
                    KeyValuePair.Create("http.status_code", "200")
                }
            },
            new object[]
            {
                "MSG kafka send orders",
                "SpanName!",
                OtlpSpanKind.Producer,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("messaging.system", "kafka"),
                    KeyValuePair.Create("messaging.operation.name", "send"),
                    KeyValuePair.Create("messaging.destination.name", "orders")
                }
            },
            new object[]
            {
                "MSG kafka send orders",
                "SpanName!",
                OtlpSpanKind.Producer,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("messaging.system", "kafka"),
                    KeyValuePair.Create("messaging.operation.name", "send"),
                    KeyValuePair.Create("messaging.operation", "legacy-operation"),
                    KeyValuePair.Create("messaging.destination.name", "orders")
                }
            },
            new object[]
            {
                "MSG kafka legacy-operation orders",
                "SpanName!",
                OtlpSpanKind.Producer,
                new KeyValuePair<string, string>[]
                {
                    KeyValuePair.Create("messaging.system", "kafka"),
                    KeyValuePair.Create("messaging.operation", "legacy-operation"),
                    KeyValuePair.Create("messaging.destination.name", "orders")
                }
            },
        };

    [Theory]
    [MemberData(nameof(DisplaySummary_SpanData))]
    public void GetDisplaySummary_SpanData_ReturnExpectedDisplaySummary(string expectedDisplaySummary, string spanName, OtlpSpanKind kind, KeyValuePair<string, string>[] attributes)
    {
        // Arrange
        var context = new OtlpContext { Logger = NullLogger.Instance, Options = new() };
        var app = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);

        var span = TelemetryTestHelpers.CreateOtlpSpan(app, trace, scope, spanId: "abc", parentSpanId: null, startDate: s_testTime,
            kind: kind, attributes: attributes, spanName: spanName);

        // Act
        var displaySummary = span.GetDisplaySummary();

        // Assert
        Assert.Equal(expectedDisplaySummary, displaySummary);
    }
}
