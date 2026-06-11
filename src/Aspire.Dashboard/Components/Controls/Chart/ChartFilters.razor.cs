// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartFilters
{
    [Parameter, EditorRequired]
    public required OtlpInstrumentData Instrument { get; set; }

    [Parameter, EditorRequired]
    public required InstrumentViewModel InstrumentViewModel { get; set; }

    [Parameter, EditorRequired]
    public required ImmutableList<DimensionFilterViewModel> DimensionFilters { get; set; }

    [Parameter]
    public EventCallback<DimensionFilterViewModel> OnDimensionValuesChanged { get; set; }

    public bool ShowCounts { get; set; }

    protected override void OnInitialized()
    {
        InstrumentViewModel.DataUpdateSubscriptions.Add(() =>
        {
            ShowCounts = InstrumentViewModel.ShowCount;
            return Task.CompletedTask;
        });
    }

    private void ShowCountChanged()
    {
        InstrumentViewModel.ShowCount = ShowCounts;
    }

    private async Task OnTagSelectionChangedAsync(DimensionFilterViewModel context, DimensionValueViewModel tag, bool isChecked)
    {
        context.OnTagSelectionChanged(tag, isChecked);
        await OnDimensionValuesChanged.InvokeAsync(context);
    }

    private async Task OnAllValuesSelectionChangedAsync(DimensionFilterViewModel context, bool? isChecked)
    {
        context.AreAllValuesSelected = isChecked;
        await OnDimensionValuesChanged.InvokeAsync(context);
    }
}
