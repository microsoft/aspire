// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Telemetry;
using Aspire.Shared.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class TagEnrichmentProcessorTests
{
    [Fact]
    public void ActivityOnEnd_AddsCommonDashboardDefaults()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var activity = new Activity("test-operation");
        var processor = new ActivityTagEnrichmentProcessor(fixture.Telemetry);

        processor.OnEnd(activity);

        Assert.Equal(fixture.Telemetry.GetDefaultProperties().OrderBy(tag => tag.Key, StringComparer.Ordinal),
            activity.TagObjects.OrderBy(tag => tag.Key, StringComparer.Ordinal));
        Assert.Equal(
            [TelemetryPropertyKeys.DashboardBuildId, TelemetryPropertyKeys.InternalMicrosoft, TelemetryPropertyKeys.DashboardVersion,
                TelemetryPropertyKeys.DeploymentEnvironmentName, TelemetryPropertyKeys.DeviceId, TelemetryPropertyKeys.MacAddressHash,
                TelemetryPropertyKeys.OsName, TelemetryPropertyKeys.OsType, TelemetryPropertyKeys.OsVersion],
            activity.TagObjects.Select(tag => tag.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ActivityOnEnd_AppliesProductPrivacyPolicyToDefaults()
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        using var activity = new Activity("test-operation");
        var processor = new ActivityTagEnrichmentProcessor(telemetry);

        processor.OnEnd(activity);

        Assert.Collection(activity.TagObjects,
            tag => Assert.Equal(new KeyValuePair<string, object?>("allowed.default", "DEFAULT"), tag));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LogRecordOnEnd_AppliesPrivacyPolicyAndPreservesExistingProperties(bool overrideDefault)
    {
        using var fixture = new DashboardTelemetryFixture();
        using var telemetry = new TestTelemetryService(fixture);
        TestDashboardTelemetryLog? log = null;
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(telemetry));
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(telemetry));
            options.AddProcessor(new TestTelemetryProcessor<LogRecord>
            {
                EndCallback = record => log = TestDashboardTelemetryLog.Create(record)
            });
        }));
        var logger = factory.CreateLogger("test-events");

        if (overrideDefault)
        {
            logger.LogInformation("Value {allowed.default}", "event-value");
        }
        else
        {
            logger.Log(LogLevel.Information, new EventId(42, "test-event"), "plain message", null, static (state, _) => state);
        }

        Assert.NotNull(log);
        Assert.Equal(overrideDefault ? "Value event-value" : "plain message", log.Message);
        var expected = new List<KeyValuePair<string, object?>>
        {
            new("allowed.default", overrideDefault ? "event-value" : "DEFAULT")
        };
        if (overrideDefault)
        {
            expected.Add(new("{OriginalFormat}", "Value {allowed.default}"));
        }
        Assert.Equal(expected.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            log.Attributes.OrderBy(tag => tag.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void LogRecordOnEnd_AddsCommonDashboardDefaults()
    {
        using var fixture = new DashboardTelemetryFixture();
        TestDashboardTelemetryLog? log = null;
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(fixture.Telemetry));
            options.AddProcessor(new TestTelemetryProcessor<LogRecord>
            {
                EndCallback = record => log = TestDashboardTelemetryLog.Create(record)
            });
        }));

        factory.CreateLogger("test-events").LogInformation("test-event");

        Assert.NotNull(log);
        fixture.AssertAttributes(log.Attributes, new KeyValuePair<string, object?>("{OriginalFormat}", "test-event"));
    }
}
