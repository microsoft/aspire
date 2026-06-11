// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Represents the result of exporting data.
/// </summary>
/// <param name="Content">The content to export.</param>
/// <param name="FileName">The suggested file name for downloading the content.</param>
internal sealed record ExportResult(string Content, string FileName);

/// <summary>
/// Helper methods for exporting data.
/// </summary>
internal static class ExportHelpers
{
    /// <summary>
    /// Gets a span as a JSON export result, including associated log entries.
    /// </summary>
    public static ExportResult GetSpanAsJson(OtlpSpan span, TelemetryRepository telemetryRepository, IOutgoingPeerResolver[] outgoingPeerResolvers)
    {
        var logs = telemetryRepository.GetLogsForSpan(span.TraceId, span.SpanId);
        var json = TelemetryExportService.ConvertSpanToJson(span, outgoingPeerResolvers, logs);
        var fileName = $"span-{OtlpHelpers.ToShortenedId(span.SpanId)}.json";
        return new ExportResult(json, fileName);
    }

    /// <summary>
    /// Gets a log entry as a JSON export result.
    /// </summary>
    /// <param name="logEntry">The log entry to convert.</param>
    /// <returns>A result containing the JSON representation and suggested file name.</returns>
    public static ExportResult GetLogEntryAsJson(OtlpLogEntry logEntry)
    {
        var json = TelemetryExportService.ConvertLogEntryToJson(logEntry);
        var fileName = $"log-{logEntry.InternalId}.json";
        return new ExportResult(json, fileName);
    }

    /// <summary>
    /// Gets all spans in a trace as a JSON export result, including associated log entries.
    /// </summary>
    public static ExportResult GetTraceAsJson(OtlpTrace trace, TelemetryRepository telemetryRepository, IOutgoingPeerResolver[] outgoingPeerResolvers)
    {
        var logs = telemetryRepository.GetLogsForTrace(trace.TraceId);
        var json = TelemetryExportService.ConvertTraceToJson(trace, outgoingPeerResolvers, logs);
        var fileName = $"trace-{OtlpHelpers.ToShortenedId(trace.TraceId)}.json";
        return new ExportResult(json, fileName);
    }

    /// <summary>
    /// Gets a resource as a JSON export result.
    /// </summary>
    /// <param name="resource">The resource to convert.</param>
    /// <param name="resourceByName">All resources for resolving relationships and resource names.</param>
    /// <returns>A result containing the JSON representation and suggested file name.</returns>
    public static ExportResult GetResourceAsJson(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        var json = TelemetryExportService.ConvertResourceToJson(resource, resourceByName.Values.ToList());
        var fileName = $"{ResourceViewModel.GetResourceName(resource, resourceByName)}.json";
        return new ExportResult(json, fileName);
    }

    /// <summary>
    /// Gets environment variables as a .env file export result.
    /// </summary>
    /// <param name="resource">The resource containing environment variables.</param>
    /// <param name="resourceByName">All resources for resolving resource names.</param>
    /// <returns>A result containing the .env file content and suggested file name.</returns>
    public static ExportResult GetEnvironmentVariablesAsEnvFile(ResourceViewModel resource, IDictionary<string, ResourceViewModel> resourceByName)
    {
        var envContent = EnvHelpers.ConvertToEnvFormat(resource.Environment.Where(e => e.FromSpec).Select(e => new KeyValuePair<string, string?>(e.Name, e.Value)));
        var fileName = $"{ResourceViewModel.GetResourceName(resource, resourceByName)}.env";
        return new ExportResult(envContent, fileName);
    }
}
