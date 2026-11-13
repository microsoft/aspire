// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Telemetry;
using Aspire.Shared.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace Aspire.Cli.Tests.Telemetry;

public class LogRecordTagEnrichmentProcessorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnEnd_AddsDefaultsWithoutDuplicatingOrOverwritingAttributes(bool structured)
    {
        using var fixture = new TelemetryFixture(initialize: false);
        fixture.TagsSource.StartCalculation(() => Task.FromResult<IReadOnlyList<KeyValuePair<string, object?>>>(
        [
            new(TelemetryConstants.Tags.CliVersion, "test-version"),
            new(TelemetryConstants.Tags.DeviceId, "default-device"),
            new("test.collection", new[] { "one", "two" })
        ]));
        await fixture.TagsSource.TagsTask;
        KeyValuePair<string, object?>[]? attributes = null;
        string? message = null;
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(fixture.Telemetry));
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(fixture.Telemetry));
            options.AddProcessor(new TestTelemetryProcessor<LogRecord>
            {
                EndCallback = record =>
                {
                    attributes = record.Attributes?.ToArray();
                    message = record.FormattedMessage;
                }
            });
        }));
        var logger = factory.CreateLogger(AspireCliTelemetry.EventLogCategoryName);

        if (structured)
        {
            logger.LogInformation("Device {machine.device_id}", "event-device");
        }
        else
        {
            logger.Log(LogLevel.Information, new EventId(42, "test-event"), "plain message", null, static (state, _) => state);
        }

        Assert.Equal(structured ? "Device event-device" : "plain message", message);
        Assert.NotNull(attributes);
        var expected = new List<KeyValuePair<string, object?>>
        {
            new(TelemetryConstants.Tags.CliVersion, "test-version"),
            new(TelemetryConstants.Tags.DeviceId, structured ? "event-device" : "default-device"),
            new("test.collection", "one,two")
        };
        if (structured)
        {
            expected.Add(new("{OriginalFormat}", "Device {machine.device_id}"));
        }
        Assert.Equal(expected.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            attributes.OrderBy(tag => tag.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OnEnd_WaitsForPendingDefaults()
    {
        using var fixture = new TelemetryFixture(initialize: false);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.TagsSource.StartCalculation(async () =>
        {
            await gate.Task;
            return [new(TelemetryConstants.Tags.CliVersion, "resolved-version")];
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        KeyValuePair<string, object?>[]? attributes = null;
        using var factory = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
        {
            options.AddProcessor(new TestTelemetryProcessor<LogRecord> { EndCallback = _ => entered.TrySetResult() });
            options.AddProcessor(new LogRecordTagEnrichmentProcessor(fixture.Telemetry));
            options.AddProcessor(new TestTelemetryProcessor<LogRecord>
            {
                EndCallback = record => attributes = record.Attributes?.ToArray()
            });
        }));
        var logger = factory.CreateLogger(AspireCliTelemetry.EventLogCategoryName);
        var logging = Task.Run(() => logger.LogInformation("test-event"));
        try
        {
            await entered.Task.DefaultTimeout();
            Assert.False(logging.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }
        await logging.DefaultTimeout();

        Assert.NotNull(attributes);
        Assert.Collection(attributes.OrderBy(tag => tag.Key, StringComparer.Ordinal),
            tag => Assert.Equal(new KeyValuePair<string, object?>(TelemetryConstants.Tags.CliVersion, "resolved-version"), tag),
            tag => Assert.Equal(new KeyValuePair<string, object?>("{OriginalFormat}", "test-event"), tag));
    }
}
