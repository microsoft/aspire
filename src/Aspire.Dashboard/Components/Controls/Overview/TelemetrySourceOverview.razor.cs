// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls.Overview;

/// <summary>
/// Summarizes a telemetry source: the resource attributes it reports and the telemetry it has sent. The resource
/// overview loads the telemetry, like it does for a resource of the app model, and passes it in.
/// </summary>
public sealed partial class TelemetrySourceOverview : ComponentBase
{
    private const int AttributesPreviewCount = 10;

    private bool _attributesExpanded;
    private ResourceKey? _renderedSourceKey;

    /// <summary>
    /// Gets or sets the name the source is displayed and selected by.
    /// </summary>
    [Parameter, EditorRequired]
    public required string Name { get; set; }

    [Parameter, EditorRequired]
    public required OtlpResource Source { get; set; }

    [Parameter]
    public OverviewTelemetry? Telemetry { get; set; }

    [Parameter]
    public bool TelemetryLoaded { get; set; }

    [Parameter]
    public bool HideEmptyPanels { get; set; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Resources> Loc { get; init; }

    protected override void OnParametersSet()
    {
        // The component is reused when another source is selected. A long list expanded for one source shouldn't
        // stay expanded for the next.
        if (_renderedSourceKey != Source.ResourceKey)
        {
            _renderedSourceKey = Source.ResourceKey;
            _attributesExpanded = false;
        }
    }

    private void ToggleAttributesExpanded() => _attributesExpanded = !_attributesExpanded;

    private ViewState GetView()
    {
        var attributes = GetAttributeItems(Source);
        var showLogs = !TelemetryLoaded || Telemetry is { LogCount: > 0 };
        var showTraces = !TelemetryLoaded || Telemetry is { TraceCount: > 0 };
        var showMetrics = !TelemetryLoaded || Telemetry is { Instruments.Count: > 0 };

        // Keep loading panels so their space is reserved. Hidden cards stay mounted, but only visible cards participate
        // in grid packing. The spans match the resource overview's cards, with the attributes in the properties' place.
        List<(OverviewCard Card, int Span, bool HasContent)> cardSpans =
        [
            (OverviewCard.Status, 3, true),
            (OverviewCard.Metrics, 5, showMetrics),
            (OverviewCard.Logs, 6, showLogs),
            (OverviewCard.Traces, 6, showTraces),
            (OverviewCard.Attributes, 6, attributes.Count > 0)
        ];
        var hiddenCards = cardSpans.Where(c => HideEmptyPanels && !c.HasContent).Select(c => c.Card).ToHashSet();
        var layout = OverviewCardLayout.Create(cardSpans.Where(c => !hiddenCards.Contains(c.Card)).Select(c => (c.Card, c.Span)).ToList());

        return new ViewState(attributes, showLogs, showTraces, showMetrics, hiddenCards, layout);
    }

    // A source can report different attributes over time, for example after a configuration change. Each value is
    // listed, so a key with several values has a row for each of them.
    private static List<TelemetryPropertyViewModel> GetAttributeItems(OtlpResource source) =>
        source.GetAttributes()
            .Select(attribute => new TelemetryPropertyViewModel
            {
                Name = attribute.Key,
                Value = attribute.Value,
                Key = attribute
            })
            .ToList();

    private sealed record ViewState(
        List<TelemetryPropertyViewModel> Attributes,
        bool ShowLogs,
        bool ShowTraces,
        bool ShowMetrics,
        HashSet<OverviewCard> HiddenCards,
        OverviewCardLayout Layout);
}
