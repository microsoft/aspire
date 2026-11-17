// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Tests.Shared.Telemetry;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class ResourcePaneActivityTests
{
    [Fact]
    public void Summarize_UsesNearestRankAndErrorSpanStatus()
    {
        var resource = new OtlpResource("api", "one", false, TelemetryTestHelpers.CreateContext());
        var latest = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var items = Enumerable.Range(1, 20).Select(i => new RequestSpanSummary
        {
            Resource = resource,
            Name = "GET /",
            StartTime = latest.AddSeconds(i - 20),
            Duration = TimeSpan.FromMilliseconds(i),
            Status = i <= 2 ? OtlpSpanStatusCode.Error : OtlpSpanStatusCode.Ok
        }).ToArray();

        var result = Assert.Single(ResourcePaneActivity.Summarize(new GetRequestSpansResponse { Items = items, TotalItemCount = items.Length }));

        Assert.Equal(resource.ResourceKey, result.Key);
        Assert.Equal(new ResourcePaneActivity(20, 2, TimeSpan.FromMilliseconds(19), 20, latest), result.Value);
    }

    [Fact]
    public void Summarize_DoesNotMergeInstancesWithTheSameDisplayName()
    {
        var time = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        var resources = new[] { "one", "two" }.Select(instance =>
            new OtlpResource("api", instance, false, TelemetryTestHelpers.CreateContext())).ToArray();
        var items = resources.Select((resource, index) => new RequestSpanSummary
        {
            Resource = resource,
            Name = "receive",
            StartTime = time,
            Duration = TimeSpan.FromMilliseconds(index + 1),
            Status = OtlpSpanStatusCode.Ok
        }).ToArray();

        var result = ResourcePaneActivity.Summarize(new GetRequestSpansResponse { Items = items, TotalItemCount = items.Length });

        Assert.Equal(2, result.Count);
        Assert.Equal(new ResourcePaneActivity(1, 0, null, 1, time), result[resources[0].ResourceKey]);
        Assert.Equal(new ResourcePaneActivity(1, 0, null, 1, time), result[resources[1].ResourceKey]);
    }

    [Fact]
    public void Summarize_NoDataDoesNotInventZeroValuedSignals()
    {
        Assert.Empty(ResourcePaneActivity.Summarize(new GetRequestSpansResponse { Items = [], TotalItemCount = 0 }));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    public void Summarize_PercentileRequiresTwentyValidDurations(int validCount)
    {
        var resource = new OtlpResource("api", null, false, TelemetryTestHelpers.CreateContext());
        var items = Enumerable.Range(0, validCount + 1).Select(i => new RequestSpanSummary
        {
            Resource = resource,
            Name = "receive",
            StartTime = DateTime.UnixEpoch,
            Duration = TimeSpan.FromMilliseconds(i - 1),
            Status = OtlpSpanStatusCode.Unset
        }).ToArray();

        var result = Assert.Single(ResourcePaneActivity.Summarize(new GetRequestSpansResponse { Items = items, TotalItemCount = items.Length })).Value;

        Assert.Equal(validCount + 1, result.Count);
        Assert.Equal(validCount, result.DurationSampleCount);
        Assert.Equal(validCount == 20 ? TimeSpan.FromMilliseconds(18) : (TimeSpan?)null, result.P95);
    }
}
