// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Otlp.Model;

namespace Aspire.Dashboard.Model;

public static class TraceHelpers
{
    /// <summary>
    /// Recursively visit spans for a trace. Start visiting spans from unrooted spans.
    /// </summary>
    public static void VisitSpans<TState>(OtlpTrace trace, Func<OtlpSpan, TState, TState> spanAction, TState state)
    {
        // Calculate span hierarchy.
        var spanLookup = new Dictionary<OtlpSpan, List<OtlpSpan>>();
        var unrootedSpans = new List<OtlpSpan>();
        foreach (var item in trace.Spans)
        {
            if (string.IsNullOrEmpty(item.ParentSpanId) || !trace.Spans.TryGetValue(item.ParentSpanId, out var parentSpan))
            {
                unrootedSpans.Add(item);
            }
            else
            {
                ref var childSpans = ref CollectionsMarshal.GetValueRefOrAddDefault(spanLookup, parentSpan, out _);
                childSpans ??= [];
                childSpans.Add(item);
            }
        }

        var orderByFunc = static (OtlpSpan s) => s.StartTime;

        foreach (var unrootedSpan in unrootedSpans.OrderBy(orderByFunc))
        {
            var newState = spanAction(unrootedSpan, state);

            Visit(spanLookup, unrootedSpan, spanAction, newState, orderByFunc);
        }

        static void Visit(Dictionary<OtlpSpan, List<OtlpSpan>> spanLookup, OtlpSpan span, Func<OtlpSpan, TState, TState> spanAction, TState state, Func<OtlpSpan, DateTime> orderByFunc)
        {
            if (spanLookup.TryGetValue(span, out var childSpans))
            {
                foreach (var childSpan in childSpans.OrderBy(orderByFunc))
                {
                    var newState = spanAction(childSpan, state);

                    Visit(spanLookup, childSpan, spanAction, newState, orderByFunc);
                }
            }
        }
    }

    private readonly record struct OrderedResourcesState(DateTime? CurrentMinDate);

    /// <summary>
    /// Get resources for a trace, with grouped information, and ordered using min date.
    /// It is possible for spans to arrive with dates that are out of order (i.e. child span has earlier
    /// start date than the parent) so ensure it isn't possible for a child to appear before parent.
    /// </summary>
    public static IEnumerable<OrderedResource> GetOrderedResources(OtlpTrace trace)
    {
        var resourceFirstTimes = new Dictionary<OtlpResource, OrderedResource>();

        VisitSpans(trace, (OtlpSpan span, OrderedResourcesState state) =>
        {
            var currentMinDate = (state.CurrentMinDate == null || state.CurrentMinDate < span.StartTime)
                ? span.StartTime
                : state.CurrentMinDate.Value;

            ProcessSpanResource(span, span.Source.Resource, resourceFirstTimes, currentMinDate);
            if (span.UninstrumentedPeer is { } peer)
            {
                ProcessSpanResource(span, peer, resourceFirstTimes, currentMinDate);
            }

            return new OrderedResourcesState(currentMinDate);
        }, new OrderedResourcesState(null));

        return resourceFirstTimes.Select(kvp => kvp.Value)
            .OrderBy(s => s.FirstDateTime)
            .ThenBy(s => s.Index);
    }

    private static void ProcessSpanResource(OtlpSpan span, OtlpResource resource, Dictionary<OtlpResource, OrderedResource> resourceFirstTimes, DateTime currentMinDate)
    {
        if (resourceFirstTimes.TryGetValue(resource, out var orderedResource))
        {
            if (currentMinDate < orderedResource.FirstDateTime)
            {
                orderedResource.FirstDateTime = currentMinDate;
            }

            if (span.Status == OtlpSpanStatusCode.Error)
            {
                orderedResource.ErroredSpans++;
            }

            orderedResource.TotalSpans++;
        }
        else
        {
            resourceFirstTimes.Add(
                resource,
                new OrderedResource(resource, resourceFirstTimes.Count, currentMinDate, totalSpans: 1, erroredSpans: span.Status == OtlpSpanStatusCode.Error ? 1 : 0));
        }
    }

    public static DeckIconName? TryGetSpanIcon(OtlpSpan span)
    {
        switch (span.Kind)
        {
            case OtlpSpanKind.Server:
                return DeckIconName.Server;
            case OtlpSpanKind.Consumer:
                // Messaging consumers read from a queue/mailbox; other consumers are generic processors.
                return span.Attributes.HasKey("messaging.system") ? DeckIconName.Mail : DeckIconName.Settings;
            case OtlpSpanKind.Producer:
                // Messaging producers send mail; other producers emit a payload/box.
                return span.Attributes.HasKey("messaging.system") ? DeckIconName.Mail : DeckIconName.Container;
            default:
                return null;
        }
    }
}

public sealed class OrderedResource(OtlpResource resource, int index, DateTime firstDateTime, int totalSpans, int erroredSpans)
{
    public OtlpResource Resource { get; } = resource;
    public int Index { get; } = index;
    public DateTime FirstDateTime { get; set; } = firstDateTime;
    public int TotalSpans { get; set; } = totalSpans;
    public int ErroredSpans { get; set; } = erroredSpans;
}
