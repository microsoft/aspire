// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

internal sealed record ResourcePaneActivitySnapshot(
    ResourcePaneActivity Activity, DateTime WindowStart, DateTime WindowEnd, DateTime Refreshed, bool Sampled);

internal sealed record ResourcePaneActivity(int Count, int FailedCount, TimeSpan? P95, int DurationSampleCount, DateTime LatestStartTime)
{
    internal static IReadOnlyDictionary<ResourceKey, ResourcePaneActivity> Summarize(GetRequestSpansResponse response) =>
        response.Items.GroupBy(span => span.Resource.ResourceKey).ToDictionary(group => group.Key, group =>
        {
            var durations = group.Select(span => span.Duration).Where(duration => duration >= TimeSpan.Zero).Order().ToArray();
            // Use nearest-rank p95, matching the application summary. These are server/consumer
            // spans, not whole-trace durations or an instrument's histogram percentiles.
            // Twenty valid durations give the empirical distribution its first 5% tail sample.
            // Smaller sets still show activity/failures, but do not imply a meaningful p95.
            var p95 = durations.Length >= 20
                ? (TimeSpan?)durations[(int)Math.Ceiling(durations.Length * 0.95) - 1]
                : null;
            return new ResourcePaneActivity(
                group.Count(),
                group.Count(span => span.Status == OtlpSpanStatusCode.Error),
                p95,
                durations.Length,
                group.Max(span => span.StartTime));
        });
}
