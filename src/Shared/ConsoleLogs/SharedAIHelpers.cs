// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;

namespace Aspire.Shared.ConsoleLogs;

/// <summary>
/// Shared AI helper methods for console log processing.
/// Used by both Dashboard and CLI.
/// </summary>
internal static class SharedAIHelpers
{
    public const int TracesLimit = 200;
    public const int StructuredLogsLimit = 200;
    public const int ConsoleLogsLimit = 500;
    public const int MaximumListTokenLength = 8192;
    public const int MaximumStringLength = 2048;

    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Estimates the token count for a string.
    /// This is a rough estimate - use a library for exact calculation.
    /// </summary>
    public static int EstimateTokenCount(string text)
    {
        return text.Length / 4;
    }

    /// <summary>
    /// Estimates the serialized JSON token size for a JsonNode.
    /// </summary>
    public static int EstimateSerializedJsonTokenSize(JsonNode node)
    {
        var json = node.ToJsonString(s_jsonSerializerOptions);
        return EstimateTokenCount(json);
    }

    /// <summary>
    /// Converts OTLP resource logs to structured logs JSON for AI processing.
    /// </summary>
    /// <param name="resourceLogs">The OTLP resource logs containing log records.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A tuple containing the JSON string and a limit message.</returns>
    public static (string json, string limitMessage) GetStructuredLogsJson(
        IList<OtlpResourceLogsJson>? resourceLogs,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var logRecords = GetLogRecordsFromOtlpData(resourceLogs);
        var promptContext = new PromptContext();

        var (trimmedItems, limitMessage) = GetLimitFromEndWithSummary(
            logRecords,
            StructuredLogsLimit,
            "log entry",
            "log entries",
            i => GetLogEntryDto(i, promptContext, getResourceName, dashboardBaseUrl),
            EstimateSerializedJsonTokenSize);

        var jsonArray = new JsonArray(trimmedItems.ToArray());
        var logsData = jsonArray.ToJsonString(s_jsonSerializerOptions);

        return (logsData, limitMessage);
    }

    /// <summary>
    /// Converts OTLP resource logs to a single structured log JSON for AI processing.
    /// </summary>
    /// <param name="resourceLogs">The OTLP resource logs containing log records.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>The JSON string for the first log entry.</returns>
    public static string GetStructuredLogJson(
        IList<OtlpResourceLogsJson>? resourceLogs,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var logRecords = GetLogRecordsFromOtlpData(resourceLogs);
        var logEntry = logRecords.FirstOrDefault() ?? throw new InvalidOperationException("No log entry found in OTLP data.");
        var promptContext = new PromptContext();
        var dto = GetLogEntryDto(logEntry, promptContext, getResourceName, dashboardBaseUrl);

        return dto.ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Converts OTLP resource spans to traces JSON for AI processing.
    /// </summary>
    /// <param name="resourceSpans">The OTLP resource spans containing trace data.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A tuple containing the JSON string and a limit message.</returns>
    public static (string json, string limitMessage) GetTracesJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var traces = GetTracesFromOtlpData(resourceSpans);
        var promptContext = new PromptContext();

        var (trimmedItems, limitMessage) = GetLimitFromEndWithSummary(
            traces,
            TracesLimit,
            "trace",
            "traces",
            t => GetTraceDto(t, promptContext, getResourceName, dashboardBaseUrl),
            EstimateSerializedJsonTokenSize);

        var jsonArray = new JsonArray(trimmedItems.ToArray());
        var tracesData = jsonArray.ToJsonString(s_jsonSerializerOptions);

        return (tracesData, limitMessage);
    }

    /// <summary>
    /// Converts OTLP resource spans to a single trace JSON for AI processing.
    /// </summary>
    /// <param name="resourceSpans">The OTLP resource spans containing trace data.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>The JSON string for the first trace.</returns>
    public static string GetTraceJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var traces = GetTracesFromOtlpData(resourceSpans);
        var trace = traces.FirstOrDefault() ?? throw new InvalidOperationException("No trace found in OTLP data.");
        var promptContext = new PromptContext();
        var dto = GetTraceDto(trace, promptContext, getResourceName, dashboardBaseUrl);

        return dto.ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Extracts traces from OTLP resource spans, grouping spans by trace ID.
    /// </summary>
    public static List<OtlpTraceDto> GetTracesFromOtlpData(IList<OtlpResourceSpansJson>? resourceSpans)
    {
        var spansByTraceId = new Dictionary<string, List<OtlpSpanDto>>(StringComparer.Ordinal);

        if (resourceSpans is null)
        {
            return [];
        }

        foreach (var resourceSpan in resourceSpans)
        {
            var resource = CreateResourceFromOtlpJson(resourceSpan.Resource);

            if (resourceSpan.ScopeSpans is null)
            {
                continue;
            }

            foreach (var scopeSpan in resourceSpan.ScopeSpans)
            {
                var scopeName = scopeSpan.Scope?.Name;

                if (scopeSpan.Spans is null)
                {
                    continue;
                }

                foreach (var span in scopeSpan.Spans)
                {
                    var traceId = span.TraceId ?? string.Empty;
                    if (!spansByTraceId.TryGetValue(traceId, out var spanList))
                    {
                        spanList = [];
                        spansByTraceId[traceId] = spanList;
                    }

                    spanList.Add(new OtlpSpanDto(span, resource, scopeName));
                }
            }
        }

        return spansByTraceId
            .Select(kvp => new OtlpTraceDto(kvp.Key, kvp.Value))
            .ToList();
    }

    /// <summary>
    /// Creates a JsonObject representing a trace for AI processing.
    /// </summary>
    /// <param name="trace">The trace DTO to convert.</param>
    /// <param name="context">The prompt context for tracking duplicate values.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A JsonObject containing the trace data.</returns>
    public static JsonObject GetTraceDto(
        OtlpTraceDto trace,
        PromptContext context,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var spanObjects = new List<JsonNode>();
        foreach (var s in trace.Spans)
        {
            var span = s.Span;
            var spanId = span.SpanId ?? string.Empty;

            var attributesObj = new JsonObject();
            if (span.Attributes is not null)
            {
                foreach (var attr in span.Attributes.Where(a => a.Key != OtlpHelpers.AspireDestinationNameAttribute))
                {
                    var attrValue = MapOtelAttributeValue(attr);
                    attributesObj[attr.Key!] = context.AddValue(attrValue, id => $@"Duplicate of attribute ""{id.Key}"" for span {OtlpHelpers.ToShortenedId(id.SpanId)}", (SpanId: spanId, attr.Key));
                }
            }

            JsonArray? linksArray = null;
            if (span.Links is { Length: > 0 })
            {
                var linkObjects = span.Links.Select(link => (JsonNode)new JsonObject
                {
                    ["trace_id"] = OtlpHelpers.ToShortenedId(link.TraceId ?? string.Empty),
                    ["span_id"] = OtlpHelpers.ToShortenedId(link.SpanId ?? string.Empty)
                }).ToArray();
                linksArray = new JsonArray(linkObjects);
            }

            var resourceName = getResourceName?.Invoke(s.Resource) ?? s.Resource.ResourceName;
            var destination = GetAttributeStringValue(span.Attributes, OtlpHelpers.AspireDestinationNameAttribute);
            var statusCode = span.Status?.Code;
            var statusText = statusCode switch
            {
                1 => "Ok",
                2 => "Error",
                _ => null
            };

            var spanObj = new JsonObject
            {
                ["span_id"] = OtlpHelpers.ToShortenedId(spanId),
                ["parent_span_id"] = span.ParentSpanId is { } id ? OtlpHelpers.ToShortenedId(id) : null,
                ["kind"] = GetSpanKindName(span.Kind),
                ["name"] = context.AddValue(span.Name, sId => $@"Duplicate of ""name"" for span {OtlpHelpers.ToShortenedId(sId)}", spanId),
                ["status"] = statusText,
                ["status_message"] = context.AddValue(span.Status?.Message, sId => $@"Duplicate of ""status_message"" for span {OtlpHelpers.ToShortenedId(sId)}", spanId),
                ["source"] = resourceName,
                ["destination"] = destination,
                ["duration_ms"] = CalculateDurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
                ["attributes"] = attributesObj,
                ["links"] = linksArray
            };
            spanObjects.Add(spanObj);
        }

        var spanArray = new JsonArray(spanObjects.ToArray());
        var traceId = OtlpHelpers.ToShortenedId(trace.TraceId);
        var rootSpan = trace.Spans.FirstOrDefault(s => string.IsNullOrEmpty(s.Span.ParentSpanId)) ?? trace.Spans.FirstOrDefault();
        var hasError = trace.Spans.Any(s => s.Span.Status?.Code == 2);
        var timestamp = rootSpan?.Span.StartTimeUnixNano is { } startNano
            ? OtlpHelpers.UnixNanoSecondsToDateTime(startNano)
            : (DateTime?)null;

        var traceData = new JsonObject
        {
            ["trace_id"] = traceId,
            ["duration_ms"] = CalculateTraceDurationMs(trace.Spans),
            ["title"] = rootSpan?.Span.Name,
            ["spans"] = spanArray,
            ["has_error"] = hasError,
            ["timestamp"] = timestamp
        };

        if (dashboardBaseUrl is not null)
        {
            traceData["dashboard_link"] = GetDashboardLinkObject(dashboardBaseUrl, DashboardUrls.TraceDetailUrl(traceId), traceId);
        }

        return traceData;
    }

    private static string MapOtelAttributeValue(OtlpKeyValueJson attribute)
    {
        var key = attribute.Key;
        var value = GetAttributeValue(attribute);

        switch (key)
        {
            case "http.response.status_code":
                {
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var intValue))
                    {
                        return GetHttpStatusName(intValue);
                    }
                    goto default;
                }
            case "rpc.grpc.status_code":
                {
                    if (int.TryParse(value, CultureInfo.InvariantCulture, out var intValue))
                    {
                        return GetGrpcStatusName(intValue);
                    }
                    goto default;
                }
            default:
                return value;
        }
    }

    private static string GetHttpStatusName(int statusCode)
    {
        return statusCode switch
        {
            200 => "200 OK",
            201 => "201 Created",
            204 => "204 No Content",
            301 => "301 Moved Permanently",
            302 => "302 Found",
            304 => "304 Not Modified",
            400 => "400 Bad Request",
            401 => "401 Unauthorized",
            403 => "403 Forbidden",
            404 => "404 Not Found",
            405 => "405 Method Not Allowed",
            408 => "408 Request Timeout",
            409 => "409 Conflict",
            422 => "422 Unprocessable Entity",
            429 => "429 Too Many Requests",
            500 => "500 Internal Server Error",
            501 => "501 Not Implemented",
            502 => "502 Bad Gateway",
            503 => "503 Service Unavailable",
            504 => "504 Gateway Timeout",
            _ => statusCode.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string GetGrpcStatusName(int statusCode)
    {
        return statusCode switch
        {
            0 => "OK",
            1 => "CANCELLED",
            2 => "UNKNOWN",
            3 => "INVALID_ARGUMENT",
            4 => "DEADLINE_EXCEEDED",
            5 => "NOT_FOUND",
            6 => "ALREADY_EXISTS",
            7 => "PERMISSION_DENIED",
            8 => "RESOURCE_EXHAUSTED",
            9 => "FAILED_PRECONDITION",
            10 => "ABORTED",
            11 => "OUT_OF_RANGE",
            12 => "UNIMPLEMENTED",
            13 => "INTERNAL",
            14 => "UNAVAILABLE",
            15 => "DATA_LOSS",
            16 => "UNAUTHENTICATED",
            _ => statusCode.ToString(CultureInfo.InvariantCulture)
        };
    }

    private static string? GetSpanKindName(int? kind)
    {
        return kind switch
        {
            1 => "Internal",
            2 => "Server",
            3 => "Client",
            4 => "Producer",
            5 => "Consumer",
            _ => null
        };
    }

    private static int? CalculateDurationMs(ulong? startTimeUnixNano, ulong? endTimeUnixNano)
    {
        if (startTimeUnixNano is null || endTimeUnixNano is null)
        {
            return null;
        }

        var durationNano = endTimeUnixNano.Value - startTimeUnixNano.Value;
        return (int)Math.Round(durationNano / 1_000_000.0, 0, MidpointRounding.AwayFromZero);
    }

    private static int? CalculateTraceDurationMs(List<OtlpSpanDto> spans)
    {
        if (spans.Count == 0)
        {
            return null;
        }

        ulong? minStart = null;
        ulong? maxEnd = null;

        foreach (var s in spans)
        {
            if (s.Span.StartTimeUnixNano is { } start)
            {
                minStart = minStart is null ? start : Math.Min(minStart.Value, start);
            }
            if (s.Span.EndTimeUnixNano is { } end)
            {
                maxEnd = maxEnd is null ? end : Math.Max(maxEnd.Value, end);
            }
        }

        return CalculateDurationMs(minStart, maxEnd);
    }

    /// <summary>
    /// Extracts log records from OTLP resource logs.
    /// </summary>
    public static List<OtlpLogEntryDto> GetLogRecordsFromOtlpData(IList<OtlpResourceLogsJson>? resourceLogs)
    {
        var logRecords = new List<OtlpLogEntryDto>();

        if (resourceLogs is null)
        {
            return logRecords;
        }

        foreach (var resourceLog in resourceLogs)
        {
            var resource = CreateResourceFromOtlpJson(resourceLog.Resource);

            if (resourceLog.ScopeLogs is null)
            {
                continue;
            }

            foreach (var scopeLogs in resourceLog.ScopeLogs)
            {
                var scopeName = scopeLogs.Scope?.Name;

                if (scopeLogs.LogRecords is null)
                {
                    continue;
                }

                foreach (var logRecord in scopeLogs.LogRecords)
                {
                    logRecords.Add(new OtlpLogEntryDto(logRecord, resource, scopeName));
                }
            }
        }

        return logRecords;
    }

    /// <summary>
    /// Gets the message from a log record.
    /// </summary>
    public static string? GetLogMessage(OtlpLogRecordJson logRecord)
    {
        return logRecord.Body?.StringValue;
    }

    /// <summary>
    /// Gets the attribute value as a string.
    /// </summary>
    public static string GetAttributeValue(OtlpKeyValueJson attribute)
    {
        if (attribute.Value is null)
        {
            return string.Empty;
        }

        return attribute.Value.StringValue
            ?? attribute.Value.IntValue?.ToString(CultureInfo.InvariantCulture)
            ?? attribute.Value.DoubleValue?.ToString(CultureInfo.InvariantCulture)
            ?? attribute.Value.BoolValue?.ToString(CultureInfo.InvariantCulture)
            ?? string.Empty;
    }

    /// <summary>
    /// Gets the value of an attribute by key as a string, or null if not found.
    /// </summary>
    public static string? GetAttributeStringValue(OtlpKeyValueJson[]? attributes, string key)
    {
        if (attributes is null)
        {
            return null;
        }

        foreach (var attr in attributes)
        {
            if (attr.Key == key)
            {
                var value = GetAttributeValue(attr);
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a SimpleOtlpResource from OTLP resource JSON.
    /// </summary>
    /// <param name="resource">The OTLP resource JSON, or null.</param>
    /// <returns>A SimpleOtlpResource with the service name and instance ID extracted from attributes.</returns>
    private static SimpleOtlpResource CreateResourceFromOtlpJson(OtlpResourceJson? resource)
    {
        var serviceName = GetAttributeStringValue(resource?.Attributes, "service.name");
        var serviceInstanceId = GetAttributeStringValue(resource?.Attributes, "service.instance.id");
        var resourceName = serviceName ?? "Unknown";
        return new SimpleOtlpResource(resourceName, serviceInstanceId);
    }

    private const string ExceptionStackTraceField = "exception.stacktrace";
    private const string ExceptionMessageField = "exception.message";
    private const string ExceptionTypeField = "exception.type";

    /// <summary>
    /// Filters out exception-related attributes and internal Aspire attributes from the attributes list.
    /// </summary>
    public static IEnumerable<OtlpKeyValueJson> GetFilteredAttributes(OtlpKeyValueJson[]? attributes)
    {
        if (attributes is null)
        {
            return [];
        }

        return attributes.Where(a => a.Key is not (ExceptionStackTraceField or ExceptionMessageField or ExceptionTypeField or OtlpHelpers.AspireLogIdAttribute));
    }

    /// <summary>
    /// Gets the exception text from a log entry's attributes.
    /// </summary>
    public static string? GetExceptionText(OtlpLogEntryDto logEntry)
    {
        var stackTrace = GetAttributeStringValue(logEntry.LogRecord.Attributes, ExceptionStackTraceField);
        if (!string.IsNullOrEmpty(stackTrace))
        {
            return stackTrace;
        }

        var message = GetAttributeStringValue(logEntry.LogRecord.Attributes, ExceptionMessageField);
        if (!string.IsNullOrEmpty(message))
        {
            var type = GetAttributeStringValue(logEntry.LogRecord.Attributes, ExceptionTypeField);
            if (!string.IsNullOrEmpty(type))
            {
                return $"{type}: {message}";
            }

            return message;
        }

        return null;
    }

    /// <summary>
    /// Creates a JsonObject representing a log entry for AI processing.
    /// </summary>
    /// <param name="logEntry">The log entry to convert.</param>
    /// <param name="context">The prompt context for tracking duplicate values.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A JsonObject containing the log entry data.</returns>
    public static JsonObject GetLogEntryDto(
        OtlpLogEntryDto logEntry,
        PromptContext context,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var exceptionText = GetExceptionText(logEntry);
        var logIdString = GetAttributeStringValue(logEntry.LogRecord.Attributes, OtlpHelpers.AspireLogIdAttribute);
        var logId = long.TryParse(logIdString, CultureInfo.InvariantCulture, out var parsedLogId) ? parsedLogId : (long?)null;
        var resourceName = getResourceName?.Invoke(logEntry.Resource) ?? logEntry.Resource.ResourceName;

        var attributesObject = new JsonObject();
        foreach (var attr in GetFilteredAttributes(logEntry.LogRecord.Attributes))
        {
            var attrValue = GetAttributeValue(attr);
            attributesObject[attr.Key!] = context.AddValue(attrValue, id => $@"Duplicate of attribute ""{id.Key}"" for log entry {id.LogId}", (LogId: logId, attr.Key));
        }

        var message = GetLogMessage(logEntry.LogRecord) ?? string.Empty;
        var log = new JsonObject
        {
            ["log_id"] = logId,
            ["span_id"] = OtlpHelpers.ToShortenedId(logEntry.LogRecord.SpanId ?? string.Empty),
            ["trace_id"] = OtlpHelpers.ToShortenedId(logEntry.LogRecord.TraceId ?? string.Empty),
            ["message"] = context.AddValue(message, id => $@"Duplicate of ""message"" for log entry {id}", logId),
            ["severity"] = logEntry.LogRecord.SeverityText ?? "Unknown",
            ["resource_name"] = resourceName,
            ["attributes"] = attributesObject,
            ["exception"] = context.AddValue(exceptionText, id => $@"Duplicate of ""exception"" for log entry {id}", logId),
            ["source"] = logEntry.ScopeName
        };

        if (dashboardBaseUrl is not null && logId is not null)
        {
            log["dashboard_link"] = GetDashboardLinkObject(dashboardBaseUrl, DashboardUrls.StructuredLogsUrl(logEntryId: logId), $"log_id: {logId}");
        }

        return log;
    }

    public static JsonObject? GetDashboardLinkObject(string dashboardBaseUrl, string path, string text)
    {
        return new JsonObject
        {
            ["url"] = DashboardUrls.CombineUrl(dashboardBaseUrl, path),
            ["text"] = text
        };
    }

    /// <summary>
    /// Serializes a log entry to a string, stripping timestamps and ANSI control sequences.
    /// </summary>
    public static string SerializeLogEntry(LogEntry logEntry)
    {
        if (logEntry.RawContent is not null)
        {
            var content = logEntry.RawContent;
            if (TimestampParser.TryParseConsoleTimestamp(content, out var timestampParseResult))
            {
                content = timestampParseResult.Value.ModifiedText;
            }

            return LimitLength(AnsiParser.StripControlSequences(content));
        }
        else
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Serializes a list of log entry strings to a single string with newlines.
    /// </summary>
    public static string SerializeConsoleLogs(IList<string> logEntries)
    {
        var consoleLogsText = new StringBuilder();

        foreach (var logEntry in logEntries)
        {
            consoleLogsText.AppendLine(logEntry);
        }

        return consoleLogsText.ToString();
    }

    /// <summary>
    /// Limits a string to the maximum length, appending a truncation marker if needed.
    /// </summary>
    public static string LimitLength(string value)
    {
        if (value.Length <= MaximumStringLength)
        {
            return value;
        }

        return
            $"""
            {value.AsSpan(0, MaximumStringLength)}...[TRUNCATED]
            """;
    }

    /// <summary>
    /// Gets items from the end of a list with a summary message, applying count and token limits.
    /// </summary>
    public static (List<TResult> items, string message) GetLimitFromEndWithSummary<T, TResult>(
        List<T> values,
        int limit,
        string itemName,
        string pluralItemName,
        Func<T, TResult> convertToDto,
        Func<TResult, int> estimateTokenSize)
    {
        return GetLimitFromEndWithSummary(values, values.Count, limit, itemName, pluralItemName, convertToDto, estimateTokenSize);
    }

    /// <summary>
    /// Gets items from the end of a list with a summary message, applying count and token limits.
    /// </summary>
    public static (List<TResult> items, string message) GetLimitFromEndWithSummary<T, TResult>(
        List<T> values,
        int totalValues,
        int limit,
        string itemName,
        string pluralItemName,
        Func<T, TResult> convertToDto,
        Func<TResult, int> estimateTokenSize)
    {
        Debug.Assert(totalValues >= values.Count, "Total values should be large or equal to the values passed into the method.");

        var trimmedItems = values.Count <= limit
            ? values
            : values[^limit..];

        var currentTokenCount = 0;
        var serializedValuesCount = 0;
        var dtos = trimmedItems.Select(i => convertToDto(i)).ToList();

        // Loop backwards to prioritize the latest items.
        for (var i = dtos.Count - 1; i >= 0; i--)
        {
            var obj = dtos[i];
            var tokenCount = estimateTokenSize(obj);

            if (currentTokenCount + tokenCount > MaximumListTokenLength)
            {
                break;
            }

            serializedValuesCount++;
            currentTokenCount += tokenCount;
        }

        // Trim again with what fits in the token limit.
        dtos = dtos[^serializedValuesCount..];

        return (dtos, GetLimitSummary(totalValues, dtos.Count, itemName, pluralItemName));
    }

    /// <summary>
    /// Gets a summary message describing how many items were returned vs total.
    /// </summary>
    public static string GetLimitSummary(int totalValues, int returnedCount, string itemName, string pluralItemName)
    {
        if (totalValues == returnedCount)
        {
            return $"Returned {ToQuantity(returnedCount, itemName, pluralItemName)}.";
        }

        return $"Returned latest {ToQuantity(returnedCount, itemName, pluralItemName)}. Earlier {ToQuantity(totalValues - returnedCount, itemName, pluralItemName)} not returned because of size limits.";
    }

    /// <summary>
    /// Formats an item name with quantity (e.g., "1 console log" or "5 console logs").
    /// </summary>
    private static string ToQuantity(int count, string itemName, string pluralItemName)
    {
        var name = count == 1 ? itemName : pluralItemName;
        return string.Create(CultureInfo.InvariantCulture, $"{count} {name}");
    }
}

/// <summary>
/// Represents a log entry extracted from OTLP JSON format for AI processing.
/// </summary>
/// <param name="LogRecord">The OTLP log record JSON data.</param>
/// <param name="Resource">The resource information from the resource attributes.</param>
/// <param name="ScopeName">The instrumentation scope name.</param>
internal sealed record OtlpLogEntryDto(OtlpLogRecordJson LogRecord, IOtlpResource Resource, string? ScopeName);

/// <summary>
/// Represents a trace (collection of spans with the same trace ID) extracted from OTLP JSON format.
/// </summary>
/// <param name="TraceId">The trace ID.</param>
/// <param name="Spans">The spans belonging to this trace.</param>
internal sealed record OtlpTraceDto(string TraceId, List<OtlpSpanDto> Spans);

/// <summary>
/// Represents a span extracted from OTLP JSON format for AI processing.
/// </summary>
/// <param name="Span">The OTLP span JSON data.</param>
/// <param name="Resource">The resource information from the resource attributes.</param>
/// <param name="ScopeName">The instrumentation scope name.</param>
internal sealed record OtlpSpanDto(OtlpSpanJson Span, IOtlpResource Resource, string? ScopeName);
