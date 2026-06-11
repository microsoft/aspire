// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;

namespace Aspire.Dashboard.Model;

[DebuggerDisplay("Span = {Span.SpanId}, Title = {Title}")]
public sealed class SpanDetailsViewModel
{
    public required OtlpSpan Span { get; init; }
    public required List<TelemetryPropertyViewModel> Properties { get; init; }
    public required List<SpanLinkViewModel> Links { get; init; }
    public required List<SpanLinkViewModel> Backlinks { get; init; }
    public required string Title { get; init; }
    public required List<OtlpResource> Resources { get; init; }

    public static SpanDetailsViewModel Create(OtlpSpan span, TelemetryRepository telemetryRepository, List<OtlpResource> resources)
    {
        ArgumentNullException.ThrowIfNull(span);
        ArgumentNullException.ThrowIfNull(telemetryRepository);
        ArgumentNullException.ThrowIfNull(resources);

        var entryProperties = span.GetKnownProperties().Select(CreateTelemetryProperty).ToList();
        if (span.GetDestination() is { } destination)
        {
            entryProperties.Add(new TelemetryPropertyViewModel
            {
                Name = "Destination",
                Key = KnownTraceFields.DestinationField,
                Value = OtlpHelpers.GetResourceName(destination, resources)
            });
        }
        entryProperties.AddRange(span.GetAttributeProperties().Select(CreateTelemetryProperty));

        var traceCache = new Dictionary<string, OtlpTrace>(StringComparer.Ordinal);

        var links = span.Links.Select(l => CreateLinkViewModel(l.TraceId, l.SpanId, l.Attributes, telemetryRepository, traceCache)).ToList();
        var backlinks = span.BackLinks.Select(l => CreateLinkViewModel(l.SourceTraceId, l.SourceSpanId, l.Attributes, telemetryRepository, traceCache)).ToList();

        var spanDetailsViewModel = new SpanDetailsViewModel
        {
            Span = span,
            Resources = resources,
            Properties = entryProperties,
            Title = SpanWaterfallViewModel.GetTitle(span, resources),
            Links = links,
            Backlinks = backlinks,
        };
        return spanDetailsViewModel;

        static TelemetryPropertyViewModel CreateTelemetryProperty(OtlpDisplayField f)
        {
            return new TelemetryPropertyViewModel { Name = f.DisplayName, Key = f.Key, Value = f.Value };
        }
    }

    private static SpanLinkViewModel CreateLinkViewModel(string traceId, string spanId, KeyValuePair<string, string>[] attributes, TelemetryRepository telemetryRepository, Dictionary<string, OtlpTrace> traceCache)
    {
        ref var trace = ref CollectionsMarshal.GetValueRefOrAddDefault(traceCache, traceId, out _);
        // Adds to dictionary if not present.
        trace ??= telemetryRepository.GetTrace(traceId);

        var linkSpan = trace?.Spans.FirstOrDefault(s => s.SpanId == spanId);

        return new SpanLinkViewModel
        {
            TraceId = traceId,
            SpanId = spanId,
            Attributes = attributes,
            Span = linkSpan,
        };
    }
}
