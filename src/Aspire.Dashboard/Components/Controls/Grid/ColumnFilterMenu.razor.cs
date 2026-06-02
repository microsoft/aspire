// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.Grid;

/// <summary>
/// A reusable column filter component that renders a checkbox list of discrete values.
/// Designed to be placed inside the <c>ColumnOptions</c> render fragment of a <c>FluentDataGrid</c> column.
/// </summary>
public partial class ColumnFilterMenu
{
    /// <summary>
    /// Threshold for showing the search box within the value list.
    /// When there are more values than this, a search box is displayed to help find values.
    /// </summary>
    private const int ValueSearchThreshold = 8;

    private string _searchText = string.Empty;

    /// <summary>
    /// The set of values and their checked (visible) state.
    /// </summary>
    [Parameter, EditorRequired]
    public required ConcurrentDictionary<string, bool> Values { get; set; }

    /// <summary>
    /// Callback invoked when filter state changes (a value is checked/unchecked or all toggled).
    /// </summary>
    [Parameter, EditorRequired]
    public required EventCallback OnFilterChanged { get; set; }

    private IEnumerable<KeyValuePair<string, bool>> GetFilteredValues()
    {
        // OrderBy doesn't use thread-safe APIs on ConcurrentDictionary. Call ToArray first.
        var items = Values.ToArray().OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);

        if (_searchText.Length > 0)
        {
            // Where preserves the existing sort order, so no second OrderBy is needed.
            return items.Where(pair => pair.Key.Contains(_searchText, StringComparison.OrdinalIgnoreCase));
        }

        return items;
    }

    private bool? GetCheckState()
    {
        if (Values.IsEmpty)
        {
            return true;
        }

        var areAllChecked = true;
        var areAllUnchecked = true;

        foreach (var value in Values.Values)
        {
            if (value)
            {
                areAllUnchecked = false;
            }
            else
            {
                areAllChecked = false;
            }
        }

        if (areAllChecked)
        {
            return true;
        }

        if (areAllUnchecked)
        {
            return false;
        }

        return null;
    }

    private async Task OnAllCheckedChangedAsync(bool? newState)
    {
        if (newState is null)
        {
            return;
        }

        foreach (var key in Values.Keys)
        {
            Values[key] = newState.Value;
        }

        await OnFilterChanged.InvokeAsync();
    }

    private async Task OnValueCheckedChangedAsync(string key, bool isChecked)
    {
        Values[key] = isChecked;
        await OnFilterChanged.InvokeAsync();
    }

    private void OnSearchChanged()
    {
        // The search text filters the displayed values in the checkbox list.
        // No callback needed — just re-renders the filtered list.
        StateHasChanged();
    }
}
