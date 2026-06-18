// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

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

    // when some filter value is selected which is not visible (overflowed)
    // we reorder it to the top of the list. For doing so we use this counter
    // to assign decremental negative number to the Order property of
    // DimensionValueViewModel
    private int _reOrderingCounter;
    private readonly Dictionary<DimensionValueViewModel, int> _originalOrdersByTag = [];

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
        if (isChecked)
        {
            if (context.OverflowedValues.Contains(tag))
            {
                // reorder tag
                _reOrderingCounter++;
                _originalOrdersByTag.TryAdd(tag, tag.Order);
                tag.Order = -_reOrderingCounter;
            }
        }

        context.OnTagSelectionChanged(tag, isChecked);
        if (context.AreAllValuesSelected is true)
        {
            RestoreTagOrders(context.Values);
        }
        else if (!isChecked)
        {
            RestoreTagOrder(tag);
        }

        await OnDimensionValuesChanged.InvokeAsync(context);
    }

    private async Task OnTagKeyDownAsync(KeyboardEventArgs args, DimensionFilterViewModel context, DimensionValueViewModel tag, bool isChecked)
    {
        if (IsActivationKey(args))
        {
            await OnTagSelectionChangedAsync(context, tag, isChecked);
        }
    }

    private static Task OnOverflowTagKeyDownAsync(KeyboardEventArgs args, DimensionFilterViewModel context)
    {
        if (IsActivationKey(args))
        {
            context.PopupVisible = true;
        }

        return Task.CompletedTask;
    }

    private async Task OnAllValuesSelectionChangedAsync(DimensionFilterViewModel context, bool? isChecked)
    {
        context.AreAllValuesSelected = isChecked;
        RestoreTagOrders(context.Values);
        await OnDimensionValuesChanged.InvokeAsync(context);
    }

    private static bool IsActivationKey(KeyboardEventArgs args) => args.Key is "Enter" or " ";

    private void RestoreTagOrders(IEnumerable<DimensionValueViewModel> tags)
    {
        foreach (var tag in tags)
        {
            RestoreTagOrder(tag);
        }
    }

    private void RestoreTagOrder(DimensionValueViewModel tag)
    {
        if (_originalOrdersByTag.Remove(tag, out var originalOrder))
        {
            tag.Order = originalOrder;
        }
    }
}
