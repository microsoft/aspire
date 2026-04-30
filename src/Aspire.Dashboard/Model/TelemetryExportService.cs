// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Dashboard.Model.Serialization;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Otlp.Serialization;
using Aspire.Dashboard.Otlp.Model.MetricValues;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Aspire.Shared.ConsoleLogs;
using Aspire.Shared.Export;
using Aspire.Shared.Model.Serialization;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Service for exporting telemetry and console logs data.
/// </summary>
public sealed class TelemetryExportService
{
    private readonly TelemetryRepository _telemetryRepository;
    private readonly ConsoleLogsFetcher _consoleLogsFetcher;
    private readonly IDashboardClient _dashboardClient;
    private readonly IOutgoingPeerResolver[] _outgoingPeerResolvers;

    /// <summary>
    /// Initializes a new instance of the <see cref="TelemetryExportService"/> class.
    /// </summary>
    /// <param name="telemetryRepository">The telemetry repository.</param>
    /// <param name="consoleLogsFetcher">The console log fetcher.</param>
    /// <param name="dashboardClient">The dashboard client for fetching resources.</param>
    /// <param name="outgoingPeerResolvers">The outgoing peer resolvers for destination name resolution.</param>
    public TelemetryExportService(TelemetryRepository telemetryRepository, ConsoleLogsFetcher consoleLogsFetcher, IDashboardClient dashboardClient, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        _telemetryRepository = telemetryRepository;
        _consoleLogsFetcher = consoleLogsFetcher;
        _dashboardClient = dashboardClient;
        _outgoingPeerResolvers = outgoingPeerResolvers.ToArray();
    }

    /// <summary>
    /// Exports selected telemetry and console logs as a zip archive stream.
    /// </summary>
    /// <param name="selectedResources">Dictionary mapping resource names to the data types to export.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A memory stream containing the zip archive.</returns>
    public async Task<MemoryStream> ExportSelectedAsync(
        Dictionary<string, HashSet<AspireDataType>> selectedResources,
        CancellationToken cancellationToken)
    {
        var memoryStream = new MemoryStream();

        var exportArchive = new ExportArchive();

        var allOtlpResources = _telemetryRepository.GetResources();

        // Get resources from dashboard client if enabled and ResourceDetails is selected
        List<ResourceViewModel> resourceDetailsResources = [];
        var hasResourceDetailsSelected = selectedResources.Any(kvp => kvp.Value.Contains(AspireDataType.ResourceDetails));
        if (_dashboardClient.IsEnabled && hasResourceDetailsSelected)
        {
            var snapshot = _dashboardClient.GetResources();
            var resourcesByName = snapshot.ToDictionary(r => r.Name, StringComparers.ResourceName);

            resourceDetailsResources = selectedResources
                .Where(kvp => kvp.Value.Contains(AspireDataType.ResourceDetails) && resourcesByName.ContainsKey(kvp.Key))
                .Select(kvp => resourcesByName[kvp.Key])
                .ToList();
        }

        var consoleLogResources = selectedResources
            .Where(kvp => kvp.Value.Contains(AspireDataType.ConsoleLogs))
            .Select(kvp => kvp.Key)
            .ToHashSet(StringComparers.ResourceName);

        var structuredLogResources = allOtlpResources
            .Where(r => selectedResources.TryGetValue(r.ResourceKey.GetCompositeName(), out var types) && types.Contains(AspireDataType.StructuredLogs))
            .ToList();

        var traceResources = allOtlpResources
            .Where(r => selectedResources.TryGetValue(r.ResourceKey.GetCompositeName(), out var types) && types.Contains(AspireDataType.Traces))
            .ToList();

        var metricsResources = allOtlpResources
            .Where(r => selectedResources.TryGetValue(r.ResourceKey.GetCompositeName(), out var types) && types.Contains(AspireDataType.Metrics))
            .ToList();

        // Export resource details for selected resources
        if (resourceDetailsResources.Count > 0)
        {
            AddResources(exportArchive, resourceDetailsResources);
        }

        // Export console logs for selected resources
        if (consoleLogResources.Count > 0)
        {
            await AddConsoleLogsAsync(exportArchive, consoleLogResources, cancellationToken).ConfigureAwait(false);
        }

        // Export structured logs (OTLP JSON)
        if (structuredLogResources.Count > 0)
        {
            AddStructuredLogs(exportArchive, structuredLogResources);
        }

        // Export traces (OTLP JSON)
        if (traceResources.Count > 0)
        {
            AddTraces(exportArchive, traceResources);
        }

        // Export metrics (OTLP JSON)
        if (metricsResources.Count > 0)
        {
            AddMetrics(exportArchive, metricsResources);
        }

        exportArchive.WriteToStream(memoryStream);

        memoryStream.Position = 0;
        return memoryStream;
    }

    private async Task AddConsoleLogsAsync(ExportArchive exportArchive, HashSet<string> resourceNames, CancellationToken cancellationToken)
    {
        if (!_consoleLogsFetcher.IsEnabled)
        {
            return;
        }

        var allLogEntries = await _consoleLogsFetcher.FetchLogEntriesAsync(resourceNames, cancellationToken).ConfigureAwait(false);

        foreach (var (resourceName, logEntries) in allLogEntries)
        {
            var lines = logEntries
                .Where(e => e.Type is not LogEntryType.Pause)
                .Select(e => e.RawContent is not null ? AnsiParser.StripControlSequences(e.RawContent) : string.Empty)
                .ToList();
            exportArchive.ConsoleLogs[resourceName] = lines;
        }
    }

    private static void AddResources(ExportArchive exportArchive, List<ResourceViewModel> resources)
    {
        foreach (var resource in resources)
        {
            var resourceName = ResourceViewModel.GetResourceName(resource, resources);
            var resourceJson = CreateResourceJson(resource, resources);
            exportArchive.Resources[resourceName] = resourceJson;
        }
    }

    private void AddStructuredLogs(ExportArchive exportArchive, List<OtlpResource> resources)
    {
        foreach (var resource in resources)
        {
            var logs = _telemetryRepository.GetLogs(GetLogsContext.ForResourceKey(resource.ResourceKey));

            if (logs.Items.Count == 0)
            {
                continue;
            }

            var resourceName = OtlpHelpers.GetResourceName(resource, resources);
            exportArchive.StructuredLogs[resourceName] = ConvertLogsToOtlpJson(logs.Items);
        }
    }

    private void AddTraces(ExportArchive exportArchive, List<OtlpResource> resources)
    {
        foreach (var resource in resources)
        {
            var tracesResponse = _telemetryRepository.GetTraces(GetTracesRequest.ForResourceKey(resource.ResourceKey));

            if (tracesResponse.PagedResult.Items.Count == 0)
            {
                continue;
            }

            var resourceName = OtlpHelpers.GetResourceName(resource, resources);
            exportArchive.Traces[resourceName] = ConvertTracesToOtlpJson(tracesResponse.PagedResult.Items, _outgoingPeerResolvers);
        }
    }

    private void AddMetrics(ExportArchive exportArchive, List<OtlpResource> resources)
    {
        foreach (var resource in resources)
        {
            var instrumentSummaries = _telemetryRepository.GetInstrumentsSummaries(resource.ResourceKey);

            if (instrumentSummaries.Count == 0)
            {
                continue;
            }

            // Get full instrument data with values for each instrument
            var instrumentsData = new List<OtlpInstrumentData>();
            foreach (var summary in instrumentSummaries)
            {
                var instrumentData = _telemetryRepository.GetInstrument(new GetInstrumentRequest
                {
                    ResourceKey = resource.ResourceKey,
                    MeterName = summary.Parent.Name,
                    InstrumentName = summary.Name,
                    StartTime = DateTime.MinValue,
                    EndTime = DateTime.MaxValue
                });

                if (instrumentData is not null)
                {
                    instrumentsData.Add(instrumentData);
                }
            }

            if (instrumentsData.Count == 0)
            {
                continue;
            }

            var resourceName = OtlpHelpers.GetResourceName(resource, resources);
            exportArchive.Metrics[resourceName] = ConvertMetricsToOtlpJson(resource, instrumentsData);
        }
    }

    internal static OtlpTelemetryDataJson ConvertLogsToOtlpJson(List<OtlpLogEntry> logs)
    {
        // Group logs by resource and scope
        var resourceLogs = logs
            .GroupBy(l => l.ResourceView.ResourceKey)
            .Select(resourceGroup =>
            {
                var firstLog = resourceGroup.First();
                return new OtlpResourceLogsJson
                {
                    Resource = ConvertResourceView(firstLog.ResourceView),
                    ScopeLogs = resourceGroup
                        .GroupBy(l => l.Scope)
                        .Select(scopeGroup => new OtlpScopeLogsJson
                        {
                            Scope = ConvertScope(scopeGroup.Key),
                            LogRecords = scopeGroup.Select(ConvertLogEntry).ToArray()
                        }).ToArray()
                };
            }).ToArray();

        return new OtlpTelemetryDataJson
        {
            ResourceLogs = resourceLogs
        };
    }

    private static OtlpLogRecordJson ConvertLogEntry(OtlpLogEntry log)
    {
        return new OtlpLogRecordJson
        {
            TimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(log.TimeStamp),
            SeverityNumber = log.SeverityNumber,
            SeverityText = log.Severity.ToString(),
            Body = new OtlpAnyValueJson { StringValue = log.Message },
            Attributes = ConvertAttributes(log.Attributes, () =>
            [
                new KeyValuePair<string, string>(OtlpHelpers.AspireLogIdAttribute, log.InternalId.ToString(CultureInfo.InvariantCulture))
            ]),
            TraceId = string.IsNullOrEmpty(log.TraceId) ? null : log.TraceId,
            SpanId = string.IsNullOrEmpty(log.SpanId) ? null : log.SpanId,
            Flags = log.Flags,
            EventName = log.EventName
        };
    }

    internal static OtlpTelemetryDataJson ConvertSpansToOtlpJson(IReadOnlyList<OtlpSpan> spans, IOutgoingPeerResolver[] outgoingPeerResolvers)
    {
        // Group spans by resource and scope
        var resourceSpans = spans
            .GroupBy(s => s.Source.ResourceKey)
            .Select(resourceGroup =>
            {
                var firstSpan = resourceGroup.First();
                return new OtlpResourceSpansJson
                {
                    Resource = ConvertResourceView(firstSpan.Source),
                    ScopeSpans = resourceGroup
                        .GroupBy(s => s.Scope)
                        .Select(scopeGroup => new OtlpScopeSpansJson
                        {
                            Scope = ConvertScope(scopeGroup.Key),
                            Spans = scopeGroup.Select(s => ConvertSpan(s, outgoingPeerResolvers)).ToArray()
                        }).ToArray()
                };
            }).ToArray();

        return new OtlpTelemetryDataJson
        {
            ResourceSpans = resourceSpans
        };
    }

    internal static OtlpTelemetryDataJson ConvertTracesToOtlpJson(IReadOnlyList<OtlpTrace> traces, IOutgoingPeerResolver[] outgoingPeerResolvers)
    {
        // Group spans by resource and scope
        var allSpans = traces.SelectMany(t => t.Spans).ToList();
        return ConvertSpansToOtlpJson(allSpans, outgoingPeerResolvers);
    }

    internal static string ConvertSpanToJson(OtlpSpan span, IOutgoingPeerResolver[] outgoingPeerResolvers, List<OtlpLogEntry>? logs = null, bool indent = true)
    {
        var data = new OtlpTelemetryDataJson
        {
            ResourceSpans =
            [
                new OtlpResourceSpansJson
                {
                    Resource = ConvertResourceView(span.Source),
                    ScopeSpans =
                    [
                        new OtlpScopeSpansJson
                        {
                            Scope = ConvertScope(span.Scope),
                            Spans = [ConvertSpan(span, outgoingPeerResolvers)]
                        }
                    ]
                }
            ],
            ResourceLogs = ConvertLogsToResourceLogs(logs)
        };
        var options = indent ? OtlpJsonSerializerContext.IndentedOptions : OtlpJsonSerializerContext.DefaultOptions;
        return JsonSerializer.Serialize(data, options);
    }

    internal static string ConvertTraceToJson(OtlpTrace trace, IOutgoingPeerResolver[] outgoingPeerResolvers, List<OtlpLogEntry>? logs = null)
    {
        // Group spans by resource and scope
        var spansByResourceAndScope = trace.Spans
            .GroupBy(s => s.Source.ResourceKey)
            .Select(resourceGroup =>
            {
                var firstSpan = resourceGroup.First();
                return new OtlpResourceSpansJson
                {
                    Resource = ConvertResourceView(firstSpan.Source),
                    ScopeSpans = resourceGroup
                        .GroupBy(s => s.Scope)
                        .Select(scopeGroup => new OtlpScopeSpansJson
                        {
                            Scope = ConvertScope(scopeGroup.Key),
                            Spans = scopeGroup.Select(s => ConvertSpan(s, outgoingPeerResolvers)).ToArray()
                        }).ToArray()
                };
            }).ToArray();

        var data = new OtlpTelemetryDataJson
        {
            ResourceSpans = spansByResourceAndScope,
            ResourceLogs = ConvertLogsToResourceLogs(logs)
        };
        return JsonSerializer.Serialize(data, OtlpJsonSerializerContext.IndentedOptions);
    }

    internal static string ConvertLogEntryToJson(OtlpLogEntry logEntry)
    {
        var data = new OtlpTelemetryDataJson
        {
            ResourceLogs =
            [
                new OtlpResourceLogsJson
                {
                    Resource = ConvertResourceView(logEntry.ResourceView),
                    ScopeLogs =
                    [
                        new OtlpScopeLogsJson
                        {
                            Scope = ConvertScope(logEntry.Scope),
                            LogRecords = [ConvertLogEntry(logEntry)]
                        }
                    ]
                }
            ]
        };
        return JsonSerializer.Serialize(data, OtlpJsonSerializerContext.IndentedOptions);
    }

    private static OtlpSpanJson ConvertSpan(OtlpSpan span, IOutgoingPeerResolver[] outgoingPeerResolvers)
    {
        var destinationName = outgoingPeerResolvers.Length > 0
            ? GetDestination(span, outgoingPeerResolvers)
            : null;

        return new OtlpSpanJson
        {
            TraceId = span.TraceId,
            SpanId = span.SpanId,
            ParentSpanId = string.IsNullOrEmpty(span.ParentSpanId) ? null : span.ParentSpanId,
            Name = span.Name,
            Kind = (int)span.Kind,
            StartTimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(span.StartTime),
            EndTimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(span.EndTime),
            Attributes = ConvertAttributes(span.Attributes, destinationName is not null
                ? () => [new KeyValuePair<string, string>(OtlpHelpers.AspireDestinationNameAttribute, destinationName)]
                : null),
            Status = ConvertSpanStatus(span.Status, span.StatusMessage),
            Events = span.Events.Count > 0 ? span.Events.Select(ConvertSpanEvent).ToArray() : null,
            Links = span.Links.Count > 0 ? span.Links.Select(ConvertSpanLink).ToArray() : null,
            TraceState = span.State
        };
    }

    private static OtlpSpanStatusJson? ConvertSpanStatus(OtlpSpanStatusCode status, string? statusMessage)
    {
        if (status == OtlpSpanStatusCode.Unset && string.IsNullOrEmpty(statusMessage))
        {
            return null;
        }

        return new OtlpSpanStatusJson
        {
            Code = (int)status,
            Message = statusMessage
        };
    }

    private static OtlpSpanEventJson ConvertSpanEvent(OtlpSpanEvent evt)
    {
        return new OtlpSpanEventJson
        {
            TimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(evt.Time),
            Name = evt.Name,
            Attributes = ConvertAttributes(evt.Attributes)
        };
    }

    private static OtlpSpanLinkJson ConvertSpanLink(OtlpSpanLink link)
    {
        return new OtlpSpanLinkJson
        {
            TraceId = link.TraceId,
            SpanId = link.SpanId,
            TraceState = link.TraceState,
            Attributes = ConvertAttributes(link.Attributes)
        };
    }

    private static OtlpResourceLogsJson[]? ConvertLogsToResourceLogs(List<OtlpLogEntry>? logs)
    {
        if (logs is null || logs.Count == 0)
        {
            return null;
        }

        // Group logs by resource and scope
        return logs
            .GroupBy(l => l.ResourceView.ResourceKey)
            .Select(resourceGroup =>
            {
                var firstLog = resourceGroup.First();
                return new OtlpResourceLogsJson
                {
                    Resource = ConvertResourceView(firstLog.ResourceView),
                    ScopeLogs = resourceGroup
                        .GroupBy(l => l.Scope)
                        .Select(scopeGroup => new OtlpScopeLogsJson
                        {
                            Scope = ConvertScope(scopeGroup.Key),
                            LogRecords = scopeGroup.Select(ConvertLogEntry).ToArray()
                        }).ToArray()
                };
            }).ToArray();
    }

    internal static OtlpTelemetryDataJson ConvertMetricsToOtlpJson(OtlpResource resource, List<OtlpInstrumentData> instruments)
    {
        // Group instruments by scope
        var instrumentsByScope = instruments.GroupBy(i => i.Summary.Parent);

        var scopeMetrics = instrumentsByScope.Select(scopeGroup => new OtlpScopeMetricsJson
        {
            Scope = ConvertScope(scopeGroup.Key),
            Metrics = scopeGroup.Select(ConvertInstrument).ToArray()
        }).ToArray();

        return new OtlpTelemetryDataJson
        {
            ResourceMetrics =
            [
                new OtlpResourceMetricsJson
                {
                    Resource = ConvertResourceView(resource.GetViews()[0]),
                    ScopeMetrics = scopeMetrics
                }
            ]
        };
    }

    private static OtlpMetricJson ConvertInstrument(OtlpInstrumentData instrumentData)
    {
        var summary = instrumentData.Summary;
        var metric = new OtlpMetricJson
        {
            Name = summary.Name,
            Description = summary.Description,
            Unit = summary.Unit
        };

        // Convert dimensions to data points based on metric type
        switch (summary.Type)
        {
            case OtlpInstrumentType.Gauge:
                metric.Gauge = new OtlpGaugeJson
                {
                    DataPoints = ConvertNumberDataPoints(instrumentData.Dimensions)
                };
                break;
            case OtlpInstrumentType.Sum:
                metric.Sum = new OtlpSumJson
                {
                    DataPoints = ConvertNumberDataPoints(instrumentData.Dimensions),
                    AggregationTemporality = (int)summary.AggregationTemporality
                };
                break;
            case OtlpInstrumentType.Histogram:
                metric.Histogram = new OtlpHistogramJson
                {
                    DataPoints = ConvertHistogramDataPoints(instrumentData.Dimensions),
                    AggregationTemporality = (int)summary.AggregationTemporality
                };
                break;
        }

        return metric;
    }

    private static OtlpNumberDataPointJson[] ConvertNumberDataPoints(List<DimensionScope> dimensions)
    {
        var dataPoints = new List<OtlpNumberDataPointJson>();

        foreach (var dimension in dimensions)
        {
            foreach (var value in dimension.Values)
            {
                var dataPoint = new OtlpNumberDataPointJson
                {
                    Attributes = ConvertAttributes(dimension.Attributes),
                    StartTimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(value.Start),
                    TimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(value.End),
                    Exemplars = value.HasExemplars ? ConvertExemplars(value.Exemplars) : null
                };

                // Set the value based on the metric value type
                if (value is MetricValue<long> longValue)
                {
                    dataPoint.AsInt = longValue.Value;
                }
                else if (value is MetricValue<double> doubleValue)
                {
                    dataPoint.AsDouble = doubleValue.Value;
                }

                dataPoints.Add(dataPoint);
            }
        }

        return dataPoints.ToArray();
    }

    private static OtlpHistogramDataPointJson[] ConvertHistogramDataPoints(List<DimensionScope> dimensions)
    {
        var dataPoints = new List<OtlpHistogramDataPointJson>();

        foreach (var dimension in dimensions)
        {
            foreach (var value in dimension.Values)
            {
                if (value is not HistogramValue histogramValue)
                {
                    continue;
                }

                var dataPoint = new OtlpHistogramDataPointJson
                {
                    Attributes = ConvertAttributes(dimension.Attributes),
                    StartTimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(value.Start),
                    TimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(value.End),
                    Count = histogramValue.Count,
                    Sum = histogramValue.Sum,
                    BucketCounts = histogramValue.Values,
                    ExplicitBounds = histogramValue.ExplicitBounds,
                    Exemplars = value.HasExemplars ? ConvertExemplars(value.Exemplars) : null
                };

                dataPoints.Add(dataPoint);
            }
        }

        return dataPoints.ToArray();
    }

    private static OtlpExemplarJson[] ConvertExemplars(List<MetricsExemplar> exemplars)
    {
        return exemplars.Select(e => new OtlpExemplarJson
        {
            TimeUnixNano = OtlpHelpers.DateTimeToUnixNanoseconds(e.Start),
            AsDouble = e.Value,
            SpanId = e.SpanId,
            TraceId = e.TraceId,
            FilteredAttributes = ConvertAttributes(e.Attributes)
        }).ToArray();
    }

    private static OtlpResourceJson ConvertResourceView(OtlpResourceView resourceView)
    {
        var attributes = new List<OtlpKeyValueJson>
        {
            new OtlpKeyValueJson
            {
                Key = OtlpResource.SERVICE_NAME,
                Value = new OtlpAnyValueJson { StringValue = resourceView.Resource.ResourceName }
            },
            new OtlpKeyValueJson
            {
                Key = OtlpResource.SERVICE_INSTANCE_ID,
                Value = new OtlpAnyValueJson { StringValue = resourceView.Resource.InstanceId }
            }
        };

        // Include additional properties from the resource view
        foreach (var property in resourceView.Properties)
        {
            attributes.Add(new OtlpKeyValueJson
            {
                Key = property.Key,
                Value = new OtlpAnyValueJson { StringValue = property.Value }
            });
        }

        return new OtlpResourceJson
        {
            Attributes = attributes.ToArray()
        };
    }

    private static OtlpInstrumentationScopeJson ConvertScope(OtlpScope scope)
    {
        return new OtlpInstrumentationScopeJson
        {
            Name = scope.Name,
            Version = string.IsNullOrEmpty(scope.Version) ? null : scope.Version,
            Attributes = scope.Attributes.Length > 0 ? ConvertAttributes(scope.Attributes) : null
        };
    }

    private static OtlpKeyValueJson[]? ConvertAttributes(KeyValuePair<string, string>[] attributes, Func<KeyValuePair<string, string>[]>? getAdditionalAttributes = null)
    {
        var additionalAttributes = getAdditionalAttributes?.Invoke();
        var additionalCount = additionalAttributes?.Length ?? 0;

        if (attributes.Length == 0 && additionalCount == 0)
        {
            return null;
        }

        var result = new OtlpKeyValueJson[attributes.Length + additionalCount];

        for (var i = 0; i < attributes.Length; i++)
        {
            result[i] = new OtlpKeyValueJson
            {
                Key = attributes[i].Key,
                Value = new OtlpAnyValueJson { StringValue = attributes[i].Value }
            };
        }

        if (additionalAttributes is not null)
        {
            for (var i = 0; i < additionalAttributes.Length; i++)
            {
                result[attributes.Length + i] = new OtlpKeyValueJson
                {
                    Key = additionalAttributes[i].Key,
                    Value = new OtlpAnyValueJson { StringValue = additionalAttributes[i].Value }
                };
            }
        }

        return result;
    }

    internal static string ConvertResourceToJson(ResourceViewModel resource, IReadOnlyList<ResourceViewModel> allResources)
    {
        var resourceJson = CreateResourceJson(resource, allResources);
        return JsonSerializer.Serialize(resourceJson, ResourceJsonSerializerContext.IndentedOptions);
    }

    internal static ResourceJson CreateResourceJson(ResourceViewModel resource, IReadOnlyList<ResourceViewModel> allResources)
    {
        // Build relationships by matching DisplayName and filtering out hidden resources
        ResourceRelationshipJson[]? relationshipsJson = null;
        if (resource.Relationships.Length > 0)
        {
            var relationships = new List<ResourceRelationshipJson>();
            foreach (var relationship in resource.Relationships)
            {
                var matches = allResources
                    .Where(r => string.Equals(r.DisplayName, relationship.ResourceName, StringComparisons.ResourceName))
                    .Where(r => r.KnownState != KnownResourceState.Hidden)
                    .ToList();

                foreach (var match in matches)
                {
                    relationships.Add(new ResourceRelationshipJson
                    {
                        Type = relationship.Type,
                        ResourceName = ResourceViewModel.GetResourceName(match, allResources)
                    });
                }
            }
            relationshipsJson = relationships.ToArray();
        }

        var resourceJson = new ResourceJson
        {
            Name = resource.Name,
            DisplayName = resource.DisplayName,
            ResourceType = resource.ResourceType,
            Uid = resource.Uid,
            State = resource.State,
            CreationTimestamp = resource.CreationTimeStamp,
            StartTimestamp = resource.StartTimeStamp,
            StopTimestamp = resource.StopTimeStamp,
            HealthStatus = resource.HealthStatus?.ToString(),
            Urls = resource.Urls.Length > 0
                ? resource.Urls.Select(u => new ResourceUrlJson
                {
                    Name = u.EndpointName,
                    DisplayName = u.DisplayProperties.DisplayName is { Length: > 0 } n ? n : null,
                    Url = u.Url.ToString()
                }).ToArray()
                : null,
            Volumes = resource.Volumes.Length > 0
                ? resource.Volumes.Select(v => new ResourceVolumeJson
                {
                    Source = v.Source,
                    Target = v.Target,
                    MountType = v.MountType,
                    IsReadOnly = v.IsReadOnly
                }).ToArray()
                : null,
            Environment = resource.Environment.Length > 0
                ? resource.Environment.Where(e => e.FromSpec).OrderBy(e => e.Name).ToDistinctDictionary(e => e.Name, e => e.Value)
                : null,
            HealthReports = resource.HealthReports.Length > 0
                ? resource.HealthReports.OrderBy(h => h.Name).ToDistinctDictionary(
                    h => h.Name,
                    h => new ResourceHealthReportJson
                    {
                        Status = h.HealthStatus?.ToString(),
                        Description = h.Description,
                        ExceptionMessage = h.ExceptionText
                    })
                : null,
            Properties = resource.Properties.Count > 0
                ? resource.Properties.OrderBy(p => p.Key).ToDistinctDictionary(
                    p => p.Key,
                    p => p.Value.Value.TryConvertToString(out var value) ? value : null)
                : null,
            Relationships = relationshipsJson,
            Commands = resource.Commands.Length > 0
                ? resource.Commands
                    .Where(c => c.State == CommandViewModelState.Enabled)
                    .OrderBy(c => c.Name)
                    .ToDistinctDictionary(
                        c => c.Name,
                        c => new ResourceCommandJson
                        {
                            Description = c.GetDisplayDescription()
                        })
                : null,
            Source = ResourceSourceViewModel.GetSourceViewModel(resource)?.Value
        };

        return resourceJson;
    }

    /// <summary>
    /// Gets the destination name for a span by resolving uninstrumented peer names.
    /// </summary>
    private static string? GetDestination(OtlpSpan span, IEnumerable<IOutgoingPeerResolver> outgoingPeerResolvers)
    {
        // Attempt to resolve uninstrumented peer to a friendly name from the span.
        foreach (var resolver in outgoingPeerResolvers)
        {
            if (resolver.TryResolvePeer(span.Attributes, out var name, out _))
            {
                return name;
            }
        }

        // Fallback to the peer address.
        return span.Attributes.GetPeerAddress();
    }
}
