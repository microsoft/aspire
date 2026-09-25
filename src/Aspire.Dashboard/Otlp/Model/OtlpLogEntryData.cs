// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using OpenTelemetry.Proto.Logs.V1;
using SeverityNumberProto = OpenTelemetry.Proto.Logs.V1.SeverityNumber;

namespace Aspire.Dashboard.Otlp.Model;

internal class OtlpLogEntryData
{
    internal OtlpLogEntryData(LogRecord record, OtlpResourceView resourceView, OtlpScope scope, OtlpContext context)
    {
        ResourceView = resourceView;
        Scope = scope;
        TimeStamp = ResolveTimeStamp(record);

        string? originalFormat = null;
        string? parentId = null;
        string? eventNameFromAttribute = null;
        Attributes = record.Attributes.ToKeyValuePairs(context, filter: attribute =>
        {
            switch (attribute.Key)
            {
                case "{OriginalFormat}":
                    originalFormat = attribute.Value.GetString();
                    return false;
                case "ParentId":
                    parentId = attribute.Value.GetString();
                    return false;
                case "SpanId":
                case "TraceId":
                case OtlpHelpers.AspireLogIdAttribute:
                    return false;
                case "logrecord.event.name":
                case "event.name":
                    eventNameFromAttribute ??= attribute.Value.GetString();
                    return false;
                default:
                    return true;
            }
        });

        Flags = record.Flags;
        SeverityNumber = (int)record.SeverityNumber;
        Severity = MapSeverity(record.SeverityNumber);
        Message = record.Body is { } body
            ? OtlpHelpers.TruncateString(body.GetString(), context.Options.MaxAttributeLength)
            : string.Empty;
        OriginalFormat = originalFormat;
        SpanId = record.SpanId.ToHexString();
        TraceId = record.TraceId.ToHexString();
        ParentId = parentId ?? string.Empty;
        // EventName from the LogRecord field takes precedence over the legacy attribute.
        EventName = !string.IsNullOrEmpty(record.EventName) ? record.EventName : eventNameFromAttribute;
    }

    internal DateTime TimeStamp { get; }
    internal uint Flags { get; }
    internal LogLevel Severity { get; }
    internal int SeverityNumber { get; }
    internal string Message { get; }
    internal string SpanId { get; }
    internal string TraceId { get; }
    internal string ParentId { get; }
    internal string? OriginalFormat { get; }
    internal OtlpResourceView ResourceView { get; }
    internal OtlpScope Scope { get; }
    internal KeyValuePair<string, string>[] Attributes { get; }
    internal string? EventName { get; }

    internal OtlpLogEntry CreateLogEntry(long internalId) => new(
        internalId,
        TimeStamp,
        Flags,
        Severity,
        SeverityNumber,
        Message,
        SpanId,
        TraceId,
        ParentId,
        OriginalFormat,
        ResourceView,
        Scope,
        Attributes,
        EventName);

    private static DateTime ResolveTimeStamp(LogRecord record)
    {
        // OpenTelemetry recommends using time_unix_nano when present and falling back to observed_time_unix_nano.
        // https://opentelemetry.io/docs/specs/otel/logs/data-model/#field-timestamp
        var resolvedTimeUnixNano = record.TimeUnixNano != 0 ? record.TimeUnixNano : record.ObservedTimeUnixNano;
        return OtlpHelpers.UnixNanoSecondsToDateTime(resolvedTimeUnixNano);
    }

    private static LogLevel MapSeverity(SeverityNumberProto severityNumber) => severityNumber switch
    {
        SeverityNumberProto.Trace or SeverityNumberProto.Trace2 or SeverityNumberProto.Trace3 or SeverityNumberProto.Trace4 => LogLevel.Trace,
        SeverityNumberProto.Debug or SeverityNumberProto.Debug2 or SeverityNumberProto.Debug3 or SeverityNumberProto.Debug4 => LogLevel.Debug,
        SeverityNumberProto.Info or SeverityNumberProto.Info2 or SeverityNumberProto.Info3 or SeverityNumberProto.Info4 => LogLevel.Information,
        SeverityNumberProto.Warn or SeverityNumberProto.Warn2 or SeverityNumberProto.Warn3 or SeverityNumberProto.Warn4 => LogLevel.Warning,
        SeverityNumberProto.Error or SeverityNumberProto.Error2 or SeverityNumberProto.Error3 or SeverityNumberProto.Error4 => LogLevel.Error,
        SeverityNumberProto.Fatal or SeverityNumberProto.Fatal2 or SeverityNumberProto.Fatal3 or SeverityNumberProto.Fatal4 => LogLevel.Critical,
        _ => LogLevel.None
    };
}