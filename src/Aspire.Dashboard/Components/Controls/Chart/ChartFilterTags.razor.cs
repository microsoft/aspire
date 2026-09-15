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

    private const int MaxRenderedOverflowItems = 20;

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

    private Task OnTagClickedAsync(MouseEventArgs args, DimensionValueViewModel tag)
    {
        return OnTagActivatedAsync(tag, args.ShiftKey);
    }

    private async Task OnTagActivatedAsync(DimensionValueViewModel tag, bool toggleSelection)
    {
        if (toggleSelection)
        {
            Filter.OnTagSelectionChanged(tag, !Filter.SelectedValues.Contains(tag));
        }
        else
        {
            Filter.SetSelectedValues([tag]);
        }

        await OnSelectionChanged.InvokeAsync(Filter);
    }

    private void ShowPopover()
    {
        Filter.PopupVisible = true;
        Filter.NotifyStateChanged?.Invoke();
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
