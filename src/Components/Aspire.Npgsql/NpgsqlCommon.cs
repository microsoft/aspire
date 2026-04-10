// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry.Metrics;

internal static class NpgsqlCommon
{
    public static void AddNpgsqlMetrics(MeterProviderBuilder meterProviderBuilder)
    {
        // https://github.com/npgsql/npgsql/blob/4c9921de2dfb48fb5a488787fc7422add3553f50/src/Npgsql/MetricsReporter.cs#L48
        meterProviderBuilder
            .AddMeter("Npgsql");

#if LEGACY_NPGSQL
        // Npgsql versions prior to 10.0 don't align their metrics with the OpenTelemetry spec,
        // so custom histogram bucket boundaries are needed for the duration metrics.
        // See https://github.com/npgsql/npgsql/commit/a27566ff3e75ab1f75feb6d24cb69cdbd3340ab4
        double[] secondsBuckets = [0, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

        meterProviderBuilder
            // Npgsql's histograms are in seconds, not milliseconds.
            .AddView("db.client.commands.duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = secondsBuckets
                })
            .AddView("db.client.connections.create_time",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = secondsBuckets
                });
#endif
    }
}
