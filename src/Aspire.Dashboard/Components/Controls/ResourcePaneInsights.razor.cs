// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Extensions;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public partial class ResourcePaneInsights
{
    private ResourcePaneActivitySnapshot? _snapshot;

    [Parameter, EditorRequired]
    public required ResourcesLayout Layout { get; set; }

    [Parameter, EditorRequired]
    public required ResourceKey ResourceKey { get; set; }

    [Parameter, EditorRequired]
    public required string ResourceName { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Layout> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    protected override void OnInitialized() => Layout.PaneActivityChanged += OnActivityChanged;

    protected override void OnParametersSet() => _snapshot = Layout.GetPaneActivitySnapshot(ResourceKey);

    private void OnActivityChanged()
    {
        var snapshot = Layout.GetPaneActivitySnapshot(ResourceKey);
        if (snapshot != _snapshot)
        {
            _snapshot = snapshot;
            StateHasChanged();
        }
    }

    private string GetContext(ResourcePaneActivitySnapshot snapshot)
    {
        var context = string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.ResourcePaneActivityContext)],
            ResourceName,
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, snapshot.WindowStart),
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, snapshot.WindowEnd),
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, snapshot.Activity.LatestStartTime),
            FormatHelpers.FormatTimeWithOptionalDate(TimeProvider, snapshot.Refreshed));
        var samples = string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Layout.ResourcePaneActivitySamples)], snapshot.Activity.DurationSampleCount);
        return snapshot.Sampled
            ? $"{context} {samples} {Loc[nameof(Resources.Layout.ResourcePaneActivitySampled)]}"
            : $"{context} {samples}";
    }

    private string GetTracesUrl(ResourcePaneActivitySnapshot snapshot, bool failed)
    {
        List<FieldTelemetryFilter> filters =
        [
            new() { Field = KnownTraceFields.TimestampField, Condition = FilterCondition.GreaterThanOrEqual, Value = snapshot.WindowStart.ToString("O", CultureInfo.InvariantCulture) },
            new() { Field = KnownTraceFields.TimestampField, Condition = FilterCondition.LessThan, Value = snapshot.WindowEnd.ToString("O", CultureInfo.InvariantCulture) }
        ];
        if (failed)
        {
            filters.Add(new() { Field = KnownTraceFields.StatusField, Condition = FilterCondition.Equals, Value = nameof(OtlpSpanStatusCode.Error) });
        }

        // The timestamp filter grammar escapes ':' before URL encoding. Preserve both
        // layers (e.g. "%253A"); the traces page decodes them in the reverse order.
        return DashboardUrls.TracesUrl(ResourceKey.GetCompositeName(), filters: TelemetryFilterFormatter.SerializeFiltersToString(filters));
    }

    public void Dispose() => Layout.PaneActivityChanged -= OnActivityChanged;
}
