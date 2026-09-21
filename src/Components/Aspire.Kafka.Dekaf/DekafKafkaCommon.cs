// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Dekaf.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Kafka.Dekaf;

internal static class DekafKafkaCommon
{
    internal static IConfigurationRoot GetConfiguration(IHostApplicationBuilder builder, string sectionName, string connectionName)
    {
        var section = builder.Configuration.GetSection(sectionName);

        // Merge before passing native configuration to Dekaf so default and named options
        // are applied together. For example, Producer:orders:Config:ClientId
        // overrides Producer:Config:ClientId while retaining other default options.
        return new ConfigurationBuilder()
            .AddInMemoryCollection(section.AsEnumerable(makePathsRelative: true))
            .AddInMemoryCollection(section.GetSection(connectionName).AsEnumerable(makePathsRelative: true))
            .Build();
    }

    internal static string GetHealthCheckName(string role, string? serviceKey)
        => serviceKey is null ? $"Kafka.Dekaf_{role}" : $"Kafka.Dekaf_{role}_{serviceKey}";

    internal static void AddTelemetry(IHostApplicationBuilder builder, bool disableMetrics, bool disableTracing)
    {
        // Dekaf uses a shared source and meter for all clients. These switches control registration
        // with OpenTelemetry; another registration can still enable telemetry for the same source.
        // https://thomhurst.github.io/Dekaf/docs/observability
        if (!disableMetrics)
        {
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(DekafDiagnostics.MeterName));
        }

        if (!disableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(DekafDiagnostics.ActivitySourceName));
        }
    }
}
