// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

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
        var resourceFirstTimes = new Dictionary<OtlpResource, OrderedResource>(OtlpResourceEqualityComparer.Instance);

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

    private static readonly Icon s_serverFilled = new Icons.Filled.Size16.Server();
    private static readonly Icon s_serverRegular = new Icons.Regular.Size16.Server();

    private static readonly Icon s_mailboxFilled = new Icons.Filled.Size16.Mailbox();
    private static readonly Icon s_mailboxRegular = new Icons.Regular.Size16.Mailbox();

    private static readonly Icon s_contentSettingsFilled = new Icons.Filled.Size16.ContentSettings();
    private static readonly Icon s_contentSettingsRegular = new Icons.Regular.Size16.ContentSettings();

    private static readonly Icon s_mailFilled = new Icons.Filled.Size16.Mail();
    private static readonly Icon s_mailRegular = new Icons.Regular.Size16.Mail();

    private static readonly Icon s_boxFilled = new Icons.Filled.Size16.Box();
    private static readonly Icon s_boxRegular = new Icons.Regular.Size16.Box();

    public static Icon? TryGetSpanIcon(OtlpSpan span, IconVariant iconVariant)
    {
        switch (span.Kind)
        {
            case OtlpSpanKind.Server:
                return GetIcon(s_serverRegular, s_serverFilled, iconVariant);
            case OtlpSpanKind.Consumer:
                if (span.Attributes.HasKey("messaging.system"))
                {
                    return GetIcon(s_mailboxRegular, s_mailboxFilled, iconVariant);
                }
                else
                {
                    return GetIcon(s_contentSettingsRegular, s_contentSettingsFilled, iconVariant);
                }
            case OtlpSpanKind.Producer:
                if (span.Attributes.HasKey("messaging.system"))
                {
                    return GetIcon(s_mailRegular, s_mailFilled, iconVariant);
                }
                else
                {
                    return GetIcon(s_boxRegular, s_boxFilled, iconVariant);
                }
            default:
                return null;
        }

        static Icon GetIcon(Icon regular, Icon filled, IconVariant iconVariant)
        {
            return iconVariant switch
            {
                IconVariant.Filled => filled,
                IconVariant.Regular => regular,
                _ => throw new ArgumentOutOfRangeException(nameof(iconVariant), iconVariant, $"Unsupported icon variant: {iconVariant}")
            };
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
