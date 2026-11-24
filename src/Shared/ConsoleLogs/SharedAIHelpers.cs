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
    public const int TracesLimit = 800;
    public const int StructuredLogsLimit = 800;
    public const int ConsoleLogsLimit = 2000;
    public const int MaximumListTokenLength = 8192;

    /// <summary>
    /// Token budget for compact list responses, such as the default MCP telemetry tool responses.
    /// </summary>
    /// <remarks>
    /// Agent harnesses commonly replace tool results larger than about 20 KB with a pointer to a
    /// spill file (for example, Copilot CLI writes them to disk and shows only the first 500 characters),
    /// so the model reads the data through extra file reads instead of the tool result.
    /// <see cref="EstimateTokenCount"/> uses four UTF-8 bytes per token, so this keeps the list itself
    /// at about 16 KB and leaves room for the response header.
    /// See https://github.com/microsoft/aspire/issues/22507.
    /// </remarks>
    public const int CompactListTokenLength = 4096;

    public const int MaximumStringLength = 2048;

    // Summary strings and shortened attribute values are bounded by their serialized size in UTF-8 bytes rather than
    // by character count, because one character can be written as up to six bytes (a control character is escaped as
    // "\u0001") and the response budget is in serialized bytes.
    private const int MaximumSummaryStringLength = 512;
    private const int MaximumSummaryResourceCount = 16;
    private const int MaximumSummaryResourceNameLength = 128;

    private const string TruncatedMarker = "...[TRUNCATED]";

    internal static readonly JsonSerializerOptions s_jsonSerializerOptions = new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Estimates the token count for a string.
    /// This is a rough estimate - use a library for exact calculation.
    /// </summary>
    /// <remarks>
    /// The estimate counts UTF-8 bytes rather than UTF-16 characters: responses are written as UTF-8, and token budgets
    /// exist to keep that output under a byte size. Counting characters would let non-ASCII text, such as CJK characters
    /// that take three bytes each, produce roughly three times the intended output.
    /// </remarks>
    public static int EstimateTokenCount(string text)
    {
        return Encoding.UTF8.GetByteCount(text) / 4;
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
    /// <param name="limit">The maximum number of log entries to return.</param>
    /// <param name="maximumTokenLength">The estimated token budget for the returned log entries.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A tuple containing the JSON string and a limit message.</returns>
    public static (string json, string limitMessage) GetStructuredLogsJson(
        IList<OtlpResourceLogsJson>? resourceLogs,
        int limit,
        int maximumTokenLength,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        // GetLimitFromEndWithSummary keeps the last items, but OTLP data is grouped by resource and scope,
        // so interleaved records from different resources are not in time order. Sort them so the limit keeps
        // the newest entries. This is done here rather than in GetLogRecordsFromOtlpData so the CLI output order
        // of other callers is unchanged. OrderBy is stable, so records with equal timestamps keep their order.
        var logRecords = GetLogRecordsFromOtlpData(resourceLogs)
            .OrderBy(GetLogTimestampUnixNano)
            .ToList();
        var promptContext = new PromptContext();

        // GetLimitFromEndWithSummary stops at the first entry that doesn't fit, so a single newest log with large
        // content would otherwise hide every log. Bound the serialized size of each entry's attributes, message and
        // exception to a quarter of the budget each: the budget is in tokens and EstimateTokenCount counts four UTF-8
        // bytes per token, so maximumTokenLength bytes is a quarter of it. The remaining quarter is left for the other,
        // short fields, so a bounded entry always fits.
        var maximumFieldLength = maximumTokenLength;

        var (trimmedItems, limitMessage) = GetLimitFromEndWithSummary(
            logRecords,
            logRecords.Count,
            limit,
            maximumTokenLength,
            "log entry",
            "log entries",
            i => GetLogEntryDto(i, promptContext, getResourceName, dashboardBaseUrl, maximumFieldLength),
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
    /// <param name="limit">The maximum number of traces to return.</param>
    /// <param name="maximumTokenLength">The estimated token budget for the returned traces.</param>
    /// <param name="getResourceName">Optional function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A tuple containing the JSON string and a limit message.</returns>
    public static (string json, string limitMessage) GetTracesJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        int limit,
        int maximumTokenLength,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var traces = GetTracesInStartOrder(resourceSpans);
        var promptContext = new PromptContext();

        var (trimmedItems, limitMessage) = GetLimitFromEndWithSummary(
            traces,
            traces.Count,
            limit,
            maximumTokenLength,
            "trace",
            "traces",
            t => GetTraceDto(t, promptContext, getResourceName, dashboardBaseUrl),
            EstimateSerializedJsonTokenSize);

        var jsonArray = new JsonArray(trimmedItems.ToArray());
        var tracesData = jsonArray.ToJsonString(s_jsonSerializerOptions);

        return (tracesData, limitMessage);
    }

    /// <summary>
    /// Converts OTLP resource spans to one compact summary per trace for AI processing.
    /// Each summary names the root span, the slowest span and the first failed span instead of listing every span.
    /// </summary>
    /// <param name="resourceSpans">The OTLP resource spans containing trace data.</param>
    /// <param name="limit">The maximum number of traces to return.</param>
    /// <param name="getResourceName">Function to resolve resource names.</param>
    /// <param name="dashboardBaseUrl">Optional dashboard URL.</param>
    /// <returns>A tuple containing the JSON string and a limit message.</returns>
    public static (string json, string limitMessage) GetTraceSummariesJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        int limit,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var traces = GetTracesInStartOrder(resourceSpans);

        var (trimmedItems, limitMessage) = GetLimitFromEndWithSummary(
            traces,
            traces.Count,
            limit,
            CompactListTokenLength,
            "trace",
            "traces",
            t => GetTraceSummaryDto(t, getResourceName, dashboardBaseUrl),
            EstimateSerializedJsonTokenSize);

        var jsonArray = new JsonArray(trimmedItems.ToArray());
        return (jsonArray.ToJsonString(s_jsonSerializerOptions), limitMessage);
    }

    /// <summary>
    /// Creates a compact JsonObject summarizing a trace for AI processing.
    /// </summary>
    /// <remarks>
    /// The summary keeps what is needed to pick a trace to investigate: the root span, the child span
    /// with the most self time and its database statement (the usual cause of a slow request), and the first failed
    /// span with its exception. The full span list is available by requesting the trace by ID.
    /// </remarks>
    public static JsonObject GetTraceSummaryDto(
        OtlpTraceDto trace,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var rootSpan = GetRootSpan(trace);
        var spansByStart = trace.Spans.OrderBy(s => s.Span.StartTimeUnixNano ?? 0).ToList();

        // Rank child spans by self time (duration not covered by their own children). Ranking by duration
        // would pick the span that wraps everything, such as the outgoing HTTP call, instead of the database
        // call inside it where the time was actually spent.
        var childrenBySpanId = trace.Spans
            .Where(s => !string.IsNullOrEmpty(s.Span.ParentSpanId))
            .GroupBy(s => s.Span.ParentSpanId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Span).ToList(), StringComparer.Ordinal);
        var slowestSpan = trace.Spans
            .Where(s => !ReferenceEquals(s, rootSpan))
            .MaxBy(s => CalculateSelfTimeNano(
                s.Span,
                s.Span.SpanId is { } spanId && childrenBySpanId.TryGetValue(spanId, out var children) ? children : []) is { } selfTime
                    ? (long)selfTime
                    : -1);

        // A failure usually propagates to its parents, which then also end with an error status.
        // The failed span that ended first is the closest to the origin of the failure; among spans
        // ending together, prefer the one that started last because it is the deepest.
        var firstErrorSpan = trace.Spans
            .Where(s => s.Span.Status?.Code == 2)
            .OrderBy(s => s.Span.EndTimeUnixNano ?? ulong.MaxValue)
            .ThenByDescending(s => s.Span.StartTimeUnixNano ?? 0)
            .FirstOrDefault();

        var timestamp = rootSpan?.Span.StartTimeUnixNano is { } startNano
            ? OtlpHelpers.UnixNanoSecondsToDateTime(startNano)
            : (DateTime?)null;

        var distinctResources = spansByStart
            .Select(s => getResourceName(s.Resource))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var resources = distinctResources
            .Take(MaximumSummaryResourceCount)
            .Select(name => (JsonNode)LimitSerializedLength(name, MaximumSummaryResourceNameLength))
            .ToArray();
        var resourcesOmitted = distinctResources.Count - resources.Length;

        // Every string in a summary is bounded by its serialized size and the resource list is capped, so a summary
        // is at most about 11 KB, well under the CompactListTokenLength budget (about 16 KB). That guarantees the newest
        // trace always fits: GetLimitFromEndWithSummary stops at the first trace that doesn't fit, so an oversized
        // newest summary would hide every older trace too. Trace and span IDs are left whole because callers use them
        // to request full detail; OTLP defines them as 16 and 8 byte identifiers, so they are short.
        var summary = new JsonObject
        {
            ["traceId"] = trace.TraceId,
            ["title"] = LimitSerializedLength(rootSpan?.Span.Name, MaximumSummaryStringLength),
            ["timestamp"] = timestamp,
            ["durationMs"] = CalculateTraceDurationMs(trace.Spans),
            ["spanCount"] = trace.Spans.Count,
            ["resources"] = new JsonArray(resources),
            // Tells the agent the resource list is incomplete; requesting the trace by ID returns every span.
            ["resourcesOmitted"] = resourcesOmitted > 0 ? resourcesOmitted : null,
            ["hasError"] = firstErrorSpan is not null,
            ["rootSpan"] = rootSpan is null ? null : GetSpanSummaryDto(rootSpan, getResourceName),
            ["slowestSpan"] = slowestSpan is null ? null : GetSpanSummaryDto(slowestSpan, getResourceName),
            ["firstErrorSpan"] = firstErrorSpan is null ? null : GetSpanSummaryDto(firstErrorSpan, getResourceName)
        };

        if (dashboardBaseUrl is not null)
        {
            summary["dashboardUrl"] = DashboardUrls.CombineUrl(dashboardBaseUrl, DashboardUrls.TraceDetailUrl(trace.TraceId));
        }

        return StripNullProperties(summary);
    }

    private static OtlpSpanDto? GetRootSpan(OtlpTraceDto trace)
    {
        return trace.Spans.FirstOrDefault(s => string.IsNullOrEmpty(s.Span.ParentSpanId)) ?? trace.Spans.FirstOrDefault();
    }

    private static JsonObject GetSpanSummaryDto(OtlpSpanDto spanDto, Func<IOtlpResource, string> getResourceName)
    {
        var span = spanDto.Span;
        var httpStatus = span.Attributes?.FirstOrDefault(a => a.Key is "http.response.status_code" or "http.status_code") is { } statusAttribute
            && int.TryParse(GetAttributeValue(statusAttribute), CultureInfo.InvariantCulture, out var statusCode)
                ? GetHttpStatusName(statusCode)
                : null;

        // Database client spans carry the executed statement under the current semantic convention
        // (db.query.text) or the deprecated one (db.statement).
        // See https://opentelemetry.io/docs/specs/semconv/database/database-spans/
        var statement = GetAttributeStringValue(span.Attributes, "db.query.text")
            ?? GetAttributeStringValue(span.Attributes, "db.statement");

        var spanObj = new JsonObject
        {
            ["spanId"] = span.SpanId,
            ["name"] = LimitSerializedLength(span.Name, MaximumSummaryStringLength),
            ["kind"] = GetSpanKindName(span.Kind),
            ["source"] = LimitSerializedLength(getResourceName(spanDto.Resource), MaximumSummaryResourceNameLength),
            ["destination"] = LimitSerializedLength(GetAttributeStringValue(span.Attributes, OtlpHelpers.AspireDestinationNameAttribute), MaximumSummaryResourceNameLength),
            ["durationMs"] = CalculateDurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
            ["status"] = span.Status?.Code switch
            {
                1 => "Ok",
                2 => "Error",
                _ => null
            },
            ["statusMessage"] = LimitSerializedLength(span.Status?.Message, MaximumSummaryStringLength),
            ["httpStatus"] = httpStatus,
            ["statement"] = LimitSerializedLength(statement, MaximumSummaryStringLength),
            ["exception"] = LimitSerializedLength(GetSpanExceptionText(span), MaximumSummaryStringLength)
        };

        return StripNullProperties(spanObj);
    }

    private static string? GetSpanExceptionText(OtlpSpanJson span)
    {
        // Exceptions are recorded as span events named "exception" with exception.type and exception.message attributes.
        // See https://opentelemetry.io/docs/specs/semconv/exceptions/exceptions-spans/
        var exceptionEvent = span.Events?.FirstOrDefault(e => e.Name == "exception");
        if (exceptionEvent is null)
        {
            return null;
        }

        var type = GetAttributeStringValue(exceptionEvent.Attributes, ExceptionTypeField);
        var message = GetAttributeStringValue(exceptionEvent.Attributes, ExceptionMessageField);
        return (type, message) switch
        {
            (not null, not null) => $"{type}: {message}",
            _ => type ?? message
        };
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
    /// Serializes OTLP resource spans to a JSON string of individual spans for CLI output.
    /// Unlike <see cref="GetTracesJson"/>, which groups spans by trace ID, this returns a flat list.
    /// </summary>
    public static string SerializeSpansToJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var spans = GetSpanDtosFromOtlpData(resourceSpans);
        var context = new PromptContext(processValues: false);
        var jsonArray = new JsonArray(spans.Select(s => GetSpanDto(s, context, getResourceName, dashboardBaseUrl)).ToArray<JsonNode>());
        return jsonArray.ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Serializes OTLP resource logs to a JSON string of log entries for CLI output.
    /// </summary>
    public static string SerializeLogsToJson(
        IList<OtlpResourceLogsJson>? resourceLogs,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var logRecords = GetLogRecordsFromOtlpData(resourceLogs);
        var context = new PromptContext(processValues: false);
        var jsonArray = new JsonArray(logRecords.Select(l => GetLogEntryDto(l, context, getResourceName, dashboardBaseUrl)).ToArray<JsonNode>());
        return jsonArray.ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Serializes OTLP resource spans to a JSON string of traces for CLI output.
    /// </summary>
    public static string SerializeTracesToJson(
        IList<OtlpResourceSpansJson>? resourceSpans,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        var traces = GetTracesFromOtlpData(resourceSpans);
        var context = new PromptContext(processValues: false);
        var jsonArray = new JsonArray(traces.Select(t => GetTraceDto(t, context, getResourceName, dashboardBaseUrl)).ToArray<JsonNode>());
        return jsonArray.ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Serializes a single trace DTO to JSON for CLI output.
    /// </summary>
    public static string SerializeTraceToJson(
        OtlpTraceDto traceDto,
        Func<IOtlpResource, string>? getResourceName = null,
        string? dashboardBaseUrl = null)
    {
        var context = new PromptContext(processValues: false);
        return GetTraceDto(traceDto, context, getResourceName ?? (s => s.ResourceName), dashboardBaseUrl)
            .ToJsonString(s_jsonSerializerOptions);
    }

    /// <summary>
    /// Creates a JsonObject representing a single span for AI processing.
    /// </summary>
    public static JsonObject GetSpanDto(
        OtlpSpanDto spanDto,
        PromptContext context,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl = null)
    {
        // `aspire otel spans --format json` and trace JSON output use this shape;
        // keep docs/specs/cli-output-formats.md in sync when changing it.
        var span = spanDto.Span;
        var spanId = span.SpanId ?? string.Empty;
        var traceId = span.TraceId ?? string.Empty;

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
                ["traceId"] = link.TraceId ?? string.Empty,
                ["spanId"] = link.SpanId ?? string.Empty
            }).ToArray();
            linksArray = new JsonArray(linkObjects);
        }

        var resourceName = getResourceName?.Invoke(spanDto.Resource) ?? spanDto.Resource.ResourceName;
        var destination = GetAttributeStringValue(span.Attributes, OtlpHelpers.AspireDestinationNameAttribute);
        var statusCode = span.Status?.Code;
        var statusText = statusCode switch
        {
            1 => "Ok",
            2 => "Error",
            _ => null
        };

        var timestamp = span.StartTimeUnixNano is { } startNano
            ? OtlpHelpers.UnixNanoSecondsToDateTime(startNano)
            : (DateTime?)null;

        var spanObj = new JsonObject
        {
            ["traceId"] = traceId,
            ["spanId"] = spanId,
            ["parentSpanId"] = span.ParentSpanId,
            ["kind"] = GetSpanKindName(span.Kind),
            ["name"] = context.AddValue(span.Name, sId => $@"Duplicate of ""name"" for span {OtlpHelpers.ToShortenedId(sId)}", spanId),
            ["status"] = statusText,
            ["statusMessage"] = context.AddValue(span.Status?.Message, sId => $@"Duplicate of ""statusMessage"" for span {OtlpHelpers.ToShortenedId(sId)}", spanId),
            ["source"] = resourceName,
            ["destination"] = destination,
            ["durationMs"] = CalculateDurationMs(span.StartTimeUnixNano, span.EndTimeUnixNano),
            ["timestamp"] = timestamp,
            ["attributes"] = attributesObj,
            ["links"] = linksArray
        };

        if (dashboardBaseUrl is not null)
        {
            spanObj["dashboardUrl"] = DashboardUrls.CombineUrl(dashboardBaseUrl, DashboardUrls.TraceDetailUrl(traceId, spanId: spanId));
        }

        return StripNullProperties(spanObj);
    }

    /// <summary>
    /// Extracts individual spans from OTLP resource spans as a flat list.
    /// </summary>
    public static List<OtlpSpanDto> GetSpanDtosFromOtlpData(IList<OtlpResourceSpansJson>? resourceSpans)
    {
        var spans = new List<OtlpSpanDto>();

        if (resourceSpans is null)
        {
            return spans;
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
                    spans.Add(new OtlpSpanDto(span, resource, scopeName));
                }
            }
        }

        return spans;
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
        // `aspire otel traces --format json` uses this shape;
        // keep docs/specs/cli-output-formats.md in sync when changing it.
        var spanObjects = new List<JsonNode>();
        foreach (var s in trace.Spans)
        {
            var spanObj = GetSpanDto(s, context, getResourceName, dashboardBaseUrl: null);
            spanObjects.Add(spanObj);
        }

        var spanArray = new JsonArray(spanObjects.ToArray());
        var traceId = trace.TraceId;
        var rootSpan = GetRootSpan(trace);
        var hasError = trace.Spans.Any(s => s.Span.Status?.Code == 2);
        var timestamp = rootSpan?.Span.StartTimeUnixNano is { } startNano
            ? OtlpHelpers.UnixNanoSecondsToDateTime(startNano)
            : (DateTime?)null;

        var traceData = new JsonObject
        {
            ["traceId"] = traceId,
            ["durationMs"] = CalculateTraceDurationMs(trace.Spans),
            ["title"] = rootSpan?.Span.Name,
            ["spans"] = spanArray,
            ["hasError"] = hasError,
            ["timestamp"] = timestamp
        };

        if (dashboardBaseUrl is not null)
        {
            traceData["dashboardUrl"] = DashboardUrls.CombineUrl(dashboardBaseUrl, DashboardUrls.TraceDetailUrl(traceId));
        }

        return StripNullProperties(traceData);
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

    private static JsonObject StripNullProperties(JsonObject obj)
    {
        foreach (var key in obj.Where(kvp => kvp.Value is null).Select(kvp => kvp.Key).ToList())
        {
            obj.Remove(key);
        }

        return obj;
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
    /// Calculates the time of a span not covered by any of its children, in nanoseconds.
    /// </summary>
    /// <remarks>
    /// Children can run in parallel, so subtracting the sum of their durations would double-count overlapping
    /// time. Instead, the union of the child intervals, clipped to the parent interval, is subtracted.
    /// Children without both timestamps are ignored.
    /// </remarks>
    private static ulong? CalculateSelfTimeNano(OtlpSpanJson span, List<OtlpSpanJson> children)
    {
        if (span.StartTimeUnixNano is not { } start || span.EndTimeUnixNano is not { } end || end < start)
        {
            return null;
        }

        var childIntervals = children
            .Where(c => c.StartTimeUnixNano is not null && c.EndTimeUnixNano is not null)
            .Select(c => (Start: Math.Max(c.StartTimeUnixNano!.Value, start), End: Math.Min(c.EndTimeUnixNano!.Value, end)))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start);

        ulong covered = 0;
        ulong? currentStart = null;
        ulong currentEnd = 0;
        foreach (var (childStart, childEnd) in childIntervals)
        {
            if (currentStart is null || childStart > currentEnd)
            {
                if (currentStart is not null)
                {
                    covered += currentEnd - currentStart.Value;
                }

                currentStart = childStart;
                currentEnd = childEnd;
            }
            else
            {
                currentEnd = Math.Max(currentEnd, childEnd);
            }
        }

        if (currentStart is not null)
        {
            covered += currentEnd - currentStart.Value;
        }

        return end - start - covered;
    }

    private static ulong GetLogTimestampUnixNano(OtlpLogEntryDto logEntry)
    {
        // OTLP uses 0 for an unknown time; fall back to the time the collector observed the record.
        // See https://opentelemetry.io/docs/specs/otel/logs/data-model/#field-observedtimestamp
        return logEntry.LogRecord.TimeUnixNano is { } time and not 0
            ? time
            : logEntry.LogRecord.ObservedTimeUnixNano ?? 0;
    }

    /// <summary>
    /// Gets traces ordered by their earliest span start time.
    /// </summary>
    /// <remarks>
    /// <see cref="GetLimitFromEndWithSummary"/> keeps the last items, but <see cref="GetTracesFromOtlpData"/> returns
    /// traces in resource and scope traversal order, which is not chronological when resources are interleaved.
    /// <see cref="GetTracesFromOtlpData"/> itself is left unchanged so the CLI output order of other callers stays the same.
    /// OrderBy is stable, so traces with equal start times keep their original order.
    /// </remarks>
    private static List<OtlpTraceDto> GetTracesInStartOrder(IList<OtlpResourceSpansJson>? resourceSpans)
    {
        return GetTracesFromOtlpData(resourceSpans)
            .OrderBy(t => t.Spans.Select(s => s.Span.StartTimeUnixNano).Min() ?? 0)
            .ToList();
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
        return GetLogEntryDto(logEntry, context, getResourceName, dashboardBaseUrl, maximumFieldLength: null);
    }

    // When maximumFieldLength is set, it is the maximum serialized size in UTF-8 bytes of the attributes, and separately
    // of the message and of the exception.
    private static JsonObject GetLogEntryDto(
        OtlpLogEntryDto logEntry,
        PromptContext context,
        Func<IOtlpResource, string> getResourceName,
        string? dashboardBaseUrl,
        int? maximumFieldLength)
    {
        // `aspire otel logs --format json` uses this shape;
        // keep docs/specs/cli-output-formats.md in sync when changing it.
        var exceptionText = GetExceptionText(logEntry);
        var logIdString = GetAttributeStringValue(logEntry.LogRecord.Attributes, OtlpHelpers.AspireLogIdAttribute);
        var logId = long.TryParse(logIdString, CultureInfo.InvariantCulture, out var parsedLogId) ? parsedLogId : (long?)null;
        var resourceName = getResourceName?.Invoke(logEntry.Resource) ?? logEntry.Resource.ResourceName;

        var attributes = GetFilteredAttributes(logEntry.LogRecord.Attributes)
            .Select(a => (Key: a.Key!, Value: GetAttributeValue(a)))
            .ToList();

        // Decide whether to bound attributes before any value is registered with the prompt context.
        // Registered values become "Duplicate of ..." references in entries converted afterwards, so registering a value
        // that is then shortened or dropped here would point those references at data that isn't in the output.
        // Sizes are measured as serialized, so escaped characters such as control characters count at their written size.
        var attributesTruncated = maximumFieldLength is { } maxLength
            && attributes.Sum(a => EstimateAttributeLength(a.Key, LimitLength(a.Value, MaximumStringLength))) > maxLength;

        var attributesObject = new JsonObject();
        var attributesLength = 0;
        foreach (var (key, value) in attributes)
        {
            if (attributesTruncated)
            {
                var boundedValue = LimitSerializedLength(value, MaximumSummaryStringLength);
                attributesLength += EstimateAttributeLength(key, boundedValue);
                if (attributesLength > maximumFieldLength)
                {
                    break;
                }

                attributesObject[key] = boundedValue;
            }
            else
            {
                attributesObject[key] = context.AddValue(value, id => $@"Duplicate of attribute ""{id.Key}"" for log entry {id.LogId}", (LogId: logId, Key: key));
            }
        }

        var message = GetLogMessage(logEntry.LogRecord) ?? string.Empty;
        var log = new JsonObject
        {
            ["logId"] = logId,
            ["spanId"] = logEntry.LogRecord.SpanId,
            ["traceId"] = logEntry.LogRecord.TraceId,
            ["message"] = AddBoundedValue(message, id => $@"Duplicate of ""message"" for log entry {id}"),
            ["severity"] = logEntry.LogRecord.SeverityText ?? "Unknown",
            ["resourceName"] = resourceName,
            ["attributes"] = attributesObject,
            // Tells the agent that attribute values were shortened or omitted; the full entry is available
            // from the dashboard (dashboardUrl).
            ["attributesTruncated"] = attributesTruncated ? true : null,
            ["exception"] = AddBoundedValue(exceptionText, id => $@"Duplicate of ""exception"" for log entry {id}"),
            ["source"] = logEntry.ScopeName
        };

        if (dashboardBaseUrl is not null && logId is not null)
        {
            log["dashboardUrl"] = DashboardUrls.CombineUrl(dashboardBaseUrl, DashboardUrls.StructuredLogsUrl(logEntryId: logId));
        }

        return StripNullProperties(log);

        string? AddBoundedValue(string? value, Func<long?, string> getKey)
        {
            // The prompt context caps values at MaximumStringLength characters, which is enough for ASCII text but
            // not for text written as several bytes per character (CJK text, escaped control characters). Shorten
            // such values to the field budget instead, without registering them for the same reason as attributes.
            if (value is not null
                && maximumFieldLength is { } maxLength
                && GetSerializedLength(LimitLength(value, MaximumStringLength)) > maxLength)
            {
                return LimitSerializedLength(value, maxLength);
            }

            return context.AddValue(value, getKey, logId);
        }
    }

    // Size of "key":"value" as serialized, including quotes, colon and separator.
    private static int EstimateAttributeLength(string key, string value)
    {
        return GetSerializedLength(key) + GetSerializedLength(value) + 6;
    }

    /// <summary>
    /// Gets the size in UTF-8 bytes of a string value as written with <see cref="s_jsonSerializerOptions"/>, excluding quotes.
    /// </summary>
    /// <remarks>
    /// Escapes count at their written size: for example, a control character is written as the six bytes <c>\u0001</c>.
    /// </remarks>
    private static int GetSerializedLength(ReadOnlySpan<char> value)
    {
        return JsonEncodedText.Encode(value, s_jsonSerializerOptions.Encoder).EncodedUtf8Bytes.Length;
    }

    /// <summary>
    /// Limits a string so that its serialized size, as measured by <see cref="GetSerializedLength"/>, is at most
    /// <paramref name="maximumLength"/> UTF-8 bytes, appending a truncation marker if needed.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? LimitSerializedLength(string? value, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        // Every character is written as at least one byte, so no prefix longer than maximumLength characters fits.
        var candidate = value.AsSpan(0, Math.Min(value.Length, maximumLength));
        if (candidate.Length == value.Length && GetSerializedLength(candidate) <= maximumLength)
        {
            return value;
        }

        // Find the longest prefix that fits. The serialized size grows with the prefix length, and the candidate is at most
        // maximumLength characters, so this encodes a short span a few times.
        var low = 0;
        var high = candidate.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (GetSerializedLength(candidate[..middle]) <= maximumLength)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        // Don't split a surrogate pair: a lone high surrogate isn't valid UTF-16.
        if (low > 0 && char.IsHighSurrogate(candidate[low - 1]))
        {
            low--;
        }

        return string.Concat(candidate[..low], TruncatedMarker);
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
        return LimitLength(value, MaximumStringLength)!;
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? LimitLength(string? value, int maximumLength)
    {
        if (value is null || value.Length <= maximumLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maximumLength), TruncatedMarker);
    }

    /// <summary>
    /// Gets items from the end of a list with a summary message, applying count and token limits.
    /// </summary>
    public static (List<TResult> items, string message) GetLimitFromEndWithSummary<T, TResult>(
        List<T> values,
        int totalValues,
        int limit,
        int maximumTokenLength,
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
        var dtos = new List<TResult>();

        // Convert from newest to oldest to prioritize the latest items, and stop at the first item that doesn't fit so
        // items that are not returned are never converted. The order also matters for conversions that deduplicate values
        // with a PromptContext: a repeated value is written in full in the newest item that has it, and older items refer
        // to that newer item. The returned items are always a suffix of the list, so every referenced item is returned too.
        // Converting oldest first would let a returned item refer to an older item that was dropped to fit the budget.
        for (var i = trimmedItems.Count - 1; i >= 0; i--)
        {
            var dto = convertToDto(trimmedItems[i]);
            var tokenCount = estimateTokenSize(dto);

            if (currentTokenCount + tokenCount > maximumTokenLength)
            {
                break;
            }

            dtos.Add(dto);
            currentTokenCount += tokenCount;
        }

        dtos.Reverse();

        return (dtos, GetLimitSummary(totalValues, dtos.Count, itemName, pluralItemName));
    }

    /// <summary>
    /// Gets items from the start of a list with a summary message, applying count and token limits.
    /// </summary>
    public static (List<TResult> items, string message) GetLimitFromStartWithSummary<T, TResult>(
        List<T> values,
        int totalValues,
        int limit,
        int maximumTokenLength,
        string itemName,
        string pluralItemName,
        Func<T, TResult> convertToDto,
        Func<TResult, int> estimateTokenSize)
    {
        Debug.Assert(totalValues >= values.Count, "Total values should be large or equal to the values passed into the method.");

        var currentTokenCount = 0;
        var dtos = new List<TResult>();

        foreach (var value in values.Take(limit))
        {
            var dto = convertToDto(value);
            var tokenCount = estimateTokenSize(dto);

            if (currentTokenCount + tokenCount > maximumTokenLength)
            {
                break;
            }

            dtos.Add(dto);
            currentTokenCount += tokenCount;
        }

        var message = totalValues == dtos.Count
            ? $"Returned {ToQuantity(dtos.Count, itemName, pluralItemName)}."
            : $"Returned first {ToQuantity(dtos.Count, itemName, pluralItemName)}. Later {ToQuantity(totalValues - dtos.Count, itemName, pluralItemName)} not returned because of size limits.";

        return (dtos, message);
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
