// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Aspire.Dashboard.Model.Otlp;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("InternalId = {InternalId}, TimeStamp = {TimeStamp}, Severity = {Severity}, Message = {Message}")]
public class OtlpLogEntry
{
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

    internal OtlpLogEntry(
        long internalId,
        DateTime timeStamp,
        uint flags,
        LogLevel severity,
        int severityNumber,
        string message,
        string spanId,
        string traceId,
        string parentId,
        string? originalFormat,
        OtlpResourceView resourceView,
        OtlpScope scope,
        KeyValuePair<string, string>[] attributes,
        string? eventName)
    {
        InternalId = internalId;
        TimeStamp = timeStamp;
        Flags = flags;
        Severity = severity;
        SeverityNumber = severityNumber;
        Message = message;
        SpanId = spanId;
        TraceId = traceId;
        ParentId = parentId;
        OriginalFormat = originalFormat;
        ResourceView = resourceView;
        Scope = scope;
        Attributes = attributes;
        EventName = eventName;
    }

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
            KnownStructuredLogFields.TimestampField => (log.TimeStamp.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture),
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
