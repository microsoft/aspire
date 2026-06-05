// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Model.Otlp;
using OpenTelemetry.Proto.Logs.V1;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("InternalId = {InternalId}, TimeStamp = {TimeStamp}, Severity = {Severity}, Message = {Message}")]
public class OtlpLogEntry
{
    private static long s_nextLogEntryId;

    public KeyValuePair<string, string>[] Attributes { get; }
    public DateTime TimeStamp { get; }
    public uint Flags { get; }
    public LogLevel Severity { get; }
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

    private static LogLevel MapSeverity(SeverityNumber severityNumber) => severityNumber switch
    {
        SeverityNumber.Trace => LogLevel.Trace,
        SeverityNumber.Trace2 => LogLevel.Trace,
        SeverityNumber.Trace3 => LogLevel.Trace,
        SeverityNumber.Trace4 => LogLevel.Trace,
        SeverityNumber.Debug => LogLevel.Debug,
        SeverityNumber.Debug2 => LogLevel.Debug,
        SeverityNumber.Debug3 => LogLevel.Debug,
        SeverityNumber.Debug4 => LogLevel.Debug,
        SeverityNumber.Info => LogLevel.Information,
        SeverityNumber.Info2 => LogLevel.Information,
        SeverityNumber.Info3 => LogLevel.Information,
        SeverityNumber.Info4 => LogLevel.Information,
        SeverityNumber.Warn => LogLevel.Warning,
        SeverityNumber.Warn2 => LogLevel.Warning,
        SeverityNumber.Warn3 => LogLevel.Warning,
        SeverityNumber.Warn4 => LogLevel.Warning,
        SeverityNumber.Error => LogLevel.Error,
        SeverityNumber.Error2 => LogLevel.Error,
        SeverityNumber.Error3 => LogLevel.Error,
        SeverityNumber.Error4 => LogLevel.Error,
        SeverityNumber.Fatal => LogLevel.Critical,
        SeverityNumber.Fatal2 => LogLevel.Critical,
        SeverityNumber.Fatal3 => LogLevel.Critical,
        SeverityNumber.Fatal4 => LogLevel.Critical,
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
