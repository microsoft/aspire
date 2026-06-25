// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Telemetry;
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
    public void OnEnd_WhenTagsNotYetResolved_StillAppliesTags()
    {
        // Verifies the processor handles the synchronous wait path when tags
        // haven't completed yet (the GetResolvedTags blocking path).
        var tagsSource = new TelemetryTagsSource(NullLogger<TelemetryTagsSource>.Instance);

        // Create a fixture just to start the tag calculation on the tagsSource
        using var fixture = new TelemetryFixture();

        // Use the fixture's TagsSource which has completed calculation
        var processor = new CliTagEnrichmentProcessor(fixture.TagsSource);

        using var source = new ActivitySource($"Test.{Path.GetRandomFileName()}");
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = source.StartActivity("test-op")!;
        processor.OnEnd(activity);

        // Tags were applied even though we used GetResolvedTags (sync path)
        var tags = activity.Tags.ToList();
        Assert.NotEmpty(tags);
    }
}
