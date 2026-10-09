// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class TreeMetricSelector
{
    private readonly Dictionary<string, bool> _meterExpansion = new(StringComparer.Ordinal);

    // Cache keyed by instrument so we don't re-query the telemetry store on every render.
    // Cleared whenever the selected resource changes so stale "no data" results from a
    // previous resource aren't carried over.
    private readonly Dictionary<OtlpInstrumentKey, bool> _instrumentHasDataCache = new();

    [Parameter, EditorRequired]
    public required Func<Task> HandleSelectedTreeItemChangedAsync { get; set; }

    [Parameter, EditorRequired]
    public required Metrics.MetricsViewModel PageViewModel { get; set; }

    [Parameter]
    public bool IncludeLabel { get; set; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    public void OnResourceChanged()
    {
        _meterExpansion.Clear();
        _instrumentHasDataCache.Clear();
        StateHasChanged();
    }

    // Lets the tree deemphasize instruments that haven't recorded any data yet, so the user
    // can tell at a glance which metrics are worth selecting instead of clicking through each one.
    private bool HasInstrumentData(OtlpInstrumentSummary instrument)
    {
        var key = instrument.GetKey();
        if (_instrumentHasDataCache.TryGetValue(key, out var hasData))
        {
            return hasData;
        }

        var resourceKey = PageViewModel.SelectedResource.Id?.GetResourceKey();
        hasData = resourceKey is { } rk
            && TelemetryRepository.GetInstrumentLatestEndTime(rk, instrument.Parent.Name, instrument.Name) is not null;

        _instrumentHasDataCache[key] = hasData;
        return hasData;
    }

    private string? GetSelectedTreeItemId()
    {
        if (PageViewModel.SelectedInstrument is { } instrument)
        {
            return GetInstrumentTreeItemId(instrument.Parent.Name, instrument.Name);
        }

        return PageViewModel.SelectedMeter is { } meterName ? GetMeterTreeItemId(meterName) : null;
    }

    private static string GetMeterTreeItemId(string meterName)
    {
        return $"metric-meter-{Uri.EscapeDataString(meterName)}";
    }

    private static string GetInstrumentTreeItemId(string meterName, string instrumentName)
    {
        return $"metric-instrument-{meterName.Length}-{Uri.EscapeDataString(meterName)}-{Uri.EscapeDataString(instrumentName)}";
    }

    private bool IsMeterExpanded(string meterName)
    {
        return _meterExpansion.TryGetValue(meterName, out var expanded)
            ? expanded
            : true;
    }

    private void SetMeterExpanded(string meterName, bool expanded)
    {
        _meterExpansion[meterName] = expanded;
    }
}
