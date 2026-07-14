// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Telemetry;

public class CliTagEnrichmentProcessorTests
{
    [Fact]
    public void OnEnd_AppliesResolvedTagsToActivity()
    {
        using var fixture = new TelemetryFixture();

        var processor = new CliTagEnrichmentProcessor(fixture.TagsSource);

        using var source = new ActivitySource($"Test.{Path.GetRandomFileName()}");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("test-op")!;
        Assert.NotNull(activity);

        // Tags should not be present before OnEnd
        Assert.DoesNotContain(activity.Tags, t => t.Key == "aspire.cli.version");

        processor.OnEnd(activity);

        // After OnEnd, enrichment tags are applied
        Assert.Contains(activity.Tags, t => t.Key == "aspire.cli.version");
        Assert.Contains(activity.Tags, t => t.Key == "machine.device_id");
    }

    [Fact]
    public async Task OnEnd_WhenTagsNotYetResolved_BlocksUntilTagsAvailable()
    {
        // Verifies the processor handles the synchronous wait path when tags
        // haven't completed yet (the GetResolvedTags blocking path).
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);

        // Gate the tag calculation behind a TaskCompletionSource so it hasn't completed
        // when OnEnd is called — this forces the blocking wait path in GetResolvedTags.
        var gate = new TaskCompletionSource<bool>();
        var expectedTags = new List<KeyValuePair<string, object?>>
        {
            new("aspire.cli.version", "1.0.0-test"),
            new("machine.device_id", "test-device-id")
        };

        tagsSource.StartCalculation(async () =>
        {
            await gate.Task;
            return expectedTags;
        });

        var processor = new CliTagEnrichmentProcessor(tagsSource);

        using var source = new ActivitySource($"Test.{Path.GetRandomFileName()}");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("test-op")!;

        // OnEnd will block waiting for the tags — run it on a background thread
        var onEndTask = Task.Run(() => processor.OnEnd(activity));

        // Tags should NOT be present yet (calculation is gated)
        Assert.False(onEndTask.IsCompleted);

        // Release the gate so GetResolvedTags unblocks
        gate.SetResult(true);

        // OnEnd should complete now
        await onEndTask.DefaultTimeout();

        // Tags were applied via the blocking wait path
        Assert.Contains(activity.Tags, t => t.Key == "aspire.cli.version" && (string?)t.Value == "1.0.0-test");
        Assert.Contains(activity.Tags, t => t.Key == "machine.device_id" && (string?)t.Value == "test-device-id");
    }
}
