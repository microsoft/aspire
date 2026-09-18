// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthModelPlayground;

internal static class HealthModelMetrics
{
    public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static string Format(HealthReport report, IEnumerable<HealthModelScenarioResource> resources)
    {
        var output = new StringBuilder();
        output.Append("# HELP aspire_health_status Simulated resource health: 0 = Unhealthy, 1 = Degraded, 2 = Healthy.\n");
        output.Append("# TYPE aspire_health_status gauge\n");

        foreach (var resource in resources.OrderBy(resource => resource.Name, StringComparer.Ordinal))
        {
            // An absent check is no data, even if the scenario's expected status is Healthy.
            // In particular, do not use the aggregate report status for an individual resource.
            if (report.Entries.TryGetValue(resource.HealthCheckName, out var entry))
            {
                AppendSample(output, resource.Name, resource.HealthCheckName, entry.Status);
            }

            // These entities run inside this serving process, rather than representing real databases.
            AppendSample(output, resource.Name, "resource-state", HealthStatus.Healthy);
        }

        return output.ToString();
    }

    private static void AppendSample(StringBuilder output, string resourceName, string healthCheck, HealthStatus status)
    {
        output.Append("aspire_health_status{resource_name=\"");
        AppendLabelValue(output, resourceName);
        output.Append("\",health_check=\"");
        AppendLabelValue(output, healthCheck);
        output.Append("\",replica_index=\"1\"} ");
        output.Append(((int)status).ToString(CultureInfo.InvariantCulture));
        output.Append('\n');
    }

    private static void AppendLabelValue(StringBuilder output, string value)
    {
        // A label such as api"\name<LF>next is written as api\"\\name\nnext.
        // Prometheus text format escapes backslashes, quotes, and line feeds:
        // https://prometheus.io/docs/instrumenting/exposition_formats/#text-format-details
        foreach (var character in value)
        {
            output.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                _ => character.ToString()
            });
        }
    }
}
