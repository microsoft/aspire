// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

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
    private const string KeyForDimensionValue = "dimensionValue";
    private const string KeyForIsIncludedInFilters = "isIncludedInFilters";

    // Maximum number of tags to render in the FluentOverflow. The visible area fits ~5-7 tags;
    // rendering 20 gives FluentOverflow enough items to measure correctly. Items beyond this
    // limit are treated as pre-overflowed and counted in the "+N" badge without being added to
    // the DOM, avoiding hundreds of elements triggering an expensive forced reflow.
    private const int MaxRenderedTags = 20;

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
        Filter.OnTagSelectionChanged(tag, isChecked);
        await OnSelectionChanged.InvokeAsync(Filter);
    }

    private async Task OnTagKeyDownAsync(KeyboardEventArgs args, DimensionValueViewModel tag, bool isChecked)
    {
        if (args.Key is "Enter" or " ")
        {
            await OnTagSelectionChangedAsync(tag, isChecked);
        }
    }

    private void ShowPopover()
    {
        Filter.PopupVisible = true;
        Filter.NotifyStateChanged?.Invoke();
    }

    private Task OnOverflowTagKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key is "Enter" or " ")
        {
            ShowPopover();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Orders dimension values numerically if all values are parsable as doubles;
    /// otherwise orders alphabetically by text.
    /// </summary>
    internal static IEnumerable<DimensionValueViewModel> GetOrderedValues(IReadOnlyList<DimensionValueViewModel> values)
    {
        var parsed = new double[values.Count];
        var allNumeric = true;

        for (var i = 0; i < values.Count; i++)
        {
            if (double.TryParse(values[i].Value, CultureInfo.InvariantCulture, out var d))
            {
                parsed[i] = d;
            }
            else
            {
                allNumeric = false;
                break;
            }
        }

        if (allNumeric)
        {
            return values.Zip(parsed).OrderBy(pair => pair.Second).Select(pair => pair.First);
        }

        return values.OrderBy(v => v.Text, StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Filter.NotifyStateChanged -= OnFilterStateChanged;
    }
}
