// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;
using MetricsResources = Aspire.Dashboard.Resources.Metrics;

namespace Aspire.Dashboard.Components.Controls;

/// <summary>
/// Lists the metric groups a resource reports: the user's pinned instruments, then one group per meter.
/// Selecting a group shows its instruments as cards; a card opens the instrument's chart.
/// </summary>
public partial class MetricGroupList : IDisposable
{
    private static readonly Icon s_pinnedIcon = new Icons.Filled.Size16.Pin();
    private static readonly Icon s_meterIcon = new Icons.Regular.Size16.ChartMultiple();
    // Fluent UI has no 16px AppsList, so the "all" row draws the 20px one at 16px, like the resource list's "All resources" row.
    private static readonly Icon s_allIcon = new Icons.Regular.Size20.AppsList();

    [Parameter, EditorRequired]
    public required Metrics.MetricsViewModel PageViewModel { get; set; }

    [Parameter, EditorRequired]
    public required Func<Metrics.MetricGroupKind, string?, Task> SelectGroupAsync { get; set; }

    [Parameter, EditorRequired]
    public required Func<Metrics.MetricGroupKind, string?, string> GetGroupUrl { get; set; }

    /// <summary>
    /// Raised when the filter text changes, so the page can filter the selected group's cards too.
    /// </summary>
    [Parameter]
    public EventCallback TextFilterChanged { get; set; }

    [Parameter]
    public bool IncludeLabel { get; set; }

    [Inject]
    public required MetricPinStore PinStore { get; init; }

    protected override void OnInitialized()
    {
        PinStore.Changed += OnPinsChanged;
    }

    private void OnPinsChanged()
    {
        _ = InvokeAsync(StateHasChanged);
    }

    public void OnResourceChanged()
    {
        StateHasChanged();
    }

    private Task OnTextFilterChanged() => TextFilterChanged.InvokeAsync();

    // The instruments the "all" row would show: every one of the resource's that matches the filter.
    private int GetAllCount() => PageViewModel.Instruments!.Count(i => MetricFilter.Matches(i, PageViewModel.TextFilter));

    private List<MetricGroup> GetGroups()
    {
        var instruments = PageViewModel.Instruments!;
        var filter = PageViewModel.TextFilter;
        var groups = new List<MetricGroup>();

        // Pins are stored by meter and instrument name across resources, so count this resource's instruments
        // that are pinned rather than the stored pins.
        var pinnedCount = instruments.Count(i => MetricFilter.Matches(i, filter) && PinStore.IsPinned(i.GetKey()));

        // Keep the pinned group while it's selected, even with no matches, so it doesn't disappear under the user.
        if (pinnedCount > 0 || PageViewModel.ShowPinned)
        {
            groups.Add(new MetricGroup(Name: Loc[nameof(MetricsResources.MetricsPinnedHeader)], MeterName: null, IsPinned: true, InstrumentCount: pinnedCount));
        }

        foreach (var meterGroup in instruments.GroupBy(i => i.Parent.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var count = meterGroup.Count(i => MetricFilter.Matches(i, filter));
            if (count > 0)
            {
                groups.Add(new MetricGroup(Name: meterGroup.Key, MeterName: meterGroup.Key, IsPinned: false, InstrumentCount: count));
            }
        }

        return groups;
    }

    private bool IsSelected(MetricGroup group)
    {
        // Filter results and the all-instruments view span every group, so none of them is the current one.
        if (PageViewModel.ShowFilterResults || PageViewModel.ShowAll)
        {
            return false;
        }

        return group.IsPinned
            ? PageViewModel.ShowPinned
            // The meter stays selected while one of its instruments is open, to show where the chart belongs
            // and to let the user go back to the meter's cards.
            : !PageViewModel.ShowPinned && string.Equals(PageViewModel.SelectedMeter, group.MeterName, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        PinStore.Changed -= OnPinsChanged;
    }

    private sealed record MetricGroup(string Name, string? MeterName, bool IsPinned, int InstrumentCount)
    {
        public string Key => IsPinned ? "pinned" : $"meter:{MeterName}";
    }
}
