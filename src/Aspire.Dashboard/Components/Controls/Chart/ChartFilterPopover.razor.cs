// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartFilterPopover : IDisposable
{
    [Parameter, EditorRequired]
    public required DimensionFilterViewModel Filter { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<DimensionFilterViewModel> OnSelectionChanged { get; set; }

    protected override void OnInitialized()
    {
        Filter.NotifyStateChanged += OnFilterStateChanged;
    }

    private void OnFilterStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async Task OnTagSelectionChangedAsync(DimensionValueViewModel tag, bool isChecked)
    {
        Filter.OnTagSelectionChanged(tag, isChecked);
        Filter.NotifyStateChanged?.Invoke();
        await OnSelectionChanged.InvokeAsync(Filter);
    }

    private async Task OnAllValuesSelectionChangedAsync(bool? isChecked)
    {
        Filter.AreAllValuesSelected = isChecked;
        Filter.NotifyStateChanged?.Invoke();
        await OnSelectionChanged.InvokeAsync(Filter);
    }

    public void Dispose()
    {
        Filter.NotifyStateChanged -= OnFilterStateChanged;
    }
}
