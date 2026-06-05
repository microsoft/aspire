// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Tests.Shared.Telemetry;
using Google.Protobuf.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class CrossColumnLogFilterTests
{
    private static OtlpContext CreateContext() => new() { Logger = NullLogger.Instance, Options = new() };

    private static OtlpLogEntry CreateLogEntry(string resourceName, string? message = null)
    {
        var context = CreateContext();
        var resource = new OtlpResource(resourceName, "instance", uninstrumentedPeer: false, context);
        var resourceView = new OtlpResourceView(resource, new RepeatedField<OpenTelemetry.Proto.Common.V1.KeyValue>());
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var logRecord = TelemetryTestHelpers.CreateLogRecord(message: message ?? "default message");
        return new OtlpLogEntry(logRecord, resourceView, scope, context);
    }

    [Fact]
    public void Apply_MatchesMessage()
    {
        var filter = new CrossColumnLogFilter("hello");

        var entries = new[]
        {
            CreateLogEntry("frontend", "hello world"),
            CreateLogEntry("backend", "goodbye world"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Single(result);
        Assert.Contains("hello", result[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_MatchesResourceName()
    {
        var filter = new CrossColumnLogFilter("frontend");

        var entries = new[]
        {
            CreateLogEntry("frontend", "some message"),
            CreateLogEntry("backend", "another message"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Single(result);
        Assert.Equal("frontend", result[0].ResourceView.Resource.ResourceName);
    }

    [Fact]
    public void Apply_CaseInsensitive()
    {
        var filter = new CrossColumnLogFilter("FRONTEND");

        var entries = new[]
        {
            CreateLogEntry("frontend", "some message"),
            CreateLogEntry("backend", "another message"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Single(result);
    }

    [Fact]
    public void Apply_NoMatch_ReturnsEmpty()
    {
        var filter = new CrossColumnLogFilter("nonexistent");

        var entries = new[]
        {
            CreateLogEntry("frontend", "hello world"),
            CreateLogEntry("backend", "goodbye world"),
        };

        var result = filter.Apply(entries).ToList();

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_SpanAlwaysReturnsTrue()
    {
        var filter = new CrossColumnLogFilter("anything");

        var context = CreateContext();
        var resource = new OtlpResource("app", "instance", uninstrumentedPeer: false, context);
        var trace = new OtlpTrace(new byte[] { 1, 2, 3 }, DateTime.MinValue);
        var scope = TelemetryTestHelpers.CreateOtlpScope(context);
        var span = TelemetryTestHelpers.CreateOtlpSpan(resource, trace, scope, spanId: "1", parentSpanId: null,
            startDate: DateTime.UtcNow, endDate: DateTime.UtcNow.AddSeconds(1));

        // CrossColumnLogFilter is not applicable to spans, should always return true
        Assert.True(filter.Apply(span));
    }

    [Fact]
    public void Equals_SameSearchText_ReturnsTrue()
    {
        var filter1 = new CrossColumnLogFilter("test");
        var filter2 = new CrossColumnLogFilter("TEST");

        Assert.True(filter1.Equals(filter2));
    }

    [Fact]
    public void Equals_DifferentSearchText_ReturnsFalse()
    {
        var filter1 = new CrossColumnLogFilter("test");
        var filter2 = new CrossColumnLogFilter("other");

        Assert.False(filter1.Equals(filter2));
    }
}
