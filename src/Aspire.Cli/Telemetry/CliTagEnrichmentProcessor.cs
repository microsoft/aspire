// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Processor that applies background-calculated telemetry tags to activities before export.
/// Tags are sourced from <see cref="TelemetryTagsSource"/> which computes machine/identity
/// information asynchronously at startup. Event-level enrichment is handled separately in
/// <see cref="AspireCliTelemetry.RecordError"/> at event creation time.
/// </summary>
internal sealed class CliTagEnrichmentProcessor : BaseProcessor<Activity>
{
    private readonly TelemetryTagsSource _tagsSource;

    public CliTagEnrichmentProcessor(TelemetryTagsSource tagsSource)
    {
        _tagsSource = tagsSource;
    }

    public override void OnEnd(Activity activity)
    {
        var tags = _tagsSource.GetResolvedTags();

        // Add tags to the activity itself.
        foreach (var tag in tags)
        {
            activity.SetTag(tag.Key, tag.Value);
        }
    }
}
