// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class AspireTelemetryBaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SetActivityProperties_AppliesPolicyWithoutAddingDefaultMetadata(bool batch)
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        using var activity = telemetry.StartReportedActivity("test-operation");
        Assert.NotNull(activity);
        telemetry.SetActivityProperty(activity, "allowed.property", "original");
        KeyValuePair<string, object?>[] properties =
        [
            new("allowed.property", "updated"),
            new("secret.property", "private property")
        ];

        if (batch)
        {
            telemetry.SetActivityProperties(activity, properties);
        }
        else
        {
            foreach (var (key, value) in properties)
            {
                telemetry.SetActivityProperty(activity, key, value);
            }
        }

        Assert.Collection(activity.TagObjects,
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.property", "UPDATED"), tag));
        Assert.Empty(activity.Events);
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void SetActivityProperties_WithNullActivity_DoesNotEnumerateProperties()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        var enumeratedCount = 0;
        var properties = Enumerable.Range(0, 1).Select(_ =>
        {
            enumeratedCount++;
            return new KeyValuePair<string, object?>("allowed.property", "value");
        });

        telemetry.SetActivityProperties(null, properties);
        telemetry.SetActivityProperty(null, "allowed.property", "value");

        Assert.Equal(0, enumeratedCount);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Fact]
    public void Recording_AppliesPolicyToDefaultsAndPropertiesForActivitiesAndLogs()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        KeyValuePair<string, object?>[] properties =
        [
            new("allowed.property", "value"),
            new("secret.property", "private property")
        ];
        using var activity = telemetry.StartOperation(properties);
        Assert.NotNull(activity);

        telemetry.RecordEvent(properties);

        Assert.Collection(activity.TagObjects.OrderBy(t => t.Key, StringComparer.Ordinal),
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.default", "DEFAULT"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.property", "VALUE"), tag));
        Assert.Empty(activity.Events);
        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Collection(log.Attributes,
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.default", "DEFAULT"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.property", "VALUE"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", "test-event"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", "test-event"), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordError_AppliesPolicyToDefaultsAndExceptionPropertiesWithoutCreatingActivity(bool withActivity)
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        using var operation = withActivity ? telemetry.StartOperation([]) : null;
        var current = Activity.Current;
        var exception = new InvalidOperationException("private exception message");

        telemetry.RecordError("Local error", exception);

        Assert.Same(current, Activity.Current);
        Assert.False(fixture.ActivityChannel.Reader.TryPeek(out _));
        if (withActivity)
        {
            Assert.NotNull(operation);
            Assert.Collection(operation.TagObjects,
                tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.default", "DEFAULT"), tag));
            Assert.Equal(ActivityStatusCode.Unset, operation.Status);
            Assert.Empty(operation.Events);
        }

        Assert.True(fixture.LogChannel.Reader.TryRead(out var log));
        Assert.Equal(current?.TraceId ?? default, log.TraceId);
        Assert.Equal(current?.SpanId ?? default, log.SpanId);
        Assert.Collection(log.Attributes,
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.default", "DEFAULT"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("exception.type", typeof(InvalidOperationException).FullName!.ToUpperInvariant()), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", "test-error"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("microsoft.operation_name", "test-error"), tag));
        Assert.False(fixture.LogChannel.Reader.TryPeek(out _));
    }
}
