// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Model.Otlp;
using OpenTelemetry.Proto.Logs.V1;
using SeverityNumberProto = OpenTelemetry.Proto.Logs.V1.SeverityNumber;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("InternalId = {InternalId}, TimeStamp = {TimeStamp}, Severity = {Severity}, Message = {Message}")]
public class OtlpLogEntry
{
    private static long s_nextLogEntryId;

    public KeyValuePair<string, string>[] Attributes { get; }
    public DateTime TimeStamp { get; }
    public uint Flags { get; }
    public LogLevel Severity { get; }
    public int SeverityNumber { get; }
    public string Message { get; }
    public string SpanId { get; }
    public string TraceId { get; }
    public string ParentId { get; }
    public string? OriginalFormat { get; }
    public OtlpResourceView ResourceView { get; }
    public OtlpScope Scope { get; }
    public long InternalId { get; }
    public string? EventName { get; }
    public bool IsError => Severity is LogLevel.Error or LogLevel.Critical;
    public bool IsWarning => Severity is LogLevel.Warning;

    public OtlpLogEntry(LogRecord record, OtlpResourceView resourceView, OtlpScope scope, OtlpContext context)
    {
        InternalId = Interlocked.Increment(ref s_nextLogEntryId);
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
                    // Explicitly ignore these
                    return false;
                case "logrecord.event.name":
                case "event.name":
                    // Capture the attribute value for fallback, but filter it out
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
        ResourceView = resourceView;
        Scope = scope;

        // EventName from the LogRecord field takes precedence over the legacy attribute
        EventName = !string.IsNullOrEmpty(record.EventName) ? record.EventName : eventNameFromAttribute;
    }

    private static DateTime ResolveTimeStamp(LogRecord record)
    {
        // From proto docs:
        //
        // For converting OpenTelemetry log data to formats that support only one timestamp or
        // when receiving OpenTelemetry log data by recipients that support only one timestamp
        // internally the following logic is recommended:
        //   - Use time_unix_nano if it is present, otherwise use observed_time_unix_nano.
        var resolvedTimeUnixNano = record.TimeUnixNano != 0 ? record.TimeUnixNano : record.ObservedTimeUnixNano;

        return OtlpHelpers.UnixNanoSecondsToDateTime(resolvedTimeUnixNano);
    }

    private static LogLevel MapSeverity(SeverityNumberProto severityNumber) => severityNumber switch
    {
        SeverityNumberProto.Trace => LogLevel.Trace,
        SeverityNumberProto.Trace2 => LogLevel.Trace,
        SeverityNumberProto.Trace3 => LogLevel.Trace,
        SeverityNumberProto.Trace4 => LogLevel.Trace,
        SeverityNumberProto.Debug => LogLevel.Debug,
        SeverityNumberProto.Debug2 => LogLevel.Debug,
        SeverityNumberProto.Debug3 => LogLevel.Debug,
        SeverityNumberProto.Debug4 => LogLevel.Debug,
        SeverityNumberProto.Info => LogLevel.Information,
        SeverityNumberProto.Info2 => LogLevel.Information,
        SeverityNumberProto.Info3 => LogLevel.Information,
        SeverityNumberProto.Info4 => LogLevel.Information,
        SeverityNumberProto.Warn => LogLevel.Warning,
        SeverityNumberProto.Warn2 => LogLevel.Warning,
        SeverityNumberProto.Warn3 => LogLevel.Warning,
        SeverityNumberProto.Warn4 => LogLevel.Warning,
        SeverityNumberProto.Error => LogLevel.Error,
        SeverityNumberProto.Error2 => LogLevel.Error,
        SeverityNumberProto.Error3 => LogLevel.Error,
        SeverityNumberProto.Error4 => LogLevel.Error,
        SeverityNumberProto.Fatal => LogLevel.Critical,
        SeverityNumberProto.Fatal2 => LogLevel.Critical,
        SeverityNumberProto.Fatal3 => LogLevel.Critical,
        SeverityNumberProto.Fatal4 => LogLevel.Critical,
        _ => LogLevel.None
    };

    public static string? GetFieldValue(OtlpLogEntry log, string field)
    {
        return field switch
        {
            KnownStructuredLogFields.MessageField => log.Message,
            KnownStructuredLogFields.TraceIdField => log.TraceId,
            KnownStructuredLogFields.SpanIdField => log.SpanId,
            KnownStructuredLogFields.OriginalFormatField => log.OriginalFormat,
            KnownStructuredLogFields.CategoryField => log.Scope.Name,
            KnownStructuredLogFields.EventNameField => log.EventName,
            KnownStructuredLogFields.LevelField => log.Severity.ToString(),
            KnownResourceFields.ServiceNameField => log.ResourceView.Resource.ResourceName,
            _ => log.Attributes.GetValue(field)
        };
    }

    public const string ExceptionStackTraceField = "exception.stacktrace";
    public const string ExceptionMessageField = "exception.message";
    public const string ExceptionTypeField = "exception.type";

    public static string? GetExceptionText(OtlpLogEntry logEntry)
    {
        // exception.stacktrace includes the exception message and type.
        // https://opentelemetry.io/docs/specs/semconv/attributes-registry/exception/
        if (GetProperty(logEntry, ExceptionStackTraceField) is { Length: > 0 } stackTrace)
        {
            return stackTrace;
        }

        if (GetProperty(logEntry, ExceptionMessageField) is { Length: > 0 } message)
        {
            if (GetProperty(logEntry, ExceptionTypeField) is { Length: > 0 } type)
            {
                return $"{type}: {message}";
            }

            return message;
        }

        return null;

        static string? GetProperty(OtlpLogEntry logEntry, string propertyName)
        {
            return logEntry.Attributes.GetValue(propertyName);
        }
    }
}
