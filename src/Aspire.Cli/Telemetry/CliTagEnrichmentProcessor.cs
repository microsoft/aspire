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
        var suppressInternalIdentity = activity.OperationName == TelemetryConstants.Activities.InternalMicrosoftDetector;

        // Add tags to the activity itself.
        foreach (var tag in tags)
        {
            // The detector activity reports only bounded outcome metadata. Alias and domain are
            // already attached to ordinary reported activities and must not be duplicated onto
            // the detector-health event that measures whether those values were available.
            if (suppressInternalIdentity &&
                tag.Key is TelemetryConstants.Tags.InternalMicrosoftAlias or TelemetryConstants.Tags.InternalMicrosoftDomain)
            {
                continue;
            }

            activity.SetTag(tag.Key, tag.Value);
        }
    }
}
