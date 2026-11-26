// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// The cards of an overview, in the order they can appear.
/// </summary>
public enum OverviewCard
{
    Status,
    Endpoints,
    Relationships,
    Console,
    Logs,
    Traces,
    Metrics,
    Properties,
    Attributes,
    Environment,
    Volumes,
    Health
}

/// <summary>
/// Packs cards into rows of a 12 column grid in page order. Each card requests a span based on its content, and the
/// spare columns at the end of a row are shared between that row's cards so rows never have trailing gaps.
/// Medium widths use a separate two-per-row packing where wide cards take the full row.
/// </summary>
public sealed class OverviewCardLayout
{
    private const int Columns = 12;

    private readonly Dictionary<OverviewCard, int> _spans;
    private readonly Dictionary<OverviewCard, int> _mediumSpans;

    private OverviewCardLayout(Dictionary<OverviewCard, int> spans, Dictionary<OverviewCard, int> mediumSpans)
    {
        _spans = spans;
        _mediumSpans = mediumSpans;
    }

    public static OverviewCardLayout Create(IReadOnlyList<(OverviewCard Card, int Span)> cards)
    {
        return new OverviewCardLayout(
            Pack(cards),
            Pack(cards.Select(c => (c.Card, c.Span >= 7 ? Columns : Columns / 2)).ToList()));
    }

    public string Style(OverviewCard card)
    {
        return string.Create(CultureInfo.InvariantCulture, $"--overview-span: {_spans.GetValueOrDefault(card, Columns)}; --overview-span-md: {_mediumSpans.GetValueOrDefault(card, Columns)}; --overview-card-height: {GetHeight(card)}px;");
    }

    // Bound scrollable content while allowing each grid row to fit its tallest card.
    // Terminals use the console limit as their minimum interactive viewport instead.
    private static int GetHeight(OverviewCard card) => card switch
    {
        OverviewCard.Status or OverviewCard.Endpoints or OverviewCard.Metrics => 260,
        OverviewCard.Console or OverviewCard.Logs => 300,
        OverviewCard.Traces or OverviewCard.Relationships => 300,
        OverviewCard.Properties or OverviewCard.Attributes or OverviewCard.Environment => 340,
        _ => 240
    };

    private static Dictionary<OverviewCard, int> Pack(IReadOnlyList<(OverviewCard Card, int Span)> cards)
    {
        var result = new Dictionary<OverviewCard, int>();
        var row = new List<(OverviewCard Card, int Span)>();
        var used = 0;

        foreach (var (card, requested) in cards)
        {
            var span = Math.Clamp(requested, 1, Columns);
            if (used + span > Columns)
            {
                CompleteRow();
            }
            row.Add((card, span));
            used += span;
        }
        CompleteRow();

        return result;

        void CompleteRow()
        {
            var spare = Columns - used;
            for (var i = 0; i < row.Count; i++)
            {
                result[row[i].Card] = row[i].Span + (spare / row.Count) + (i < spare % row.Count ? 1 : 0);
            }
            row.Clear();
            used = 0;
        }
    }
}
