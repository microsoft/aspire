// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Otlp.Serialization;

namespace Aspire.Dashboard.Api;

/// <summary>
/// Handles telemetry API requests, returning data in OTLP JSON format.
/// </summary>
internal sealed class TelemetryApiService(
    TelemetryRepository telemetryRepository,
    IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
{
    private const int DefaultLimit = 200;
    private const int DefaultTraceLimit = 100;

    private readonly IOutgoingPeerResolver[] _outgoingPeerResolvers = outgoingPeerResolvers.ToArray();

    /// <summary>
    /// Gets spans in OTLP JSON format.
    /// Returns null if resource filter is specified but not found.
    /// Supports multiple resource names.
    /// </summary>
    public TelemetryApiResponse? GetSpans(string[]? resourceNames, string? traceId, bool? hasError, int? limit, string? search = null, double? minDurationMs = null)
    {
        // Resolve resource keys for all specified resources
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);
        if (resourceKeys is null)
        {
            return null;
        }

        var effectiveLimit = limit ?? DefaultLimit;

        // Get spans for all resource keys
        var allSpans = new List<OtlpSpan>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetTraces(new GetTracesRequest
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = [],
                FilterText = string.Empty
            });
            allSpans.AddRange(result.PagedResult.Items.SelectMany(t => t.Spans));
        }

        var spans = allSpans;

        // TODO: Consider adding an ExcludeFromApi property on resources in the future.
        // Currently the API returns all telemetry data for all resources.

        // Filter by traceId
        if (!string.IsNullOrEmpty(traceId))
        {
            spans = spans.Where(s => OtlpHelpers.MatchTelemetryId(traceId, s.TraceId)).ToList();
        }

        // Filter by hasError
        if (hasError == true)
        {
            spans = spans.Where(s => s.Status == OtlpSpanStatusCode.Error).ToList();
        }
        else if (hasError == false)
        {
            spans = spans.Where(s => s.Status != OtlpSpanStatusCode.Error).ToList();
        }

        // Apply full-text search across all span fields
        if (!string.IsNullOrEmpty(search))
        {
            spans = spans.Where(s => MatchesSearch(s, search)).ToList();
        }

        if (GetMinimumDuration(minDurationMs) is { } minimumDuration)
        {
            spans = spans.Where(s => s.Duration >= minimumDuration).ToList();
        }

        var totalCount = spans.Count;

        // Apply limit (take from end for most recent)
        if (spans.Count > effectiveLimit)
        {
            spans = spans.Skip(spans.Count - effectiveLimit).ToList();
        }

        var otlpData = TelemetryExportService.ConvertSpansToOtlpJson(spans, _outgoingPeerResolvers);

        return new TelemetryApiResponse
        {
            Data = otlpData,
            TotalCount = totalCount,
            ReturnedCount = spans.Count
        };
    }

    /// <summary>
    /// Gets traces in OTLP JSON format (grouped by trace).
    /// Returns null if resource filter is specified but not found.
    /// Supports multiple resource names.
    /// </summary>
    public TelemetryApiResponse? GetTraces(string[]? resourceNames, bool? hasError, int? limit, string? search = null, double? minDurationMs = null)
    {
        // Resolve resource keys for all specified resources
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);
        if (resourceKeys is null)
        {
            return null;
        }

        var effectiveLimit = limit ?? DefaultTraceLimit;

        // Get traces for all resource keys
        var allTraces = new List<OtlpTrace>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetTraces(new GetTracesRequest
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = [],
                FilterText = string.Empty
            });
            allTraces.AddRange(result.PagedResult.Items);
        }

        var traces = allTraces;

        // Filter traces by hasError
        if (hasError == true)
        {
            traces = traces.Where(t => t.Spans.Any(s => s.Status == OtlpSpanStatusCode.Error)).ToList();
        }
        else if (hasError == false)
        {
            traces = traces.Where(t => !t.Spans.Any(s => s.Status == OtlpSpanStatusCode.Error)).ToList();
        }

        // Apply full-text search: a trace matches if its name matches or any span within it matches
        if (!string.IsNullOrEmpty(search))
        {
            traces = traces.Where(t =>
                t.FullName.Contains(search, StringComparisons.FullTextSearch) ||
                t.Spans.Any(s => MatchesSearch(s, search))).ToList();
        }

        List<OtlpSpan> spans;
        int totalCount;
        int returnedCount;

        if (GetMinimumDuration(minDurationMs) is { } minimumDuration)
        {
            var filteredTraces = traces
                .Select(t => (Trace: t, Spans: GetSpansMatchingMinimumDuration(t.Spans, minimumDuration).ToList()))
                .Where(t => t.Spans.Count > 0)
                .ToList();

            totalCount = filteredTraces.Count;

            // Apply limit (take from end for most recent)
            if (filteredTraces.Count > effectiveLimit)
            {
                filteredTraces = filteredTraces.Skip(filteredTraces.Count - effectiveLimit).ToList();
            }

            spans = filteredTraces.SelectMany(t => t.Spans).ToList();
            returnedCount = filteredTraces.Count;
        }
        else
        {
            totalCount = traces.Count;

            // Apply limit (take from end for most recent)
            if (traces.Count > effectiveLimit)
            {
                traces = traces.Skip(traces.Count - effectiveLimit).ToList();
            }

            spans = traces.SelectMany(t => t.Spans).ToList();
            returnedCount = traces.Count;
        }

        var otlpData = TelemetryExportService.ConvertSpansToOtlpJson(spans, _outgoingPeerResolvers);

        return new TelemetryApiResponse
        {
            Data = otlpData,
            TotalCount = totalCount,
            ReturnedCount = returnedCount
        };
    }

    /// <summary>
    /// Gets a specific trace by ID with all spans in OTLP format.
    /// Returns null if trace not found.
    /// </summary>
    public TelemetryApiResponse? GetTrace(string traceId, double? minDurationMs = null)
    {
        var trace = telemetryRepository.GetTrace(traceId);
        if (trace is null)
        {
            return null;
        }

        var spans = GetSpansMatchingMinimumDuration(trace.Spans, GetMinimumDuration(minDurationMs)).ToList();

        var otlpData = TelemetryExportService.ConvertSpansToOtlpJson(spans, _outgoingPeerResolvers);

        return new TelemetryApiResponse
        {
            Data = otlpData,
            TotalCount = spans.Count,
            ReturnedCount = spans.Count
        };
    }

    /// <summary>
    /// Gets logs in OTLP JSON format.
    /// Returns null if resource filter is specified but not found.
    /// Supports multiple resource names.
    /// </summary>
    public TelemetryApiResponse? GetLogs(string[]? resourceNames, string? traceId, string? severity, int? limit, string? search = null)
    {
        // Resolve resource keys for all specified resources
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);
        if (resourceKeys is null)
        {
            return null;
        }

        var effectiveLimit = limit ?? DefaultLimit;

        var filters = new List<TelemetryFilter>();

        if (!string.IsNullOrEmpty(traceId))
        {
            filters.Add(new FieldTelemetryFilter
            {
                Field = KnownStructuredLogFields.TraceIdField,
                Value = traceId,
                Condition = FilterCondition.Contains
            });
        }

        // Severity filter uses GreaterThanOrEqual - e.g., "error" returns Error and Critical
        if (!string.IsNullOrEmpty(severity) && Enum.TryParse<LogLevel>(severity, ignoreCase: true, out var logLevel))
        {
            // Trace is the lowest level, so no filter needed for it
            if (logLevel != LogLevel.Trace)
            {
                filters.Add(new FieldTelemetryFilter
                {
                    Field = nameof(OtlpLogEntry.Severity),
                    Value = logLevel.ToString(),
                    Condition = FilterCondition.GreaterThanOrEqual
                });
            }
        }

        // Get logs for all resource keys
        var allLogs = new List<OtlpLogEntry>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetLogs(new GetLogsContext
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = filters
            });
            allLogs.AddRange(result.Items);
        }

        var logs = allLogs;

        // Apply full-text search across all log fields
        if (!string.IsNullOrEmpty(search))
        {
            logs = logs.Where(l => MatchesSearch(l, search)).ToList();
        }

        var totalCount = logs.Count;

        // Apply limit (take from end for most recent)
        if (logs.Count > effectiveLimit)
        {
            logs = logs.Skip(logs.Count - effectiveLimit).ToList();
        }

        var otlpData = TelemetryExportService.ConvertLogsToOtlpJson(logs);

        return new TelemetryApiResponse
        {
            Data = otlpData,
            TotalCount = totalCount,
            ReturnedCount = logs.Count
        };
    }

    /// <summary>
    /// Streams span updates as they arrive in OTLP JSON format.
    /// Supports multiple resource names.
    /// </summary>
    public async IAsyncEnumerable<string> FollowSpansAsync(
        string[]? resourceNames,
        string? traceId,
        bool? hasError,
        string? search,
        double? minDurationMs = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve resource keys
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);

        // For streaming, if resources were specified but can't be resolved, filter everything out
        var hasResourceFilter = resourceNames is { Length: > 0 };
        var invalidResourceFilter = hasResourceFilter && resourceKeys is null;

        var minimumDuration = GetMinimumDuration(minDurationMs);

        // Watch all spans and filter
        await foreach (var span in telemetryRepository.WatchSpansAsync(null, cancellationToken).ConfigureAwait(false))
        {
            // If resource filter is invalid (resources specified but not found), skip all
            if (invalidResourceFilter)
            {
                continue;
            }

            // Filter by resource if specified
            if (resourceKeys is { Count: > 0 } && !resourceKeys.Any(k => k is null) &&
                !resourceKeys.Any(k => k?.EqualsCompositeName(span.Source.ResourceKey.GetCompositeName()) == true))
            {
                continue;
            }

            // Apply traceId filter
            if (!string.IsNullOrEmpty(traceId) && !OtlpHelpers.MatchTelemetryId(traceId, span.TraceId))
            {
                continue;
            }

            // Apply hasError filter
            if (hasError.HasValue && (span.Status == OtlpSpanStatusCode.Error) != hasError.Value)
            {
                continue;
            }

            // Apply full-text search filter
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(span, search))
            {
                continue;
            }

            if (minimumDuration is { } duration && span.Duration < duration)
            {
                continue;
            }

            // Use compact JSON for NDJSON streaming (no indentation)
            yield return TelemetryExportService.ConvertSpanToJson(span, _outgoingPeerResolvers, logs: null, indent: false);
        }
    }

    /// <summary>
    /// Streams log updates as they arrive in OTLP JSON format.
    /// Supports multiple resource names.
    /// </summary>
    public async IAsyncEnumerable<string> FollowLogsAsync(
        string[]? resourceNames,
        string? traceId,
        string? severity,
        string? search,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Resolve resource keys
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);

        // For streaming, if resources were specified but can't be resolved, filter everything out
        var hasResourceFilter = resourceNames is { Length: > 0 };
        var invalidResourceFilter = hasResourceFilter && resourceKeys is null;

        // Build filters
        var filters = new List<TelemetryFilter>();

        if (!string.IsNullOrEmpty(traceId))
        {
            filters.Add(new FieldTelemetryFilter
            {
                Field = KnownStructuredLogFields.TraceIdField,
                Value = traceId,
                Condition = FilterCondition.Contains
            });
        }

        if (!string.IsNullOrEmpty(severity) && Enum.TryParse<LogLevel>(severity, ignoreCase: true, out var parsedLevel))
        {
            // Trace is the lowest level, so no filter needed for it
            if (parsedLevel != LogLevel.Trace)
            {
                filters.Add(new FieldTelemetryFilter
                {
                    Field = nameof(OtlpLogEntry.Severity),
                    Value = parsedLevel.ToString(),
                    Condition = FilterCondition.GreaterThanOrEqual
                });
            }
        }

        // Watch all logs and filter by resource
        await foreach (var log in telemetryRepository.WatchLogsAsync(null, filters, cancellationToken).ConfigureAwait(false))
        {
            // If resource filter is invalid (resources specified but not found), skip all
            if (invalidResourceFilter)
            {
                continue;
            }

            // Filter by resource if specified
            if (resourceKeys is { Count: > 0 } && !resourceKeys.Any(k => k is null) &&
                !resourceKeys.Any(k => k?.EqualsCompositeName(log.ResourceView.ResourceKey.GetCompositeName()) == true))
            {
                continue;
            }

            // Apply full-text search filter
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(log, search))
            {
                continue;
            }

            var otlpData = TelemetryExportService.ConvertLogsToOtlpJson([log]);
            yield return JsonSerializer.Serialize(otlpData, OtlpJsonSerializerContext.DefaultOptions);
        }
    }

    /// <summary>
    /// Gets the list of available resources that have telemetry data.
    /// </summary>
    public ResourceInfoJson[] GetResources()
    {
        var resources = telemetryRepository.GetResources();
        return resources
            .Where(r => !r.UninstrumentedPeer) // Exclude uninstrumented peers
            .Select(r => new ResourceInfoJson
            {
                Name = r.ResourceName,
                InstanceId = r.InstanceId,
                DisplayName = r.ResourceKey.GetCompositeName(),
                HasLogs = r.HasLogs,
                HasTraces = r.HasTraces,
                HasMetrics = r.HasMetrics
            })
            .ToArray();
    }

    /// <summary>
    /// Checks whether a log entry matches a full-text search string.
    /// Searches across message, attribute values, scope name, event name, trace ID, span ID,
    /// severity, and resource name using case-insensitive contains matching.
    /// </summary>
    private static bool MatchesSearch(OtlpLogEntry log, string search)
    {
        if (log.Message.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.Scope.Name.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.EventName is not null && log.EventName.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.TraceId.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.SpanId.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.Severity.ToString().Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (log.ResourceView.Resource.ResourceName.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        foreach (var attribute in log.Attributes)
        {
            if (attribute.Key.Contains(search, StringComparisons.FullTextSearch) ||
                attribute.Value.Contains(search, StringComparisons.FullTextSearch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks whether a span matches a full-text search string.
    /// Searches across name, attribute values, span ID, trace ID, status message,
    /// scope name, event names, and resource name using case-insensitive contains matching.
    /// </summary>
    private static bool MatchesSearch(OtlpSpan span, string search)
    {
        if (span.Name.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.SpanId.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.TraceId.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.StatusMessage is not null && span.StatusMessage.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.Scope.Name.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.Source.Resource.ResourceName.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.Status.ToString().Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        if (span.Kind.ToString().Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        foreach (var attribute in span.Attributes)
        {
            if (attribute.Key.Contains(search, StringComparisons.FullTextSearch) ||
                attribute.Value.Contains(search, StringComparisons.FullTextSearch))
            {
                return true;
            }
        }

        foreach (var evt in span.Events)
        {
            if (evt.Name.Contains(search, StringComparisons.FullTextSearch))
            {
                return true;
            }
        }

        return false;
    }

    private static TimeSpan? GetMinimumDuration(double? minimumDurationMilliseconds)
    {
        if (minimumDurationMilliseconds is not > 0)
        {
            return null;
        }

        var value = minimumDurationMilliseconds.GetValueOrDefault();
        if (!double.IsFinite(value))
        {
            return null;
        }

        if (value >= TimeSpan.MaxValue.TotalMilliseconds)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromMilliseconds(value);
    }

    private static IEnumerable<OtlpSpan> GetSpansMatchingMinimumDuration(IEnumerable<OtlpSpan> spans, TimeSpan? minimumDuration)
    {
        if (minimumDuration is not { } duration)
        {
            return spans;
        }

        return spans.Where(s => s.Duration >= duration);
    }

    /// <summary>
    /// Resolves resource names to ResourceKeys.
    /// Returns null if any specified resource is not found.
    /// If no resources are specified, returns a list with a single null key (no filter).
    /// </summary>
    private static List<ResourceKey?>? ResolveResourceKeys(IReadOnlyList<OtlpResource> resources, string[]? resourceNames)
    {
        if (resourceNames is null || resourceNames.Length == 0)
        {
            // No filter - return a list with null to indicate "all resources"
            return [null];
        }

        var keys = new List<ResourceKey?>();
        foreach (var resourceName in resourceNames)
        {
            if (!AIHelpers.TryResolveResourceForTelemetry(resources, resourceName, out _, out var resourceKey))
            {
                return null;
            }
            keys.Add(resourceKey);
        }
        return keys;
    }
}
