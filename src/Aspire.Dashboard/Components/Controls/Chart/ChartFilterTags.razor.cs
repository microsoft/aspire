// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Components;

/// <summary>
/// Renders the FluentOverflow tag list for a single dimension filter row.
/// Owns the inline tag click handlers so that clicking a tag only re-renders
/// this component, not sibling rows in the grid.
/// </summary>
public partial class ChartFilterTags : IDisposable
{
    [Parameter, EditorRequired]
    public required DimensionFilterViewModel Filter { get; set; }

    [Parameter, EditorRequired]
    public required EventCallback<DimensionFilterViewModel> OnSelectionChanged { get; set; }

    // Prevent magic string for dictionary keys
    public const string KeyForDimensionValue = "dimensionValue";
    public const string KeyForIsIncludedInFilters = "isIncludedInFilters";

    // Maximum number of tags to render in the FluentOverflow. The visible area fits ~5-7 tags;
    // rendering 20 gives FluentOverflow enough items to measure correctly. Items beyond this
    // limit are treated as pre-overflowed and counted in the "+N" badge without being added to
    // the DOM, avoiding hundreds of elements triggering an expensive forced reflow.
    private const int MaxRenderedTags = 20;

    // When some filter value is selected which is not visible (overflowed)
    // we reorder it to the top of the list. For doing so we use this counter
    // to assign decremental negative number to the Order property of
    // DimensionValueViewModel
    private int _reOrderingCounter;
    private readonly Dictionary<DimensionValueViewModel, int> _originalOrdersByTag = [];

    protected override void OnInitialized()
    {
        // Subscribe to external state changes (e.g., popover checkbox toggles)
        // so this component re-renders when selections change outside of inline tag clicks.
        Filter.NotifyStateChanged += OnFilterStateChanged;
    }

    private void OnFilterStateChanged()
    {
        InvokeAsync(StateHasChanged);
    }

    private async Task OnTagSelectionChangedAsync(DimensionValueViewModel tag, bool isChecked)
    {
        if (isChecked)
        {
            if (Filter.OverflowedValues.Contains(tag))
            {
                // reorder tag
                _reOrderingCounter++;
                _originalOrdersByTag.TryAdd(tag, tag.Order);
                tag.Order = -_reOrderingCounter;
            }
        }

        Filter.OnTagSelectionChanged(tag, isChecked);
        if (Filter.AreAllValuesSelected is true)
        {
            RestoreTagOrders(Filter.Values);
        }
        else if (!isChecked)
        {
            RestoreTagOrder(tag);
        }

        await OnSelectionChanged.InvokeAsync(Filter);
    }

    private async Task OnTagKeyDownAsync(KeyboardEventArgs args, DimensionValueViewModel tag, bool isChecked)
    {
        if (args.Key is "Enter" or " ")
        {
            await OnTagSelectionChangedAsync(tag, isChecked);
        }
    }

    private Task OnOverflowTagKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key is "Enter" or " ")
        {
            Filter.PopupVisible = true;
        }

        return Task.CompletedTask;
    }

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

    public void HandleOverflowChanged(IEnumerable<FluentOverflowItem> overflowItems)
    {
        var overflowedFromComponent = overflowItems
            .Select(i => (DimensionValueViewModel)i.AdditionalAttributes![KeyForDimensionValue])
            .ToArray();

        // Items beyond MaxRenderedTags are always overflowed since they aren't rendered
        // in the FluentOverflow. Include them so the reordering logic works when a
        // pre-overflowed tag is selected via the popover.
        var preOverflowed = Filter.Values.OrderBy(v => v.Order).Skip(MaxRenderedTags);

        Filter.OverflowedValues = [.. overflowedFromComponent, .. preOverflowed];
    }

    public void Dispose()
    {
        Filter.NotifyStateChanged -= OnFilterStateChanged;
    }
}
