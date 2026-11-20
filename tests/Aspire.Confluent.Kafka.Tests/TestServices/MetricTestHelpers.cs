// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry.Metrics;
using Xunit;

namespace Aspire.Confluent.Kafka.Tests;

internal static class MetricTestHelpers
{
    public static MetricPoint? GetMetricPointWithTag(Metric metric, string tagName, string tagValue)
    {
        MetricPoint? result = null;
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            if (Equals(GetTagValue(point, tagName), tagValue))
            {
                Assert.Null(result);
                result = point;
            }
        }

        return result;
    }

    public static object? GetTagValue(MetricPoint metricPoint, string name)
    {
        foreach (var tag in metricPoint.Tags)
        {
            if (tag.Key == name)
            {
                return tag.Value;
            }
        }

        return null;
    }
}
