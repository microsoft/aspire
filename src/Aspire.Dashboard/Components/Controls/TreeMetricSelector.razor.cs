// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Otlp.Storage;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class TreeMetricSelector
{
    private readonly Dictionary<string, bool> _meterExpansion = new(StringComparer.Ordinal);

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
        StateHasChanged();
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
