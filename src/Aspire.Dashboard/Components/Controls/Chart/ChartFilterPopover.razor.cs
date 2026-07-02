// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

public partial class ChartFilterPopover : IDisposable
{
    // Stable per-instance id used as the FluentPopover anchor. Composed of the sanitized filter
    // name (for readability when debugging) plus an Identifier.NewId() suffix so multiple
    // ChartFilterPopover instances on the same page never collide.
    private string _anchorId = string.Empty;

    [Parameter, EditorRequired]
    public required DimensionFilterViewModel Filter { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<DimensionFilterViewModel> OnSelectionChanged { get; set; }

    protected override void OnInitialized()
    {
        _anchorId = $"typeFilterButton-{Filter.SanitizedHtmlId}-{Identifier.NewId()}";
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
