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
    public TelemetryApiResponse? GetSpans(string[]? resourceNames, string? traceId, bool? hasError, int? limit, string? search = null)
    {
        // Resolve resource keys for all specified resources
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);
        if (resourceKeys is null)
        {
            return null;
        }

        var effectiveLimit = limit ?? DefaultLimit;

        // Convert structured search qualifiers into TelemetryFilter objects for repository-level filtering
        var spanFilters = new List<TelemetryFilter>();
        var searchTextFragments = ParseAndApplySearchFilters(search, spanFilters, AddSpanFiltersFromQualifiers, key => ResolveSpanFieldKey(key) is not null);

        // Get spans for all resource keys
        var allSpans = new List<OtlpSpan>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetSpans(new GetSpansRequest
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = spanFilters,
                TraceId = traceId,
                HasError = hasError,
                TextFragments = searchTextFragments
            });
            allSpans.AddRange(result.PagedResult.Items);
        }

        var totalCount = allSpans.Count;

        // Apply limit (take from end for most recent)
        var spans = allSpans;
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
    public TelemetryApiResponse? GetTraces(string[]? resourceNames, bool? hasError, int? limit, string? search = null)
    {
        // Resolve resource keys for all specified resources
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);
        if (resourceKeys is null)
        {
            return null;
        }

        var effectiveLimit = limit ?? DefaultTraceLimit;

        // Convert structured search qualifiers into TelemetryFilter objects for repository-level filtering
        var traceFilters = new List<TelemetryFilter>();
        var searchTextFragments = ParseAndApplySearchFilters(search, traceFilters, AddSpanFiltersFromQualifiers, key => ResolveSpanFieldKey(key) is not null);

        // Get traces for all resource keys
        var allTraces = new List<OtlpTrace>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetTraces(new GetTracesRequest
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = traceFilters,
                TextFragments = searchTextFragments
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

        var totalCount = traces.Count;

        // Apply limit (take from end for most recent)
        if (traces.Count > effectiveLimit)
        {
            traces = traces.Skip(traces.Count - effectiveLimit).ToList();
        }

        var spans = traces.SelectMany(t => t.Spans).ToList();
        var returnedCount = traces.Count;

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
    public TelemetryApiResponse? GetTrace(string traceId)
    {
        var trace = telemetryRepository.GetTrace(traceId);
        if (trace is null)
        {
            return null;
        }

        var spans = trace.Spans.ToList();

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

        var searchTextFragments = ParseAndApplySearchFilters(search, filters, AddLogFiltersFromQualifiers, key => ResolveLogFieldKey(key) is not null);

        // Get logs for all resource keys
        var allLogs = new List<OtlpLogEntry>();
        foreach (var resourceKey in resourceKeys)
        {
            var result = telemetryRepository.GetLogs(new GetLogsContext
            {
                ResourceKey = resourceKey,
                StartIndex = 0,
                Count = int.MaxValue,
                Filters = filters,
                TextFragments = searchTextFragments
            });
            allLogs.AddRange(result.Items);
        }

        var logs = allLogs;

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
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Resolve resource keys
        var resources = telemetryRepository.GetResources();
        var resourceKeys = ResolveResourceKeys(resources, resourceNames);

        // For streaming, if resources were specified but can't be resolved, filter everything out
        var hasResourceFilter = resourceNames is { Length: > 0 };
        var invalidResourceFilter = hasResourceFilter && resourceKeys is null;

        if (invalidResourceFilter)
        {
            yield break;
        }

        // Convert structured search qualifiers into TelemetryFilter objects for per-span filtering
        List<TelemetryFilter> spanFilters = [];
        var searchTextFragments = ParseAndApplySearchFilters(search, spanFilters, AddSpanFiltersFromQualifiers, key => ResolveSpanFieldKey(key) is not null);

        // Build the watch request with all filters pushed into the repository
        var watchRequest = new WatchSpansRequest
        {
            ResourceKey = resourceKeys is { Count: 1 } ? resourceKeys[0] : null,
            Filters = spanFilters,
            TraceId = traceId,
            HasError = hasError,
            TextFragments = searchTextFragments
        };

        // Watch spans with filtering done inside the repository
        await foreach (var span in telemetryRepository.WatchSpansAsync(watchRequest, cancellationToken).ConfigureAwait(false))
        {
            // Multi-resource filtering: repository only supports single ResourceKey,
            // so for multi-resource queries we filter here.
            if (resourceKeys is { Count: > 1 } &&
                !resourceKeys.Any(k => k?.EqualsCompositeName(span.Source.ResourceKey.GetCompositeName()) == true))
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

        if (invalidResourceFilter)
        {
            yield break;
        }

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

        var searchTextFragments = ParseAndApplySearchFilters(search, filters, AddLogFiltersFromQualifiers, key => ResolveLogFieldKey(key) is not null);

        // Build the watch request with all filters pushed into the repository
        var watchRequest = new WatchLogsRequest
        {
            ResourceKey = resourceKeys is { Count: 1 } ? resourceKeys[0] : null,
            Filters = filters,
            TextFragments = searchTextFragments
        };

        // Watch logs with filtering done inside the repository
        await foreach (var log in telemetryRepository.WatchLogsAsync(watchRequest, cancellationToken).ConfigureAwait(false))
        {
            // Multi-resource filtering: repository only supports single ResourceKey,
            // so for multi-resource queries we filter here.
            if (resourceKeys is { Count: > 1 } &&
                !resourceKeys.Any(k => k?.EqualsCompositeName(log.ResourceView.ResourceKey.GetCompositeName()) == true))
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
    /// Parses the search string and appends the resulting qualifier-based filters to <paramref name="filters"/>.
    /// Returns the extracted free-text fragments, or null if no search text was provided.
    /// </summary>
    private static string[]? ParseAndApplySearchFilters(
        string? search,
        List<TelemetryFilter> filters,
        Action<SearchFilter, List<TelemetryFilter>> addFilters,
        Func<string, bool> isKnownKey)
    {
        if (!string.IsNullOrEmpty(search))
        {
            var parsedSearch = SearchTextParser.ParseSearch(search, isKnownKey);
            if (!parsedSearch.IsEmpty)
            {
                addFilters(parsedSearch, filters);
                return parsedSearch.TextFragments;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts search qualifiers into <see cref="FieldTelemetryFilter"/> objects for log filtering.
    /// Maps user-facing qualifier keys (e.g., "severity", "message") to internal field constants.
    /// Attribute qualifiers (@-prefixed) pass the key directly for attribute fallback lookup.
    /// </summary>
    private static void AddLogFiltersFromQualifiers(SearchFilter parsedSearch, List<TelemetryFilter> filters)
    {
        foreach (var qualifier in parsedSearch.Qualifiers)
        {
            var field = qualifier.IsAttribute ? qualifier.Key : ResolveLogFieldKey(qualifier.Key);
            if (field is null)
            {
                // Unknown bare qualifier key — skip (treated as text at a higher level if needed)
                continue;
            }

            filters.Add(new FieldTelemetryFilter
            {
                Field = field,
                Value = qualifier.Value,
                Condition = ToFilterCondition(qualifier.Operator, negated: false)
            });
        }

        foreach (var qualifier in parsedSearch.NegatedQualifiers)
        {
            var field = qualifier.IsAttribute ? qualifier.Key : ResolveLogFieldKey(qualifier.Key);
            if (field is null)
            {
                continue;
            }

            filters.Add(new FieldTelemetryFilter
            {
                Field = field,
                Value = qualifier.Value,
                Condition = ToFilterCondition(qualifier.Operator, negated: true)
            });
        }
    }

    /// <summary>
    /// Converts search qualifiers into <see cref="FieldTelemetryFilter"/> objects for span/trace filtering.
    /// Maps user-facing qualifier keys (e.g., "status", "duration") to internal field constants.
    /// Attribute qualifiers (@-prefixed) pass the key directly for attribute fallback lookup.
    /// </summary>
    private static void AddSpanFiltersFromQualifiers(SearchFilter parsedSearch, List<TelemetryFilter> filters)
    {
        foreach (var qualifier in parsedSearch.Qualifiers)
        {
            var field = qualifier.IsAttribute ? qualifier.Key : ResolveSpanFieldKey(qualifier.Key);
            if (field is null)
            {
                continue;
            }

            filters.Add(new FieldTelemetryFilter
            {
                Field = field,
                Value = qualifier.Value,
                Condition = ToFilterCondition(qualifier.Operator, negated: false)
            });
        }

        foreach (var qualifier in parsedSearch.NegatedQualifiers)
        {
            var field = qualifier.IsAttribute ? qualifier.Key : ResolveSpanFieldKey(qualifier.Key);
            if (field is null)
            {
                continue;
            }

            filters.Add(new FieldTelemetryFilter
            {
                Field = field,
                Value = qualifier.Value,
                Condition = ToFilterCondition(qualifier.Operator, negated: true)
            });
        }
    }

    /// <summary>
    /// Maps user-facing log qualifier key names to internal field constants used by
    /// <see cref="OtlpLogEntry.GetFieldValue"/>. Returns null for unrecognized keys.
    /// </summary>
    private static string? ResolveLogFieldKey(string key) => key switch
    {
        "severity" or "level" => KnownStructuredLogFields.LevelField,
        "resource" => KnownResourceFields.ServiceNameField,
        "scope" or "category" => KnownStructuredLogFields.CategoryField,
        "message" or "msg" => KnownStructuredLogFields.MessageField,
        "trace-id" or "traceid" => KnownStructuredLogFields.TraceIdField,
        "span-id" or "spanid" => KnownStructuredLogFields.SpanIdField,
        "event" => KnownStructuredLogFields.EventNameField,
        _ => null
    };

    /// <summary>
    /// Maps user-facing span qualifier key names to internal field constants used by
    /// <see cref="OtlpSpan.GetFieldValue"/>. Returns null for unrecognized keys.
    /// </summary>
    private static string? ResolveSpanFieldKey(string key) => key switch
    {
        "name" => KnownTraceFields.NameField,
        "resource" => KnownResourceFields.ServiceNameField,
        "scope" or "source" => KnownSourceFields.NameField,
        "status" => KnownTraceFields.StatusField,
        "kind" => KnownTraceFields.KindField,
        "trace-id" or "traceid" => KnownTraceFields.TraceIdField,
        "span-id" or "spanid" => KnownTraceFields.SpanIdField,
        "duration" => KnownTraceFields.DurationField,
        _ => null
    };

    /// <summary>
    /// Maps a <see cref="ComparisonOperator"/> to the corresponding <see cref="FilterCondition"/>,
    /// inverting the logic when the qualifier is negated.
    /// </summary>
    private static FilterCondition ToFilterCondition(ComparisonOperator op, bool negated) => (op, negated) switch
    {
        (ComparisonOperator.Contains, false) => FilterCondition.Contains,
        (ComparisonOperator.Contains, true) => FilterCondition.NotContains,
        (ComparisonOperator.GreaterThan, false) => FilterCondition.GreaterThan,
        (ComparisonOperator.GreaterThan, true) => FilterCondition.LessThanOrEqual,
        (ComparisonOperator.GreaterThanOrEqual, false) => FilterCondition.GreaterThanOrEqual,
        (ComparisonOperator.GreaterThanOrEqual, true) => FilterCondition.LessThan,
        (ComparisonOperator.LessThan, false) => FilterCondition.LessThan,
        (ComparisonOperator.LessThan, true) => FilterCondition.GreaterThanOrEqual,
        (ComparisonOperator.LessThanOrEqual, false) => FilterCondition.LessThanOrEqual,
        (ComparisonOperator.LessThanOrEqual, true) => FilterCondition.GreaterThan,
        _ => FilterCondition.Contains
    };

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
